import AppKit
import Foundation

// Discord's upload limits, and the checks Buzzcord makes before it attaches or sends a file.
// Buzzcord never splits, shrinks or re-encodes a file to fit, and never retries a file Discord
// refused: Discord's limit is the limit.
enum UploadLimits {
    // Sources, checked 2026-11-10:
    //  - support.discord.com "File Attachments FAQ" (article 25444343291031): 20MB without Nitro
    //    ("As of August 2026 ... up from 10MB"), Nitro Basic 50MB, Nitro up to 1GB.
    //  - support.discord.com "What are Nitro & Nitro Basic?" (115000435108): Nitro Classic 50MB.
    //  - support.discord.com "Server Boosting FAQ" (360028038352): Level 2 50MB, Level 3 100MB,
    //    "for all members (server only)". Levels 0 and 1 add nothing.
    //  - docs.discord.com/developers/reference "Uploading Files": the limit "applies to each file",
    //    and is given in MiB (default 20 MiB). premium_type and premium_tier values from the
    //    User and Guild resource pages.
    //  - 10 files per message: what Discord's own app allows; the help center doesn't state it.
    // Discord also runs experiments (a 250MB server add-on, larger limits for some users) that
    // nothing in the API shows. If Discord refuses a file anyway, its answer is final.
    static let mb: Int64 = 1_048_576
    static let free: Int64 = 20 * mb
    static let nitroClassic: Int64 = 50 * mb   // premium_type 1 (no longer sold)
    static let nitro: Int64 = 1024 * mb        // premium_type 2
    static let nitroBasic: Int64 = 50 * mb     // premium_type 3
    static let boostLevel2: Int64 = 50 * mb    // guild premium_tier 2
    static let boostLevel3: Int64 = 100 * mb   // guild premium_tier 3
    static let maxFiles = 10

    // The limit that comes with the account (READY user.premium_type).
    static func account(premiumType: Int) -> Int64 {
        switch premiumType {
        case 1: return nitroClassic
        case 2: return nitro
        case 3: return nitroBasic
        default: return free
        }
    }

    // The limit a server's boost level gives everyone in it (guild premium_tier).
    static func server(tier: Int) -> Int64 {
        switch tier {
        case 3: return boostLevel3
        case 2: return boostLevel2
        default: return free
        }
    }

    // In a server channel the larger of the two; in DMs and group chats (guildTier nil) the
    // account's own limit.
    static func effective(premiumType: Int, guildTier: Int?) -> Int64 {
        let a = account(premiumType: premiumType)
        guard let guildTier else { return a }
        return max(a, server(tier: guildTier))
    }
}

// Where a file is being sent, as far as the limit goes.
struct UploadContext: Equatable {
    var premiumType = 0
    var guildTier: Int?   // nil in DMs and group chats

    var limit: Int64 { UploadLimits.effective(premiumType: premiumType, guildTier: guildTier) }
}

enum Uploads {
    // "84 MB", "9.6 MB", "820 KB", "1 GB". roundUp keeps a file just over the limit from
    // reading as exactly the limit.
    static func size(_ bytes: Int64, roundUp: Bool = false) -> String {
        let mb = Double(bytes) / Double(UploadLimits.mb)
        if mb >= 1024 { return trim(round1(mb / 1024, roundUp)) + " GB" }
        if mb >= 100 { return String(Int(roundUp ? mb.rounded(.up) : mb.rounded())) + " MB" }
        if mb >= 1 { return trim(round1(mb, roundUp)) + " MB" }
        return String(max(1, Int((Double(bytes) / 1024).rounded(roundUp ? .up : .toNearestOrAwayFromZero)))) + " KB"
    }

    private static func round1(_ v: Double, _ up: Bool) -> Double { up ? (v * 10).rounded(.up) / 10 : (v * 10).rounded() / 10 }
    private static func trim(_ v: Double) -> String {
        v == v.rounded() ? String(Int(v)) : String(format: "%.1f", v)
    }

    // "You can send up to 20 MB here (50 MB with Nitro Basic, 1 GB with Nitro, or 50-100 MB in
    // a boosted server)." Worded for the account and the chat.
    static func limitSentence(_ ctx: UploadContext) -> String {
        let limit = ctx.limit
        let account = UploadLimits.account(premiumType: ctx.premiumType)
        var s = "You can send up to " + size(limit)
        if let tier = ctx.guildTier, UploadLimits.server(tier: tier) > account {
            s += " in this server, thanks to its Level \(tier) boost"
        } else {
            switch ctx.premiumType {
            case 1: s += " with Nitro Classic"
            case 2: s += " with Nitro"
            case 3: s += " with Nitro Basic"
            default: s += " here"
            }
        }
        var more: [String] = []
        if account < UploadLimits.nitroBasic && UploadLimits.nitroBasic > limit { more.append(size(UploadLimits.nitroBasic) + " with Nitro Basic") }
        if account < UploadLimits.nitro && UploadLimits.nitro > limit { more.append(size(UploadLimits.nitro) + " with Nitro") }
        if let tier = ctx.guildTier {
            let l2 = tier < 2 && UploadLimits.boostLevel2 > limit, l3 = tier < 3 && UploadLimits.boostLevel3 > limit
            if l2 && l3 {
                more.append("\(Int(UploadLimits.boostLevel2 / UploadLimits.mb))-\(size(UploadLimits.boostLevel3)) in a boosted server")
            } else if l3 {
                more.append(size(UploadLimits.boostLevel3) + " if this server reaches boost Level 3")
            }
        }
        if more.isEmpty { return s + ", the most Discord allows." }
        return s + " (" + list(more, "or") + ")."
    }

    // Said when a file over the limit is attached; it isn't attached.
    static func tooBig(_ name: String, _ bytes: Int64, _ ctx: UploadContext) -> String {
        "\(name) is \(size(bytes, roundUp: true)). " + limitSentence(ctx)
    }

    // Discord refused an upload as too large (413, or error 40005 / 50045). Not retried.
    static func refused(_ ctx: UploadContext) -> String {
        "Discord refused the file as too big, so nothing was sent. " + limitSentence(ctx)
    }

    static func tooMany(_ names: [String]) -> String {
        let who: String
        switch names.count {
        case 1: who = names[0] + " wasn't"
        case 2: who = names[0] + " and " + names[1] + " weren't"
        default: who = names[0] + " and \(names.count - 1) more weren't"
        }
        return "Discord allows \(UploadLimits.maxFiles) files per message, so \(who) attached. Send these first, then add the rest."
    }

    static func list(_ items: [String], _ conj: String) -> String {
        switch items.count {
        case 0: return ""
        case 1: return items[0]
        case 2: return items[0] + " " + conj + " " + items[1]
        default: return items.dropLast().joined(separator: ", ") + ", " + conj + " " + items.last!
        }
    }

    struct Candidate {
        var name: String
        var size: Int64
        var isFolder = false
    }

    struct CheckResult: Equatable {
        var accepted: [Int] = []   // indexes into the candidates
        var notes: [String] = []
    }

    // Which of the candidates can be attached next to `existing` files already in the tray, and
    // what to tell the user about the rest.
    static func check(existing: Int, _ files: [Candidate], _ ctx: UploadContext) -> CheckResult {
        var r = CheckResult()
        var fits: [Int] = []
        for (i, f) in files.enumerated() {
            if f.isFolder { r.notes.append("\(f.name) is a folder. Zip it first to send it."); continue }
            if f.size > ctx.limit { r.notes.append(tooBig(f.name, f.size, ctx)); continue }
            fits.append(i)
        }
        let room = max(0, UploadLimits.maxFiles - existing)
        r.accepted = Array(fits.prefix(room))
        if fits.count > room { r.notes.append(tooMany(fits.dropFirst(room).map { files[$0].name })) }
        return r
    }

    // A note for the tray when a file is close to the limit (or over it, if the limit dropped
    // after it was attached), else nil.
    static func trayWarning(_ files: [Candidate], _ ctx: UploadContext) -> (text: String, over: Bool)? {
        let limit = ctx.limit
        if let f = files.first(where: { $0.size > limit }) {
            return ("\(f.name) is over the \(size(limit)) limit here. Remove it to send.", true)
        }
        if let f = files.first(where: { Double($0.size) >= Double(limit) * 0.9 }) {
            return ("\(f.name) is close to the \(size(limit)) limit here.", false)
        }
        return nil
    }
}

// What to do with the pasteboard on Paste. Pure, so it can be tested with plain type lists.
enum PastePick {
    static let fileURL = "public.file-url"
    static let plainText = "public.utf8-plain-text"
    static let rtf = "public.rtf"
    static let tiff = "public.tiff"

    // Pasted data kept in the format it came in, best first: animation and the original
    // compression survive. TIFF (what a lot of Mac apps copy) is made into PNG, since Discord
    // doesn't show TIFF.
    static let kept: [(type: String, ext: String)] = [
        ("com.compuserve.gif", "gif"),
        ("org.webmproject.webp", "webp"),
        ("public.jpeg", "jpg"),
        ("public.png", "png"),
        ("public.mpeg-4", "mp4"),
        ("com.apple.quicktime-movie", "mov"),
    ]

    struct Item: Equatable {
        var type: String   // the pasteboard type to read
        var ext: String    // the file extension to save it with
        var toPng = false  // TIFF: convert to PNG
    }

    enum Choice: Equatable {
        case files          // file URLs (Finder copies): attach those files
        case data([Item])   // one item per pasteboard item with picture or video data
        case imageFallback  // something NSImage can read (PDF, HEIC...): attach as PNG
        case text
    }

    static func item(_ types: [String]) -> Item? {
        for k in kept where types.contains(k.type) { return Item(type: k.type, ext: k.ext) }
        if types.contains(tiff) { return Item(type: tiff, ext: "png", toPng: true) }
        return nil
    }

    // items: each pasteboard item's types. text: the pasteboard's plain text, if any.
    // canMakeImage: NSImage can read the pasteboard (only asked when nothing else fits).
    static func choose(_ items: [[String]], text: String?, canMakeImage: () -> Bool = { false }) -> Choice {
        let all = items.flatMap { $0 }
        if all.contains(fileURL) { return .files }
        // Copied text stays text, even when the app put a picture of it alongside (Notes, Excel,
        // Pages): real words, or anything copied with rich text. A browser's "Copy Image" puts
        // only the picture's address there, which isn't text the user meant to paste.
        if let text, !isJustLink(text) || all.contains(rtf) { return .text }
        let picked = items.compactMap(item)
        if !picked.isEmpty { return .data(picked) }
        if text == nil && canMakeImage() { return .imageFallback }
        return .text
    }

    // Browsers put the picture's address next to a copied picture; that alone isn't text the
    // user meant to paste.
    static func isJustLink(_ s: String) -> Bool {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty { return true }
        if t.contains(where: { $0.isWhitespace }) { return false }
        guard let u = URL(string: t), let scheme = u.scheme?.lowercased() else { return false }
        return ["http", "https", "file", "data", "blob"].contains(scheme)
    }

    // Writes the chosen items to files in the temporary folder and returns them.
    @MainActor
    static func save(_ pb: NSPasteboard, _ items: [Item]) -> [URL] {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Buzzcord", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd-HHmmss"
        let stamp = f.string(from: Date())
        var urls: [URL] = []
        let pbItems = pb.pasteboardItems ?? []
        var n = 0
        for it in pbItems {
            let types = it.types.map(\.rawValue)
            guard let want = item(types), items.contains(want), var data = it.data(forType: NSPasteboard.PasteboardType(want.type)) else { continue }
            if want.toPng {
                guard let png = NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:]) else { continue }
                data = png
            }
            n += 1
            let base = (["mp4", "mov"].contains(want.ext) ? "video-" : "image-") + stamp + (n > 1 ? "-\(n)" : "")
            let url = dir.appendingPathComponent(base + "." + want.ext)
            if (try? data.write(to: url)) != nil { urls.append(url) }
        }
        return urls
    }

    @MainActor
    static func saveImageFallback(_ pb: NSPasteboard) -> URL? {
        guard let img = NSImage(pasteboard: pb), let tiff = img.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { return nil }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Buzzcord", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd-HHmmss"
        let url = dir.appendingPathComponent("image-\(f.string(from: Date())).png")
        return (try? png.write(to: url)) != nil ? url : nil
    }
}
