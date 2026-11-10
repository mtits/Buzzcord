import XCTest
@testable import Buzzcord

final class UploadTests: XCTestCase {
    private let mb = UploadLimits.mb

    // MARK: - the limits table and which limit applies

    func testTable() {
        XCTAssertEqual(UploadLimits.account(premiumType: 0), 20 * mb)
        XCTAssertEqual(UploadLimits.account(premiumType: 1), 50 * mb)     // Nitro Classic
        XCTAssertEqual(UploadLimits.account(premiumType: 2), 1024 * mb)   // Nitro
        XCTAssertEqual(UploadLimits.account(premiumType: 3), 50 * mb)     // Nitro Basic
        XCTAssertEqual(UploadLimits.account(premiumType: 9), 20 * mb)     // unknown: the free limit
        XCTAssertEqual(UploadLimits.server(tier: 0), 20 * mb)
        XCTAssertEqual(UploadLimits.server(tier: 1), 20 * mb)
        XCTAssertEqual(UploadLimits.server(tier: 2), 50 * mb)
        XCTAssertEqual(UploadLimits.server(tier: 3), 100 * mb)
        XCTAssertEqual(UploadLimits.maxFiles, 10)
        XCTAssertEqual(UploadLimits.free, 20_971_520)   // Discord's docs give it in MiB
    }

    func testEffectiveLimit() {
        // account: none, Basic, Nitro  x  where: DM, unboosted server, Level 2, Level 3
        let tiers: [Int?] = [nil, 0, 2, 3]
        let want: [Int: [Int64]] = [
            0: [20, 20, 50, 100],
            3: [50, 50, 50, 100],
            2: [1024, 1024, 1024, 1024],
        ]
        for (premium, row) in want {
            for (tier, limit) in zip(tiers, row) {
                XCTAssertEqual(UploadContext(premiumType: premium, guildTier: tier).limit, limit * mb, "premium \(premium), tier \(String(describing: tier))")
            }
        }
    }

    // A boosted server's limit never applies in DMs, whatever servers you're in.
    @MainActor
    func testSessionReadsPremiumAndBoostLevel() {
        let s = Session(token: "test.token")
        s.inject("READY", [
            "user": ["id": "1", "username": "me", "premium_type": 3],
            "private_channels": [["id": "10", "type": 1, "recipient_ids": ["2"]]],
            "guilds": [["id": "20", "properties": ["name": "Boosted", "premium_tier": 3],
                        "channels": [["id": "21", "type": 0, "name": "general"]]]],
        ] as JObj)
        XCTAssertEqual(s.uploadContext(s.findChannel("10")!), UploadContext(premiumType: 3, guildTier: nil))
        XCTAssertEqual(s.uploadContext(s.findChannel("21")!), UploadContext(premiumType: 3, guildTier: 3))
        XCTAssertEqual(s.uploadContext(s.findChannel("21")!).limit, 100 * mb)
        // GUILD_UPDATE sends the guild flat; USER_UPDATE when Nitro changes.
        s.inject("GUILD_UPDATE", ["id": "20", "name": "Boosted", "premium_tier": 1] as JObj)
        s.inject("USER_UPDATE", ["id": "1", "username": "me", "premium_type": 0] as JObj)
        XCTAssertEqual(s.uploadContext(s.findChannel("21")!).limit, 20 * mb)
    }

    // MARK: - what the user is told

    func testSizes() {
        XCTAssertEqual(Uploads.size(84 * mb), "84 MB")
        XCTAssertEqual(Uploads.size(9 * mb + mb * 6 / 10), "9.6 MB")
        XCTAssertEqual(Uploads.size(820 * 1024), "820 KB")
        XCTAssertEqual(Uploads.size(1024 * mb), "1 GB")
        XCTAssertEqual(Uploads.size(250 * mb), "250 MB")
        // Just over 20 MB must not read as 20 MB in the over-limit message.
        XCTAssertEqual(Uploads.size(20 * mb + 1000, roundUp: true), "20.1 MB")
    }

    func testOverLimitMessages() {
        let big = 84 * mb
        func msg(_ premium: Int, _ tier: Int?) -> String { Uploads.tooBig("clip.mov", big, UploadContext(premiumType: premium, guildTier: tier)) }
        XCTAssertEqual(msg(0, nil), "clip.mov is 84 MB. You can send up to 20 MB here (50 MB with Nitro Basic or 1 GB with Nitro).")
        XCTAssertEqual(msg(0, 0), "clip.mov is 84 MB. You can send up to 20 MB here (50 MB with Nitro Basic, 1 GB with Nitro, or 50-100 MB in a boosted server).")
        XCTAssertEqual(msg(0, 2), "clip.mov is 84 MB. You can send up to 50 MB in this server, thanks to its Level 2 boost (1 GB with Nitro or 100 MB if this server reaches boost Level 3).")
        XCTAssertEqual(msg(3, nil), "clip.mov is 84 MB. You can send up to 50 MB with Nitro Basic (1 GB with Nitro).")
        XCTAssertEqual(msg(3, 0), "clip.mov is 84 MB. You can send up to 50 MB with Nitro Basic (1 GB with Nitro or 100 MB if this server reaches boost Level 3).")
        XCTAssertEqual(msg(1, nil), "clip.mov is 84 MB. You can send up to 50 MB with Nitro Classic (1 GB with Nitro).")
        XCTAssertEqual(Uploads.tooBig("movie.mkv", 2000 * mb, UploadContext(premiumType: 2, guildTier: nil)),
                       "movie.mkv is 2 GB. You can send up to 1 GB with Nitro, the most Discord allows.")
        XCTAssertEqual(Uploads.tooBig("clip.mov", 150 * mb, UploadContext(premiumType: 0, guildTier: 3)),
                       "clip.mov is 150 MB. You can send up to 100 MB in this server, thanks to its Level 3 boost (1 GB with Nitro).")
        XCTAssertEqual(Uploads.refused(UploadContext()),
                       "Discord refused the file as too big, so nothing was sent. You can send up to 20 MB here (50 MB with Nitro Basic or 1 GB with Nitro).")
    }

    func testRefusalIsRecognised() {
        XCTAssertTrue(ApiError(status: 413, body: nil, raw: nil).isTooLarge)
        XCTAssertTrue(ApiError(status: 400, body: ["code": 40005, "message": "Request entity too large"], raw: nil).isTooLarge)
        XCTAssertTrue(ApiError(status: 400, body: ["code": 50045], raw: nil).isTooLarge)
        XCTAssertFalse(ApiError(status: 400, body: ["code": 50035], raw: nil).isTooLarge)
        XCTAssertFalse(ApiError(status: 413, body: nil, raw: nil).errorDescription!.contains("10 MB"))
    }

    // MARK: - attaching

    func testOverLimitFileIsNotAttached() {
        let ctx = UploadContext()
        let r = Uploads.check(existing: 0, [.init(name: "a.png", size: mb), .init(name: "clip.mov", size: 84 * mb), .init(name: "Photos", size: 0, isFolder: true)], ctx)
        XCTAssertEqual(r.accepted, [0])
        XCTAssertEqual(r.notes, [Uploads.tooBig("clip.mov", 84 * mb, ctx), "Photos is a folder. Zip it first to send it."])
        // Exactly at the limit is fine.
        XCTAssertEqual(Uploads.check(existing: 0, [.init(name: "b.zip", size: 20 * mb)], ctx).accepted, [0])
    }

    func testFileCountCap() {
        let files = (1...4).map { Uploads.Candidate(name: "p\($0).png", size: 1000) }
        let r = Uploads.check(existing: 8, files, UploadContext())
        XCTAssertEqual(r.accepted, [0, 1])
        XCTAssertEqual(r.notes, ["Discord allows 10 files per message, so p3.png and p4.png weren't attached. Send these first, then add the rest."])
        XCTAssertEqual(Uploads.check(existing: 10, [files[0]], UploadContext()).accepted, [])
        XCTAssertEqual(Uploads.tooMany(["a", "b", "c"]), "Discord allows 10 files per message, so a and 2 more weren't attached. Send these first, then add the rest.")
        XCTAssertEqual(Uploads.check(existing: 0, (1...12).map { .init(name: "\($0)", size: 1) }, UploadContext()).accepted.count, 10)
    }

    func testTrayWarning() {
        let ctx = UploadContext()
        XCTAssertNil(Uploads.trayWarning([.init(name: "a", size: 5 * mb)], ctx))
        XCTAssertEqual(Uploads.trayWarning([.init(name: "a", size: 19 * mb)], ctx)?.text, "a is close to the 20 MB limit here.")
        XCTAssertEqual(Uploads.trayWarning([.init(name: "a", size: 30 * mb)], ctx)?.over, true)
    }

    // MARK: - paste

    func testPastePicking() {
        let gif = PastePick.Item(type: "com.compuserve.gif", ext: "gif")
        let png = PastePick.Item(type: "public.png", ext: "png")
        // A GIF copied in Safari stays a GIF, even with the TIFF and the address next to it.
        XCTAssertEqual(PastePick.choose([["com.compuserve.gif", "public.tiff", "public.url"]], text: "https://example.com/dance.gif"), .data([gif]))
        // Chrome's Copy Image: PNG and HTML, no text.
        XCTAssertEqual(PastePick.choose([["public.html", "public.png"]], text: nil), .data([png]))
        // A screenshot to the clipboard.
        XCTAssertEqual(PastePick.choose([["public.png", "public.tiff"]], text: nil), .data([png]))
        XCTAssertEqual(PastePick.choose([["public.jpeg", "public.tiff"]], text: nil), .data([.init(type: "public.jpeg", ext: "jpg")]))
        XCTAssertEqual(PastePick.choose([["org.webmproject.webp"]], text: nil), .data([.init(type: "org.webmproject.webp", ext: "webp")]))
        XCTAssertEqual(PastePick.choose([["public.mpeg-4"]], text: nil), .data([.init(type: "public.mpeg-4", ext: "mp4")]))
        XCTAssertEqual(PastePick.choose([["com.apple.quicktime-movie"]], text: nil), .data([.init(type: "com.apple.quicktime-movie", ext: "mov")]))
        XCTAssertEqual(PastePick.choose([["public.tiff"]], text: nil), .data([.init(type: "public.tiff", ext: "png", toPng: true)]))
        // Two pictures copied at once.
        XCTAssertEqual(PastePick.choose([["com.compuserve.gif"], ["public.png"]], text: nil), .data([gif, png]))
        // Files copied in Finder (name as text, icon as TIFF) attach as the files.
        XCTAssertEqual(PastePick.choose([["public.file-url", "public.utf8-plain-text", "public.tiff"]], text: "clip.mov"), .files)
        // Text stays text: plain, from a document with a picture of it, a cell holding a link,
        // a sentence with a link in it.
        XCTAssertEqual(PastePick.choose([["public.utf8-plain-text"]], text: "hello"), .text)
        XCTAssertEqual(PastePick.choose([["public.rtf", "public.utf8-plain-text", "public.tiff"]], text: "hello there"), .text)
        XCTAssertEqual(PastePick.choose([["public.rtf", "public.utf8-plain-text", "public.png"]], text: "https://example.com"), .text)
        XCTAssertEqual(PastePick.choose([["public.utf8-plain-text", "public.png"]], text: "look https://example.com/a.png"), .text)
        XCTAssertEqual(PastePick.choose([["public.utf8-plain-text", "public.url"]], text: "https://example.com"), .text)
        // Something only NSImage can read (a PDF), with no text: a PNG, as before.
        XCTAssertEqual(PastePick.choose([["com.adobe.pdf"]], text: nil, canMakeImage: { true }), .imageFallback)
        XCTAssertEqual(PastePick.choose([["com.adobe.pdf"]], text: nil), .text)
    }

    @MainActor
    func testPastedGifIsSavedAsGif() throws {
        // GIF89a with two frames' worth of header is enough: the bytes must come out unchanged.
        let gifData = Data("GIF89a".utf8) + Data([1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x3B])
        let pb = NSPasteboard(name: NSPasteboard.Name("BuzzcordTest-\(UUID().uuidString)"))
        defer { pb.releaseGlobally() }
        pb.clearContents()
        let item = NSPasteboardItem()
        item.setData(gifData, forType: NSPasteboard.PasteboardType("com.compuserve.gif"))
        item.setData(Data([0]), forType: .tiff)
        pb.writeObjects([item])
        let types = (pb.pasteboardItems ?? []).map { $0.types.map(\.rawValue) }
        guard case .data(let want) = PastePick.choose(types, text: pb.string(forType: .string)) else { return XCTFail("not data") }
        let urls = PastePick.save(pb, want)
        XCTAssertEqual(urls.count, 1)
        XCTAssertEqual(urls.first?.pathExtension, "gif")
        XCTAssertEqual(try Data(contentsOf: urls[0]), gifData)
        try? FileManager.default.removeItem(at: urls[0])
    }
}
