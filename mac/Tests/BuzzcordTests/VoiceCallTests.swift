import CryptoKit
import Foundation
import Network
import XCTest
@testable import Buzzcord

// A whole call against a fake Discord voice server on localhost (websocket and UDP), with DAVE:
// a pretend second caller decrypts what we send and talks back. Port of VoiceCallTest in
// src/Voice/VoiceTests.cs.
final class FakeVoiceServer: @unchecked Sendable {
    private let q = DispatchQueue(label: "fake voice server")
    private let ws: NWListener
    private let udp: NWListener
    private var wsConn: NWConnection?
    private var udpConn: NWConnection?
    private var seq = 0
    private let lock = NSLock()
    private var text: [JObj] = []
    private var binary: [[UInt8]] = []
    private var audio: [[UInt8]] = []
    private(set) var clientPort = 0

    init() throws {
        let p = NWParameters.tcp
        let wsOptions = NWProtocolWebSocket.Options()
        wsOptions.autoReplyPing = true
        p.defaultProtocolStack.applicationProtocols.insert(wsOptions, at: 0)
        ws = try NWListener(using: p, on: .any)
        udp = try NWListener(using: .udp, on: .any)
        let ready = DispatchSemaphore(value: 0)
        for l in [ws, udp] {
            l.stateUpdateHandler = { if case .ready = $0 { ready.signal() } }
        }
        ws.newConnectionHandler = { [weak self] c in
            guard let self else { return }
            self.wsConn = c
            c.start(queue: self.q)
            self.receiveWs(c)
        }
        udp.newConnectionHandler = { [weak self] c in
            guard let self else { return }
            self.udpConn = c
            if case .hostPort(_, let port) = c.endpoint { self.clientPort = Int(port.rawValue) }
            c.start(queue: self.q)
            self.receiveUdp(c)
        }
        ws.start(queue: q)
        udp.start(queue: q)
        _ = ready.wait(timeout: .now() + 5)
        _ = ready.wait(timeout: .now() + 5)
    }

    var wsPort: Int { Int(ws.port?.rawValue ?? 0) }
    var udpPort: Int { Int(udp.port?.rawValue ?? 0) }

    func stop() { ws.cancel(); udp.cancel(); wsConn?.cancel(); udpConn?.cancel() }

    private func receiveWs(_ c: NWConnection) {
        c.receiveMessage { [weak self] data, ctx, _, error in
            guard let self, error == nil else { return }
            let meta = ctx?.protocolMetadata(definition: NWProtocolWebSocket.definition) as? NWProtocolWebSocket.Metadata
            if let data {
                self.lock.lock()
                if meta?.opcode == .text, let j = Json.parse(data) as? JObj { self.text.append(j) }
                else if meta?.opcode == .binary { self.binary.append([UInt8](data)) }
                self.lock.unlock()
            }
            self.receiveWs(c)
        }
    }

    private func receiveUdp(_ c: NWConnection) {
        c.receiveMessage { [weak self] data, _, _, error in
            guard let self, error == nil else { return }
            if let p = data.map({ [UInt8]($0) }) {
                if p.count == 74 && p[1] == 1 {
                    // IP discovery: echo the SSRC and tell the client its address and port.
                    var reply = [UInt8](repeating: 0, count: 74)
                    reply[1] = 2; reply[3] = 70
                    reply[4..<8] = p[4..<8]
                    let addr = Array("127.0.0.1".utf8)
                    reply[8..<(8 + addr.count)] = addr[...]
                    reply[72] = UInt8(self.clientPort >> 8); reply[73] = UInt8(self.clientPort & 0xFF)
                    c.send(content: Data(reply), completion: .contentProcessed { _ in })
                } else {
                    self.lock.lock(); self.audio.append(p); self.lock.unlock()
                }
            }
            self.receiveUdp(c)
        }
    }

    private func frame(_ opcode: NWProtocolWebSocket.Opcode, _ payload: Data) {
        let meta = NWProtocolWebSocket.Metadata(opcode: opcode)
        let ctx = NWConnection.ContentContext(identifier: "m", metadata: [meta])
        wsConn?.send(content: payload, contentContext: ctx, isComplete: true, completion: .contentProcessed { _ in })
    }

    func sendJson(_ op: Int, _ d: JObj) { frame(.text, Json.data(["op": op, "d": d])) }

    func sendBinary(_ op: Int, _ payload: [UInt8]) {
        frame(.binary, Data(TlsWriter().u16(seq).u8(op).raw(payload).toArray()))
        seq += 1
    }

    func sendUdp(_ p: [UInt8]) { udpConn?.send(content: Data(p), completion: .contentProcessed { _ in }) }

    private func waitFor<T>(_ what: String, _ take: () -> T?) throws -> T {
        let until = Date().addingTimeInterval(5)
        while Date() < until {
            lock.lock()
            let got = take()
            lock.unlock()
            if let got { return got }
            Thread.sleep(forTimeInterval: 0.01)
        }
        throw CheckFailed(description: "voice client never sent " + what)
    }

    func waitForClient() throws { _ = try waitFor("a connection") { wsConn != nil ? true : nil } }

    func expect(_ op: Int) throws -> JObj {
        try waitFor("op \(op)") {
            guard let i = text.firstIndex(where: { $0.long("op", -1) == Int64(op) }) else { return nil }
            let m = text[i]
            text.removeSubrange(0...i)
            return m.obj("d") ?? JObj()
        }
    }

    func expectBinary(_ op: Int) throws -> [UInt8] {
        try waitFor("binary op \(op)") {
            guard let i = binary.firstIndex(where: { $0.first == UInt8(op) }) else { return nil }
            let b = binary[i]
            binary.removeSubrange(0...i)
            return Array(b.dropFirst())
        }
    }

    func expectAudio() throws -> [UInt8] {
        try waitFor("audio") {
            while let p = audio.first {
                audio.removeFirst()
                if p.count > 12 + 16 + 4 { return p }   // skip keepalives
            }
            return nil
        }
    }
}

final class VoiceCallTests: XCTestCase {
    func testCallAgainstFakeVoiceServer() throws {
        let me: UInt64 = 1001, friend: UInt64 = 42, channel: UInt64 = 555
        let keyBytes = (0..<32).map { _ in UInt8.random(in: 0...255) }
        let transport = SymmetricKey(data: keyBytes)
        let server = try FakeVoiceServer()
        defer { server.stop() }
        let conn = VoiceConnection(endpoint: "ws://127.0.0.1:\(server.wsPort)", serverId: "999", userId: me, channelId: channel, sessionId: "sess", token: "tok")
        defer { conn.stop() }
        let ready = DispatchSemaphore(value: 0)
        let speaking = DispatchSemaphore(value: 0)
        let heard = DispatchSemaphore(value: 0)
        var received: (UInt64, Data)?
        conn.onReady = { ready.signal() }
        conn.onSpeaking = { u, _ in if u == friend { speaking.signal() } }
        conn.onOpus = { u, _, f in received = (u, f); heard.signal() }
        conn.start()
        try server.waitForClient()

        let identify = try server.expect(0)
        try check(identify.str("server_id") == "999" && identify.str("user_id") == "1001" && identify.long("max_dave_protocol_version") == 1, "identify: \(Json.string(identify))")
        server.sendJson(8, ["heartbeat_interval": 30000])
        server.sendJson(2, ["ssrc": 777, "ip": "127.0.0.1", "port": server.udpPort, "modes": [VoiceConnection.mode]])
        let select = try server.expect(1)
        let data = select.obj("data") ?? JObj()
        try check(select.str("protocol") == "udp" && data.str("address") == "127.0.0.1" && Int(data.long("port")) == server.clientPort
                  && data.str("mode") == VoiceConnection.mode, "select protocol: \(Json.string(select))")

        // DAVE: the gateway's identity, the session description, then who's here.
        let gateway = NistP256.generate()
        let external = ExternalSender(signatureKey: gateway.pub, credential: MlsCredential.basic(Array("gw".utf8)))
        server.sendBinary(25, external.bytes())
        server.sendJson(4, ["mode": VoiceConnection.mode, "secret_key": keyBytes.map { Int($0) }, "dave_protocol_version": 1])
        try check(ready.wait(timeout: .now() + 5) == .success, "connection never became ready")
        server.sendJson(11, ["user_ids": [String(friend)]])
        let kpMine = try KeyPackage.read(TlsReader(try server.expectBinary(26)))
        try check(DaveSession.fromBigEndian(kpMine.leafNode.credential.identity) == me, "key package identity")

        // Before the group exists, audio goes out with transport encryption only.
        conn.setSpeaking(true)
        let sp = try server.expect(5)
        try check(sp.long("speaking") == 1 && sp.long("ssrc") == 777, "speaking: \(Json.string(sp))")
        let early = Array("early opus".utf8)
        conn.sendOpus(Data(early))
        let got = VoiceConnection.openRtp(transport, try server.expectAudio())
        try check(got?.1 == 777 && got?.2 == early, "passthrough audio didn't arrive intact")

        // The friend's key package; the gateway asks us to add them, we commit, it's announced.
        let friendSig = NistP256.generate()
        let kpFriend = try MlsGroup.makeKeyPackage(credential: MlsCredential.basic(DaveSession.bigEndian(friend)), sigPriv: friendSig.priv, sigPub: friendSig.pub)
        let proposalMsg = try MlsGroup.externalProposal(groupId: DaveSession.bigEndian(channel), epoch: 0, senderIndex: 0,
                                                       proposal: MlsProposal.adding(kpFriend.keyPackage), sigPriv: gateway.priv)
        server.sendBinary(27, TlsWriter().u8(0).vector { $0.raw(proposalMsg) }.toArray())
        let op28 = try server.expectBinary(28)
        let r28 = TlsReader(op28)
        let start = r28.position
        _ = try MlsMessage.read(r28)
        let commit = r28.since(start)
        let welcome = try MlsWelcome.read(r28)
        server.sendBinary(29, TlsWriter().u16(3).raw(commit).toArray())
        try check(try server.expect(23).long("transition_id") == 3, "not ready for the transition")
        server.sendJson(22, ["transition_id": 3])

        // The friend joins from our welcome and checks they can hear us.
        let friendGroup = try MlsGroup.join(welcome: welcome, keyPackage: kpFriend.keyPackage, initPriv: kpFriend.initPriv,
                                            leafPriv: kpFriend.leafPriv, sigPriv: friendSig.priv, externalTree: nil)
        func le(_ v: UInt64) -> [UInt8] { withUnsafeBytes(of: v.littleEndian) { Array($0) } }
        let hearMe = FrameDecryptor(HashRatchet(friendGroup.export("Discord Secure Frames v0", le(me), 16)), Date().addingTimeInterval(-5))
        let secret = Array("encrypted opus".utf8)
        var packet: [UInt8]?
        for _ in 0..<50 where packet == nil {
            conn.sendOpus(Data(secret))
            if let p = VoiceConnection.openRtp(transport, try server.expectAudio())?.2, DaveFrame.parse(p) != nil { packet = p }   // the transition may take a moment
        }
        guard let packet, let frame = DaveFrame.parse(packet) else { throw CheckFailed(description: "audio never became DAVE-encrypted") }
        try check(hearMe.decrypt(frame, packet, Date()) == secret, "friend couldn't decrypt our audio")

        // And we hear them.
        server.sendJson(5, ["user_id": String(friend), "ssrc": 4242, "speaking": 1])
        try check(speaking.wait(timeout: .now() + 5) == .success, "never saw the friend's speaking state")
        let theirs = FrameEncryptor(HashRatchet(friendGroup.export("Discord Secure Frames v0", le(friend), 16)))
        server.sendUdp(VoiceConnection.sealRtp(transport, 10, 960, 4242, 1, theirs.encrypt(Array("hello from 42".utf8)))!)
        try check(heard.wait(timeout: .now() + 5) == .success, "friend's audio never arrived")
        try check(received?.0 == friend && received.map { String(decoding: $0.1, as: UTF8.self) } == "hello from 42", "friend's audio came out wrong")
        let code = conn.dave.privacyCode ?? ""
        try check(code.split(separator: " ").count == 6, "privacy code: \(code)")
        print("Voice call against a fake server: IP discovery, select protocol, passthrough, DAVE join, audio both ways; privacy code \(code.prefix(11))...")
    }
}

// The main gateway's side of joining: our voice state plus the voice server make a VoiceServer;
// ringing comes and goes with call events.
@MainActor
final class VoiceJoinTests: XCTestCase {
    func testJoinAndCallEvents() async throws {
        let s = Session(token: "test.token")
        s.inject("READY", ["user": ["id": "100", "username": "me"], "guilds": [], "private_channels": [["id": "500", "type": 1, "recipient_ids": ["200"]]]])
        var rang: [String] = []
        s.onIncomingCall = { rang.append($0) }
        s.inject("CALL_CREATE", ["channel_id": "500", "ringing": ["100"], "voice_states": [["user_id": "200", "channel_id": "500", "session_id": "x"]]])
        try check(rang == ["500"] && s.isRingingMe("500") && s.voiceMembers("500") == ["200"], "incoming call: \(rang)")
        s.inject("CALL_UPDATE", ["channel_id": "500", "ringing": [Any]()])
        try check(!s.isRingingMe("500") && rang.count == 1, "still ringing after the update")

        async let server = s.joinVoice(guildId: "900", channelId: "914", selfMute: false, selfDeaf: false)
        try await Task.sleep(nanoseconds: 50_000_000)
        s.inject("VOICE_STATE_UPDATE", ["user_id": "100", "channel_id": "914", "guild_id": "900", "session_id": "vs1"])
        s.inject("VOICE_SERVER_UPDATE", ["token": "vt", "guild_id": "900", "endpoint": "voice.example:443"])
        let v = try await server
        try check(v.sessionId == "vs1" && v.token == "vt" && v.serverId == "900" && v.endpoint == "voice.example:443" && v.channelId == "914", "voice server: \(v)")
        try check(s.voiceMembers("914") == ["100"], "our voice state")
    }
}
