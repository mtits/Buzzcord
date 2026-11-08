import AppKit
import UserNotifications
import CryptoKit

// Hidden checks, run from the command line:
//   Buzzcord --demo            buddy list and a chat window filled with made-up data (no sign-in)
//   Buzzcord --gateway-test    connects with a fake token; passes if Discord rejects it with 4004
//   --demo --notice / --login / --about   also show the first-run notice (saves nothing), the
//                              sign-in window and the About box
@MainActor
enum Demo {
    private static var demoKeep: [AnyObject] = []

    // --demo --shots: for website screenshots. Smileys instead of Discord's default avatars
    // (its logo), and the status line a signed-in account shows.
    private static var shotPicture = "https://cdn.discordapp.com/embed/avatars/2.png"

    private static func screenshotLook(_ s: Session, _ ids: [String]) {
        let faces: [(String, NSColor)] = [("😎", .systemTeal), ("🙂", .systemYellow), ("😄", .systemPink),
                                          ("🤠", .systemOrange), ("😺", .systemPurple), ("🐶", .systemGreen)]
        for (i, f) in faces.enumerated() {
            let img = NSImage(size: NSSize(width: 128, height: 128), flipped: false) { r in
                f.1.withAlphaComponent(0.35).setFill()
                r.fill()
                let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 84)]
                let t = NSAttributedString(string: f.0, attributes: a)
                let sz = t.size()
                t.draw(at: NSPoint(x: (r.width - sz.width) / 2, y: (r.height - sz.height) / 2))
                return true
            }
            ImageCache.shared.preload("demo://face/\(i)", img)
            if i < ids.count { User.demoAvatars[ids[i]] = "demo://face/\(i)" }
            if i == 0, let tiff = img.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                shotPicture = "data:image/png;base64," + png.base64EncodedString()
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { s.connectionState = "Online" }
    }

    static func run(_ app: AppDelegate) {
        let a = AccountController(token: "demo.token", app: app, index: 0)
        app.accounts.append(a)
        let s = a.session
        let base = Int64(Snowflake.now())! - (Int64(3600 * 1000) << 22)
        func id(_ n: Int64) -> String { String(base + (n * 60_000 << 22)) }
        let me = id(1), alyssa = id(2), ben = id(3), dina = id(4), xenia = id(5), carlo = id(6)
        let dm = id(10), group = id(11), guild = id(20), cat = id(21), general = id(22), memes = id(23), lobby = id(24)
        func user(_ i: String, _ name: String, _ global: String?) -> JObj { ["id": i, "username": name, "global_name": global ?? JNull, "avatar": JNull, "discriminator": "0"] }
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let ready: JObj = [
            "user": user(me, "marc", "Marc"),
            "users": [user(alyssa, "alyssa", "Alyssa"), user(ben, "ben", "Ben"), user(dina, "dina", "Dina"), user(xenia, "xenia", "♡ Xenia ♡"), user(carlo, "carlo", "Carlo")],
            "relationships": [["id": alyssa, "type": 1], ["id": ben, "type": 1], ["id": dina, "type": 1], ["id": xenia, "type": 1], ["id": carlo, "type": 3]],
            "private_channels": [
                ["id": dm, "type": 1, "recipient_ids": [alyssa], "last_message_id": id(40)],
                ["id": group, "type": 3, "name": "Valo squad", "recipient_ids": [alyssa, ben], "last_message_id": id(30)],
            ],
            "guilds": [[
                "id": guild,
                "properties": ["name": "PH Gamers", "icon": JNull, "owner_id": me],
                "roles": [["id": guild, "name": "@everyone", "permissions": "1071698660929", "position": 0]],
                "channels": [
                    ["id": cat, "type": 4, "name": "Text channels", "position": 0],
                    ["id": general, "type": 0, "name": "general", "parent_id": cat, "position": 0, "last_message_id": id(35)],
                    ["id": memes, "type": 0, "name": "memes", "parent_id": cat, "position": 1],
                    ["id": lobby, "type": 2, "name": "Lobby", "parent_id": cat, "position": 2],
                ],
                "voice_states": [["user_id": ben, "channel_id": lobby]],
            ]],
            "read_state": ["entries": [["id": dm, "last_message_id": id(30)], ["id": general, "last_message_id": id(30), "mention_count": 2]]],
            "presences": [
                ["user": ["id": alyssa], "status": "online", "activities": [["type": 0, "name": "VALORANT", "details": "Competitive", "state": "In Queue (2 of 5)", "timestamps": ["start": now - 765_000]]]],
                ["user": ["id": ben], "status": "idle", "activities": [["type": 4, "name": "Custom Status", "state": "brb eating"]]],
                ["user": ["id": dina], "status": "dnd", "activities": [["type": 2, "name": "Spotify", "details": "Ikaw", "state": "Yeng Constantino"]]],
            ],
            "user_settings": ["status": "online"],
        ]
        s.inject("READY", ready)
        a.buddy.filter = ""

        // Your own profile with a Board (real game ids, so names and icons load).
        ProfileWindowController.demoProfiles[me] = ["user": user(me, "marc", "Marc"), "widgets": [
            ["id": "1", "data": ["type": "favorite_games", "games": [["game_id": "1402418342127472751", "comment": "Best roguelike ever", "tags": ["expert", "hidden_gem"]]]]],
            ["id": "2", "data": ["type": "played_games", "games": [["game_id": "356875890958925834"], ["game_id": "1402418342127472751"], ["game_id": "1402418491272986635"], ["game_id": "1124352351269048370"]]]],
        ] as [Any]]
        if CommandLine.arguments.contains("--profile") { a.showProfile(me, guildId: nil) }
        if let i = CommandLine.arguments.firstIndex(of: "--picture"), i + 1 < CommandLine.arguments.count {
            PictureWindowController.show(url: CommandLine.arguments[i + 1], title: "Alyssa - Profile Picture", fileName: "alyssa profile picture.png")
        }
        if CommandLine.arguments.contains("--shots") { screenshotLook(s, [alyssa, me, ben, dina, xenia, carlo]) }
        if CommandLine.arguments.contains("--about") { app.about(nil) }
        if CommandLine.arguments.contains("--unread") { DispatchQueue.main.asyncAfter(deadline: .now() + 1) { UnreadWindowController.show(app) } }
        if let i = CommandLine.arguments.firstIndex(of: "--donate") {
            if i + 1 < CommandLine.arguments.count { DonateView.demoShown = DonateQR(rawValue: CommandLine.arguments[i + 1]) }
            DonateWindowController.show()
        }
        if CommandLine.arguments.contains("--login") {
            let login = LoginWindowController { _ in }
            login.showWindow(nil)
            demoKeep.append(login)
        }
        if CommandLine.arguments.contains("--notice") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { _ = Notice.show() }   // saves nothing
        }
        if CommandLine.arguments.contains("--away") {
            AutoAway.shared.sessions = { [] }   // keep the real idle check out of it
            s.setAutoAway(true)
            UNUserNotificationCenter.current().getNotificationCategories { cats in
                Log.write("banner actions: " + cats.flatMap { c in c.actions.map { "\(c.identifier)/\($0.title)" } }.joined(separator: ", "))
            }
        }
        if CommandLine.arguments.contains("--playing") {
            GameDetector.demo = true
            Playing.onChange = { s.pushPresence() }
            Playing.apply()
        }

        let c = s.findChannel(dm)!
        let chat = a.openChat(c)
        func msg(_ n: Int64, _ from: String, _ text: String, _ extra: JObj = [:]) -> JObj {
            var m: JObj = ["id": id(n), "channel_id": dm, "author": ["id": from], "content": text, "type": 0]
            for (k, v) in extra { m[k] = v }
            return m
        }
        chat.add(msg(31, alyssa, "Immortal :fire: **finally**"))
        chat.add(msg(32, alyssa, "", ["attachments": [["id": "1", "filename": "rank.png", "url": shotPicture, "content_type": "image/png", "width": 128, "height": 128, "size": 4000]]]))
        chat.add(msg(33, me, "check this clip\nhttps://example.com/clip", ["reactions": [["emoji": ["name": "🔥"], "count": 2, "me": true]]]))
        chat.add(msg(34, alyssa, "I know right `ez`", ["referenced_message": ["author": ["id": me], "content": "that ace was insane"]]))
        chat.add(msg(40, alyssa, "you on tonight? ||it's a surprise||", ["edited_timestamp": "2026-11-03T00:00:00Z"]))
        s.onTyping?(dm, alyssa)
        a.openChat(s.findChannel(group)!, activate: false)
        a.alerts.clear()
        a.alerts.record(AlertEntry(time: Date().addingTimeInterval(-600), kind: .signIn, title: "Ben", text: "signed in.", user: ben))
        a.alerts.record(AlertEntry(time: Date().addingTimeInterval(-300), kind: .mention, title: "Dina in #general (PH Gamers)", text: "@Marc are you joining later?", channel: general))
        a.alerts.record(AlertEntry(time: Date(), kind: .message, title: "Alyssa", text: "you on tonight?", channel: dm))
        AlertsWindowController.show(a)
    }

    // Offline checks of the call plumbing: RTP transport encryption, Opus through the jitter
    // buffer, and voice activity.
    static func voiceTest() {
        var fails = 0
        func check(_ ok: Bool, _ name: String, _ detail: String = "") {
            print((ok ? "PASS  " : "FAIL  ") + name + (detail.isEmpty ? "" : "  -- " + detail))
            if !ok { fails += 1 }
        }
        let key = SymmetricKey(size: .bits256)
        let payload = (0..<120).map { UInt8($0) }
        let sealed = VoiceConnection.sealRtp(key, 7, 960, 0xABCD, 5, payload)!
        let opened = VoiceConnection.openRtp(key, sealed)
        check(opened?.0 == 7 && opened?.1 == 0xABCD && opened?.2 == payload, "RTP seal/open round trip", "\(sealed.count) bytes")
        var tampered = sealed
        tampered[20] ^= 1
        check(VoiceConnection.openRtp(key, tampered) == nil, "RTP rejects a tampered packet")
        // A packet with a one-word header extension, as WebRTC clients send.
        var withExt = [UInt8](sealed[0..<12])
        withExt[0] |= 0x10
        let ext: [UInt8] = [0xBE, 0xDE, 0x00, 0x01]
        let body: [UInt8] = [1, 2, 3, 4] + payload
        var nonceCounter = [UInt8](repeating: 0, count: 4)
        VoiceConnection.writeU32(&nonceCounter, 0, 9)
        let box = try! AES.GCM.seal(body, using: key, nonce: try! AES.GCM.Nonce(data: nonceCounter + [UInt8](repeating: 0, count: 8)), authenticating: withExt + ext)
        let extPacket = withExt + ext + [UInt8](box.ciphertext) + [UInt8](box.tag) + nonceCounter
        check(VoiceConnection.openRtp(key, extPacket)?.2 == payload, "RTP header extension is skipped")

        let audio = AudioEngine()
        var frames: [Data] = []
        var talking: [Bool] = []
        audio.onFrame = { frames.append($0) }
        audio.onTransmitting = { talking.append($0) }
        let now = Date()
        for f in 0..<25 {
            let mono = (0..<960).map { i in Float(sin(Double(f * 960 + i) * 2 * .pi * 440 / 48000) * 0.3) }
            audio.processCaptured(mono, now.addingTimeInterval(Double(f) * 0.02))
        }
        for f in 25..<60 { audio.processCaptured([Float](repeating: 0, count: 960), now.addingTimeInterval(Double(f) * 0.02)) }
        let silences = frames.filter { $0 == AudioEngine.silenceFrame }.count
        check(talking == [true, false] && frames.count >= 25 && silences == 5, "Voice activity: talks, then 5 silence frames", "\(frames.count) frames, \(silences) silence, \(talking)")

        let stream = PlaybackStream()
        for (i, f) in frames.prefix(25).enumerated().reversed() { stream.push(UInt16(1000 + i), f) }
        var peak: Int16 = 0
        var got = 0
        while let pcm = stream.nextFrame() { got += 1; if got > 3 { peak = max(peak, pcm.map { abs($0) }.max() ?? 0) } ; if got > 40 { break } }
        check(got >= 25 && peak > 5000, "Opus out of order through the jitter buffer", "\(got) frames played, peak \(peak)")
        print(fails == 0 ? "ALL PASSED" : "\(fails) FAILED")
        exit(fails == 0 ? 0 : 1)
    }

    // Downloads Discord's game list, looks a few games up and lists games running right now.
    static func gamesTest() {
        Task { @MainActor in
            let started = Date()
            await GameCatalog.shared.ensureLoaded()
            print("catalog loaded: \(GameCatalog.shared.isLoaded) in \(String(format: "%.1f", Date().timeIntervalSince(started))) s")
            for q in ["hades", "minecraft", "hearthstone"] {
                print("search \(q): " + GameCatalog.shared.search(q, max: 4).map { "\($0.name) (\($0.id))" }.joined(separator: ", "))
            }
            if let id = GameCatalog.shared.search("hearthstone", max: 1).first?.id {
                let g = await GameBoard.info(id)
                print("info: \(g.title) icon=\(g.iconUrl ?? "none")")
            }
            GameDetector.shared.scan(force: true)
            print("running games: " + (GameDetector.shared.running.isEmpty ? "none" : GameDetector.shared.running.map { "\($0.name) [\($0.appId ?? "no id")]" }.joined(separator: ", ")))
            exit(0)
        }
    }

    // Opens the real mic and speakers for a few seconds and logs what happened. Run it as
    // `open Buzzcord.app --args --audio-test` so macOS uses Buzzcord's own mic permission.
    // Starts call audio on pairs of this Mac's devices and logs what each one ended up using.
    static func audioProbe() {
        let devs = AudioDevices.all()
        for d in devs { Log.write("probe device: \(d.name) id=\(d.id) in=\(d.inputs) out=\(d.outputs) rate=\(Int(d.sampleRate))") }
        func find(_ name: String, _ input: Bool) -> AudioDevices.Device? { devs.first { $0.name.contains(name) && (input ? $0.inputs : $0.outputs) > 0 } }
        var cases: [(String, AudioDevices.Device?, AudioDevices.Device?)] = [("Voice Settings choice", nil, nil)]
        for (label, i, o) in [("MacBook", "MacBook", "MacBook"), ("Razer", "Razer", "Razer"), ("Insta360 + USB", "Insta360", "USB Sound")] {
            if let i = find(i, true), let o = find(o, false) { cases.append((label, i, o)) }
        }
        func next() {
            guard !cases.isEmpty else { exit(0) }
            let (label, i, o) = cases.removeFirst()
            let a = AudioEngine()
            a.muted = true
            a.inputDevice = i?.id
            a.outputDevice = o?.id
            var frames = 0
            a.onLevel = { _ in frames += 1 }
            do { try a.start() } catch { Log.write("probe \(label): FAIL \(error.localizedDescription)"); next(); return }
            frames = 0   // count only the 1.5 s below: about 75 frames of 20 ms
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                Log.write("probe \(label): mic \(a.usingInput?.name ?? "-"), speakers \(a.usingOutput?.name ?? "-"), echo cancellation \(a.echoCancellation ? "ON" : "off"), \(frames) frames, \(a.renderCalls) speaker callbacks\(a.note.isEmpty ? "" : ", note: " + a.note)")
                a.stop()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { next() }
            }
        }
        next()
    }

    static func audioTest() {
        let a = AudioEngine()
        var frames = 0
        var peak: Float = -120
        a.muted = true
        a.onLevel = { db in frames += 1; peak = max(peak, db) }
        do {
            try a.start()
            Log.write("audio test: started, echo cancellation \(a.echoCancellation ? "on" : "off")")
        } catch {
            Log.write("audio test: FAIL \(error.localizedDescription)")
            exit(1)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            a.stop()
            Log.write("audio test: \(frames) mic frames in 3 s (want about 150), loudest \(Int(peak)) dBFS, \(a.renderCalls) speaker callbacks")
            exit(frames > 100 && a.renderCalls > 10 ? 0 : 1)
        }
    }

    static func gatewayTest() {
        let g = Gateway(token: "fake.token.value") { ["status": "online", "since": 0, "activities": [Any](), "afk": false] }
        var states: [String] = []
        g.onState = { st in
            states.append(st)
            print("state:", st)
        }
        g.onAuthFailed = {
            print("PASS gateway rejected the fake token (4004):", states.joined(separator: " > "))
            exit(0)
        }
        g.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + 25) {
            print("FAIL no 4004 within 25s:", states.joined(separator: " > "))
            exit(1)
        }
    }
}
