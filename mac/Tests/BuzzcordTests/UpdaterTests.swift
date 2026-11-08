import XCTest
@testable import Buzzcord

final class UpdaterTests: XCTestCase {
    typealias V = Updater.Version

    func testVersionParsing() {
        XCTAssertEqual(V("v1.3.0")?.parts, [1, 3, 0])
        XCTAssertEqual(V("1.3")?.parts, [1, 3, 0])
        XCTAssertEqual(V(" V2 ")?.parts, [2, 0, 0])
        XCTAssertEqual(V("1.2.3.4")?.parts, [1, 2, 3])
        XCTAssertNil(V("1.3.0-beta"))      // pre-releases are skipped
        XCTAssertNil(V("1.3.0+build5"))
        XCTAssertNil(V("mac-v1.2.0"))
        XCTAssertNil(V("1..2"))
        XCTAssertNil(V(""))
        XCTAssertNil(V(nil))
        XCTAssertTrue(V("1.10.0")! > V("1.9.9")!)
        XCTAssertTrue(V("v1.3.0")! > V("1.2.0")!)
        XCTAssertFalse(V("1.3")! > V("1.3.0")!)
        XCTAssertEqual(V("v1.3")?.description, "1.3.0")
    }

    private func release(tag: String = "v1.3.0", draft: Bool = false, pre: Bool = false, assets: [String]) -> JObj {
        ["tag_name": tag, "draft": draft, "prerelease": pre, "body": "## New\n**Auto-updates**", "html_url": "https://github.com/mtits/Buzzcord/releases/tag/\(tag)",
         "assets": assets.map { ["name": $0, "browser_download_url": "https://example.com/\($0)"] }]
    }

    func testReleaseParsing() {
        let all = ["Buzzcord-1.3.0-windows.zip", "Buzzcord-1.3.0-windows.zip.sha256", "Buzzcord-1.3.0-mac.zip", "Buzzcord-1.3.0-mac.zip.sha256"]
        let r = Updater.parseRelease(release(assets: all))
        XCTAssertEqual(r?.version.description, "1.3.0")
        XCTAssertEqual(r?.zipUrl, "https://example.com/Buzzcord-1.3.0-mac.zip")
        XCTAssertEqual(r?.shaUrl, "https://example.com/Buzzcord-1.3.0-mac.zip.sha256")
        XCTAssertEqual(r.map(Updater.summary), "New\nAuto-updates")

        // Windows-only release: found, but nothing for the Mac to download.
        let win = Updater.parseRelease(release(assets: Array(all.prefix(2))))
        XCTAssertNotNil(win)
        XCTAssertNil(win?.zipUrl)
        // Another version's zip doesn't count.
        XCTAssertNil(Updater.parseRelease(release(assets: ["Buzzcord-1.2.0-mac.zip"]))?.zipUrl)

        XCTAssertNil(Updater.parseRelease(release(draft: true, assets: all)))
        XCTAssertNil(Updater.parseRelease(release(pre: true, assets: all)))
        XCTAssertNil(Updater.parseRelease(release(tag: "v1.4.0-rc1", assets: all)))
        XCTAssertNil(Updater.parseRelease(nil))
    }

    func testChecksum() {
        let zip = Data("pretend this is a zip".utf8)
        let hash = Updater.sha256(zip)
        XCTAssertEqual(hash.count, 64)
        XCTAssertTrue(Updater.matches(zip, shaFile: Data("\(hash)  Buzzcord-1.3.0-mac.zip\n".utf8)))
        XCTAssertTrue(Updater.matches(zip, shaFile: Data(hash.uppercased().utf8)))
        XCTAssertFalse(Updater.matches(zip + Data([0]), shaFile: Data("\(hash)  Buzzcord-1.3.0-mac.zip".utf8)))
        XCTAssertFalse(Updater.matches(zip, shaFile: Data("\(hash.dropLast())  x".utf8)))
        XCTAssertFalse(Updater.matches(zip, shaFile: Data()))
        XCTAssertFalse(Updater.matches(zip, shaFile: Data(String(repeating: "z", count: 64).utf8)))
    }

    func testUnsafeEntries() {
        XCTAssertEqual(Updater.unsafeEntries(["Buzzcord.app/", "Buzzcord.app/Contents/Info.plist"]), [])
        XCTAssertEqual(Updater.unsafeEntries(["../evil", "/etc/passwd", "a/../../b", "ok/file", "~/x"]), ["../evil", "/etc/passwd", "a/../../b", "~/x"])
    }

    private var tmp: URL!

    override func setUpWithError() throws {
        tmp = FileManager.default.temporaryDirectory.appendingPathComponent("buzzcord-updater-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: tmp) }

    // A minimal Buzzcord.app with a marker file.
    private func fakeApp(_ dir: URL, marker: String) throws -> URL {
        let app = dir.appendingPathComponent("Buzzcord.app", isDirectory: true)
        let macos = app.appendingPathComponent("Contents/MacOS", isDirectory: true)
        try FileManager.default.createDirectory(at: macos, withIntermediateDirectories: true)
        try Data(marker.utf8).write(to: macos.appendingPathComponent("Buzzcord"))
        return app
    }

    private func marker(_ app: URL) -> String? {
        (try? Data(contentsOf: app.appendingPathComponent("Contents/MacOS/Buzzcord"))).map { String(decoding: $0, as: UTF8.self) }
    }

    func testUnpackGoodZipAndRefuseEscapes() throws {
        let src = tmp.appendingPathComponent("src", isDirectory: true)
        let app = try fakeApp(src, marker: "new")
        let zipURL = tmp.appendingPathComponent("good.zip")
        XCTAssertEqual(Updater.run("/usr/bin/ditto", ["-c", "-k", "--keepParent", app.path, zipURL.path]), 0)
        let out = try Updater.unpack(Data(contentsOf: zipURL), into: tmp.appendingPathComponent("update/1.3.0", isDirectory: true))
        XCTAssertEqual(marker(out), "new")

        // A link inside the app pointing outside the folder.
        try FileManager.default.createSymbolicLink(atPath: app.appendingPathComponent("Contents/escape").path, withDestinationPath: "/etc")
        let linkZip = tmp.appendingPathComponent("link.zip")
        XCTAssertEqual(Updater.run("/usr/bin/ditto", ["-c", "-k", "--keepParent", app.path, linkZip.path]), 0)
        XCTAssertThrowsError(try Updater.unpack(Data(contentsOf: linkZip), into: tmp.appendingPathComponent("update/link", isDirectory: true)))

        // An entry named ../evil (written with Python's zipfile, which allows it).
        let evilZip = tmp.appendingPathComponent("evil.zip")
        XCTAssertEqual(Updater.run("/usr/bin/python3", ["-I", "-c", "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'w'); z.writestr('Buzzcord.app/Contents/MacOS/Buzzcord','x'); z.writestr('../evil','x'); z.close()", evilZip.path]), 0)
        let target = tmp.appendingPathComponent("update/target", isDirectory: true)
        XCTAssertThrowsError(try Updater.unpack(Data(contentsOf: evilZip), into: target))
        XCTAssertFalse(FileManager.default.fileExists(atPath: tmp.appendingPathComponent("update/evil").path))   // where ../evil would land

        XCTAssertThrowsError(try Updater.unpack(Data("not a zip".utf8), into: tmp.appendingPathComponent("update/junk", isDirectory: true)))
    }

    func testSwapBundle() throws {
        let live = try fakeApp(tmp.appendingPathComponent("Applications", isDirectory: true), marker: "old")
        let staged = try fakeApp(tmp.appendingPathComponent("update/1.3.0", isDirectory: true), marker: "new")
        let aside = try Updater.swapBundle(at: live, with: staged)
        XCTAssertEqual(marker(live), "new")
        XCTAssertEqual(marker(aside), "old")
        XCTAssertEqual(aside.deletingLastPathComponent().standardizedFileURL, live.deletingLastPathComponent().standardizedFileURL)
        XCTAssertTrue(aside.lastPathComponent.hasPrefix(".Buzzcord-old-"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: staged.path))
    }

    func testSwapBundleRollsBack() throws {
        let folder = tmp.appendingPathComponent("Applications", isDirectory: true)
        let live = try fakeApp(folder, marker: "old")
        let missing = tmp.appendingPathComponent("update/gone/Buzzcord.app", isDirectory: true)
        XCTAssertThrowsError(try Updater.swapBundle(at: live, with: missing))
        XCTAssertEqual(marker(live), "old")   // put back
        let left = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        XCTAssertEqual(left, ["Buzzcord.app"])   // nothing left aside
    }

    func testWhereItCanInstall() throws {
        XCTAssertNil(Updater.cannotInstall(try fakeApp(tmp, marker: "x")))
        XCTAssertNotNil(Updater.cannotInstall(URL(fileURLWithPath: "/private/var/folders/xy/T/AppTranslocation/ABC/d/Buzzcord.app")))
        let downloads = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads/Buzzcord.app")
        XCTAssertNotNil(Updater.cannotInstall(downloads))
        XCTAssertNotNil(Updater.cannotInstall(URL(fileURLWithPath: "/System/Applications/Buzzcord.app")))   // not writable
    }

    func testDonateQRsAreInTheRepo() {
        for q in DonateQR.allCases {
            let png = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
                .appendingPathComponent("../../../assets/donate/\(q.rawValue).png").standardizedFileURL
            XCTAssertNotNil(NSImage(contentsOf: png), "missing \(png.path)")
        }
    }
}
