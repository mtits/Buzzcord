import AppKit
import CryptoKit
import Foundation

// Updates from GitHub Releases, like the Windows Updater.cs. The newest release that has a Mac
// zip is downloaded in the background, checked against the SHA-256 file published beside it, and
// unpacked with ditto into ~/Library/Application Support/Buzzcord/update/<version>. Installing
// swaps the .app bundle in place: the running one is moved aside, the new one moved in (put
// back if anything fails), the new copy is started with --updated, and it deletes the old one.
//
// URLSession downloads carry no quarantine flag, so the new app doesn't either; nothing here
// adds one.
enum Updater {
    static let repo = "mtits/Buzzcord"
    static let releasesUrl = "https://github.com/\(repo)/releases"
    // The list, not /releases/latest: a release can be for one platform only (v1.3.2 was Mac only),
    // and GitHub's "latest" would then hide the other platform's newest release.
    static let listApi = "https://api.github.com/repos/\(repo)/releases?per_page=20"

    // End-to-end check only: a copy whose bundle id ends in ".updatetest" (so it has its own
    // settings) can read a local feed and runs without signing in. See mac/README.md.
    static var testMode: String? {
        guard Bundle.main.bundleIdentifier?.hasSuffix(".updatetest") == true else { return nil }
        return Prefs.string("UpdateTestMode")
    }
    static var feed: String { testMode != nil ? (Prefs.string("UpdateFeedURL") ?? listApi) : listApi }

    struct Version: Comparable, CustomStringConvertible {
        let parts: [Int]   // always 3: major, minor, patch

        // "v1.3.0" -> 1.3.0, "1.3" -> 1.3.0. Nil for pre-releases ("1.3.0-beta") and non-versions.
        init?(_ tag: String?) {
            guard var t = tag?.trimmingCharacters(in: .whitespaces), !t.isEmpty else { return nil }
            if t.first == "v" || t.first == "V" { t.removeFirst() }
            if t.contains("-") || t.contains("+") { return nil }
            let bits = t.split(separator: ".", omittingEmptySubsequences: false)
            guard (1...4).contains(bits.count) else { return nil }
            var p: [Int] = []
            for b in bits {
                guard !b.isEmpty, b.allSatisfy(\.isASCII), let n = Int(b), n >= 0 else { return nil }
                p.append(n)
            }
            parts = Array((p + [0, 0, 0]).prefix(3))
        }

        static func < (a: Version, b: Version) -> Bool { a.parts.lexicographicallyPrecedes(b.parts) }
        var description: String { parts.map(String.init).joined(separator: ".") }
    }

    struct Release {
        let version: Version
        let tag: String
        let notes: String
        let pageUrl: String
        var zipUrl: String?
        var shaUrl: String?
    }

    static var current: Version {
        Version(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? Version("0.0.0")!
    }

    // Reads one release from GET /repos/{repo}/releases. Nil for drafts, pre-releases and odd tags.
    static func parseRelease(_ j: JObj?) -> Release? {
        guard let j, !j.bool("draft"), !j.bool("prerelease"), let tag = j.str("tag_name"), let v = Version(tag) else { return nil }
        var r = Release(version: v, tag: tag, notes: j.str("body") ?? "", pageUrl: j.str("html_url") ?? releasesUrl)
        // The tag's own text ("1.3.0"), so Buzzcord-1.3.0-mac.zip matches exactly.
        let name = "Buzzcord-\(tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag)-mac.zip"
        for a in j.objs("assets") {
            switch a.str("name") {
            case name: r.zipUrl = a.str("browser_download_url")
            case name + ".sha256": r.shaUrl = a.str("browser_download_url")
            default: break
            }
        }
        return r
    }

    // The highest version that has a Mac zip and its checksum. Takes the release list, or one
    // release (older test feeds). GitHub lists by date, not version, so every entry is compared.
    static func newestForMac(_ feed: Any?) -> Release? {
        let list = (feed as? [JObj]) ?? (feed as? JObj).map { [$0] } ?? []
        return list.compactMap(parseRelease).filter { $0.zipUrl != nil && $0.shaUrl != nil }.max { $0.version < $1.version }
    }

    static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    // The .sha256 file is "<64 hex>  Buzzcord-x-mac.zip" (shasum's format); only the hash counts.
    static func matches(_ zip: Data, shaFile: Data) -> Bool {
        let first = String(decoding: shaFile, as: UTF8.self).split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "\n" || $0 == "\r" }).first
        guard let expected = first?.lowercased(), expected.count == 64, expected.allSatisfy(\.isHexDigit) else { return false }
        return sha256(zip) == expected
    }

    // Zip entry names that would land outside the folder they're unpacked into.
    static func unsafeEntries(_ names: [String]) -> [String] {
        names.filter { n in
            n.hasPrefix("/") || n.hasPrefix("~") || n.contains("\\") || n.split(separator: "/").contains("..")
        }
    }

    private static func session() -> URLSession {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 60
        c.timeoutIntervalForResource = 300
        c.httpAdditionalHeaders = [
            "User-Agent": "Buzzcord/\(current) (Macintosh; macOS) +https://github.com/\(repo)",
            "Accept": "application/vnd.github+json",
        ]
        return URLSession(configuration: c)
    }

    private static func get(_ url: String) async throws -> Data {
        guard let u = URL(string: url) else { throw UpdateError("Bad address: \(url)") }
        let (data, resp) = try await session().data(from: u)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 else { throw UpdateError("GitHub answered \(code).") }
        return data
    }

    // The newest release with a Mac zip if it's newer than this copy, else nil.
    static func check() async throws -> Release? {
        guard let r = newestForMac(Json.parse(try await get(feed))) else { return nil }
        return r.version > current ? r : nil
    }

    static var root: URL { AppPaths.support.appendingPathComponent("update", isDirectory: true) }

    // Downloads, verifies and unpacks the release; returns the new Buzzcord.app.
    @discardableResult
    static func download(_ r: Release) async throws -> URL {
        guard let zipUrl = r.zipUrl, let shaUrl = r.shaUrl else { throw UpdateError("The release has no checksum file, so it can't be verified.") }
        let zip = try await get(zipUrl)
        let sha = try await get(shaUrl)
        guard matches(zip, shaFile: sha) else { throw UpdateError("The download didn't match its published checksum, so it was thrown away.") }
        let app = try unpack(zip, into: root.appendingPathComponent(r.version.description, isDirectory: true))
        guard let v = bundleVersion(app), v == r.version, Bundle(url: app)?.bundleIdentifier == Bundle.main.bundleIdentifier || Bundle.main.bundleIdentifier == nil
        else { throw UpdateError("The download isn't Buzzcord \(r.version).") }
        guard run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]) == 0 else { throw UpdateError("The downloaded app's signature doesn't check out.") }
        Prefs.set("UpdateStaged", app.path)
        Prefs.set("UpdateStagedVersion", r.version.description)
        return app
    }

    // Unzips with ditto into dir (replacing it). Refuses archives with paths, or links, that
    // would reach outside dir. Returns dir/Buzzcord.app.
    static func unpack(_ zip: Data, into dir: URL) throws -> URL {
        let fm = FileManager.default
        try? fm.removeItem(at: dir)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let tmp = dir.deletingLastPathComponent().appendingPathComponent(dir.lastPathComponent + ".zip")
        try zip.write(to: tmp)
        defer { try? fm.removeItem(at: tmp) }
        let list = Process.output("/usr/bin/zipinfo", ["-1", tmp.path]) ?? ""
        let names = list.split(separator: "\n").map(String.init)
        guard !names.isEmpty else { throw UpdateError("The download isn't a zip file.") }
        if let bad = unsafeEntries(names).first { throw UpdateError("The download has a bad file path: \(bad)") }
        guard run("/usr/bin/ditto", ["-x", "-k", tmp.path, dir.path]) == 0 else { throw UpdateError("Couldn't unzip the download.") }
        try checkInside(dir)
        let app = dir.appendingPathComponent("Buzzcord.app", isDirectory: true)
        guard fm.fileExists(atPath: app.appendingPathComponent("Contents/MacOS/Buzzcord").path) else { throw UpdateError("The download has no Buzzcord.app in it.") }
        return app
    }

    // Every file under dir, links resolved, stays under dir.
    static func checkInside(_ dir: URL) throws {
        let base = dir.resolvingSymlinksInPath().standardizedFileURL.path + "/"
        let e = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.isSymbolicLinkKey])
        while let u = e?.nextObject() as? URL {
            let real = u.resolvingSymlinksInPath().standardizedFileURL.path
            if !real.hasPrefix(base) { throw UpdateError("The download has a link pointing outside its folder: \(u.lastPathComponent)") }
        }
    }

    static func bundleVersion(_ app: URL) -> Version? {
        guard let d = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) else { return nil }
        return Version(d["CFBundleShortVersionString"] as? String)
    }

    // A downloaded update waiting to be installed, newer than this copy; else nil.
    static var staged: URL? {
        guard let path = Prefs.string("UpdateStaged"), !path.isEmpty,
              let v = Version(Prefs.string("UpdateStagedVersion")), v > current else { return nil }
        let app = URL(fileURLWithPath: path)
        return FileManager.default.fileExists(atPath: app.appendingPathComponent("Contents/MacOS/Buzzcord").path) ? app : nil
    }

    static var stagedVersion: String { Prefs.string("UpdateStagedVersion") ?? "" }

    static func clearStaged() {
        Prefs.set("UpdateStaged", "")
    }

    // Why this copy can't replace itself, or nil when it can. Gatekeeper runs apps opened
    // straight from Downloads or a disk image from a read-only, randomised copy (App
    // Translocation), so there's nothing to swap.
    static func cannotInstall(_ app: URL = Bundle.main.bundleURL) -> String? {
        let path = app.path
        let move = "Move Buzzcord to your Applications folder and open it from there, so it can update itself."
        if path.contains("/AppTranslocation/") { return "Buzzcord is running from a temporary read-only copy that macOS made. " + move }
        if let ro = try? app.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly, ro { return "Buzzcord is running from a read-only disk. " + move }
        if path.hasPrefix(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads").path + "/") { return "Buzzcord is running from your Downloads folder. " + move }
        if !FileManager.default.isWritableFile(atPath: app.deletingLastPathComponent().path) { return "Buzzcord can't write to the folder it's in. " + move }
        return nil
    }

    // Puts `staged` where `current` is. The current bundle is moved aside first (same folder, so
    // it's a rename); if the new one can't be moved in, the old one goes back. Returns where the
    // old bundle now is, for the new copy to delete.
    static func swapBundle(at current: URL, with staged: URL, fm: FileManager = .default) throws -> URL {
        let aside = current.deletingLastPathComponent()
            .appendingPathComponent(".\(current.deletingPathExtension().lastPathComponent)-old-\(Int(Date().timeIntervalSince1970)).app", isDirectory: true)
        try fm.moveItem(at: current, to: aside)
        do {
            try fm.moveItem(at: staged, to: current)
        } catch {
            try? fm.removeItem(at: current)   // half-copied, if the move was across disks
            do { try fm.moveItem(at: aside, to: current) } catch { Log.write("update: rollback failed: \(error)") }
            throw error
        }
        return aside
    }

    // Installs the staged update and starts the new copy once this one has quit. The caller quits
    // right after.
    static func installAndRelaunch() throws {
        guard let app = staged else { throw UpdateError("No update is ready to install.") }
        if let why = cannotInstall() { throw UpdateError(why) }
        let here = Bundle.main.bundleURL
        let version = stagedVersion
        let old = try swapBundle(at: here, with: app)
        clearStaged()
        Prefs.set("UpdateOldCopy", old.path)
        Prefs.set("UpdatedFrom", current.description)
        Log.write("update: installed \(version) over \(current)")
        // Wait for this process to end, then open the new bundle.
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while /bin/kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do /bin/sleep 0.2; done; /usr/bin/open -n \"$1\" --args --updated", "sh", here.path]
        try p.run()
    }

    // After --updated: delete the old bundle and the staging folder.
    static func cleanUp() {
        let fm = FileManager.default
        if let old = Prefs.string("UpdateOldCopy"), !old.isEmpty {
            let u = URL(fileURLWithPath: old)
            // Only ever a hidden "-old-" bundle next to this one.
            if u.lastPathComponent.hasPrefix("."), u.lastPathComponent.contains("-old-"), u.pathExtension == "app",
               u.deletingLastPathComponent().standardizedFileURL == Bundle.main.bundleURL.deletingLastPathComponent().standardizedFileURL {
                do { try fm.removeItem(at: u); Log.write("update: removed \(u.lastPathComponent)") } catch { Log.write("update: couldn't remove old copy: \(error)") }
            }
            Prefs.set("UpdateOldCopy", "")
        }
        if staged == nil { try? fm.removeItem(at: root) }
    }

    // Short release notes for a dialog: the first few lines.
    static func summary(_ r: Release) -> String {
        var lines: [String] = []
        for raw in r.notes.replacingOccurrences(of: "\r", with: "").split(separator: "\n") {
            var l = raw.trimmingCharacters(in: .whitespaces)
            while l.hasPrefix("#") || l.hasPrefix(">") { l = String(l.dropFirst()).trimmingCharacters(in: .whitespaces) }
            l = l.replacingOccurrences(of: "**", with: "")
            if l.isEmpty || l.hasPrefix("[!") { continue }
            lines.append(l)
            if lines.count == 8 { lines.append("..."); break }
        }
        return lines.joined(separator: "\n")
    }

    @discardableResult
    static func run(_ tool: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }
}

struct UpdateError: LocalizedError {
    let message: String
    init(_ m: String) { message = m }
    var errorDescription: String? { message }
}

extension Process {
    static func output(_ tool: String, _ args: [String]) -> String? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return p.terminationStatus == 0 ? String(decoding: data, as: UTF8.self) : nil
    }
}

// The timer, banners, menu item and dialogs around Updater (Windows: Program.CheckForUpdates).
@MainActor
final class UpdateController {
    static let shared = UpdateController()
    static let notifyKey = "__update__"

    var onChange: () -> Void = {}   // the menu bar menu's "Restart to update" item
    private var timer: Timer?
    private var checking = false
    private var noticedPage: String?   // the release a "Buzzcord X is out" banner is about

    static var automatic: Bool { Prefs.bool("AutoUpdate", true) }

    // First look 20 s after start, then every 6 hours.
    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 20, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.check(manual: false)
                self?.timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { _ in
                    MainActor.assumeIsolated { UpdateController.shared.check(manual: false) }
                }
            }
        }
    }

    // A ready update not installed last time goes in now, before any window opens. True when
    // Buzzcord is restarting (the caller does nothing else).
    func installPendingAtLaunch() -> Bool {
        guard !CommandLine.arguments.contains("--updated"), Self.automatic, Updater.staged != nil, Updater.cannotInstall() == nil else { return false }
        do {
            try Updater.installAndRelaunch()
            NSApp.terminate(nil)
            return true
        } catch {
            Log.write("update install at launch: \(error.localizedDescription)")
            Updater.clearStaged()
            return false
        }
    }

    // Just restarted after an update: clean up and say so.
    func afterLaunch() {
        guard let from = Prefs.string("UpdatedFrom"), !from.isEmpty else {
            if !CommandLine.arguments.contains("--updated") { return }
            Updater.cleanUp()
            return
        }
        Prefs.set("UpdatedFrom", "")
        let old = Prefs.string("UpdateOldCopy") ?? ""
        Updater.cleanUp()
        if Updater.testMode != nil {
            Log.write("update-test: now \(Updater.current) at \(Bundle.main.bundlePath), updated from \(from), old copy gone: \(!FileManager.default.fileExists(atPath: old))")
            NSApp.terminate(nil)
            return
        }
        Notifier.shared.alert(account: Self.notifyKey, channel: "notes", title: "Buzzcord is up to date",
                              body: "Updated from \(from) to \(Updater.current). Click to see what's new.", sound: nil)
    }

    // manual: Help > Check for Updates, which always answers. The timer stays quiet unless
    // there's news, and only runs while automatic updates are on.
    func check(manual: Bool) {
        guard !checking, manual || Self.automatic else { return }
        checking = true
        Task { @MainActor in
            defer { checking = false }
            do {
                if Updater.staged != nil { ready(manual: manual, release: nil); return }
                guard let r = try await Updater.check() else {
                    if manual { info("You have the latest Buzzcord (\(Updater.current)).") }
                    return
                }
                if let why = Updater.cannotInstall() {
                    // Can't replace itself here: say so, once per release unless asked.
                    if manual {
                        if confirm("Buzzcord \(r.version) is out.\n\n\(why)", ok: "Open Download Page") { openExternal(r.pageUrl) }
                    } else if Prefs.string("UpdateNoticed") != r.tag {
                        Prefs.set("UpdateNoticed", r.tag)
                        noticedPage = r.pageUrl
                        Notifier.shared.alert(account: Self.notifyKey, channel: "page", title: "Buzzcord \(r.version) is out", body: why, sound: nil)
                    }
                    return
                }
                try await Updater.download(r)
                Log.write("update: \(r.tag) downloaded and verified")
                ready(manual: manual, release: r)
            } catch {
                Log.write("update: \(error.localizedDescription)")
                if manual { showError("Couldn't check for updates: \(error.localizedDescription)") }
            }
        }
    }

    private func ready(manual: Bool, release: Updater.Release?) {
        onChange()
        if let t = Updater.testMode {
            Log.write("update-test: \(Updater.stagedVersion) staged at \(Updater.staged?.path ?? "-")")
            if t == "now" { install() } else { NSApp.terminate(nil) }
            return
        }
        let v = Updater.stagedVersion
        if manual {
            let notes = release.map(Updater.summary) ?? ""
            let a = NSAlert()
            a.messageText = "Buzzcord \(v) is ready to install."
            a.informativeText = (notes.isEmpty ? "" : notes + "\n\n") + "Restart now to update? Otherwise it installs the next time Buzzcord starts."
            a.addButton(withTitle: "Restart to Update")
            a.addButton(withTitle: "Later")
            if a.runModal() == .alertFirstButtonReturn { install() }
            return
        }
        Notifier.shared.alert(account: Self.notifyKey, channel: "install", title: "Buzzcord \(v) is ready",
                              body: "Click to restart and update now, or it installs the next time Buzzcord starts.", sound: nil)
    }

    // Swaps in the new bundle and restarts. Buzzcord keeps running if that fails.
    func install() {
        do {
            try Updater.installAndRelaunch()
            NSApp.terminate(nil)
        } catch {
            Log.write("update install: \(error.localizedDescription)")
            Updater.clearStaged()
            onChange()
            showError("Couldn't install the update: \(error.localizedDescription)\n\nYou can download it from \(Updater.releasesUrl).")
        }
    }

    // A banner from this controller was clicked.
    func clicked(_ what: String) {
        switch what {
        case "install": if Updater.staged != nil { install() }
        default: openExternal(noticedPage ?? Updater.releasesUrl)   // "page"
        }
    }

    private func info(_ text: String) {
        let a = NSAlert()
        a.messageText = text
        a.runModal()
    }
}
