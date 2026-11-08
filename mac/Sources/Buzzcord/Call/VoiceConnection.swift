import CryptoKit
import Foundation
import Network

// One connection to a Discord voice server (voice gateway v8): the websocket for signalling and
// DAVE, and UDP for RTP audio with aead_aes256_gcm_rtpsize transport encryption. Opus frames go
// in and out; audio devices live in AudioEngine. Everything runs on one serial queue.
final class VoiceConnection: @unchecked Sendable {
    static let mode = "aead_aes256_gcm_rtpsize"
    static let payloadType: UInt8 = 0x78   // Opus

    private let url: URL
    private let serverId: String, sessionId: String, token: String
    private let userId: UInt64
    let dave: DaveSession
    let q = DispatchQueue(label: "Buzzcord voice", qos: .userInteractive)

    private var ws: URLSessionWebSocketTask?
    private let closeCodes = SocketCloseCodes()
    private lazy var http = SocketCloseCodes.makeSession(closeCodes)
    private var heartbeat: DispatchSourceTimer?
    private var udp: NWConnection?
    private var transport: SymmetricKey?
    private var ssrcUsers: [UInt32: UInt64] = [:]
    private(set) var ssrc: UInt32 = 0
    private var sendNonce: UInt32 = 0, rtpTimestamp: UInt32 = 0
    private var rtpSeq: UInt16 = 0
    private var lastSeq: Int64 = -1
    private var resumable = false
    private var stopped = false
    private var attempts = 0
    private var keepAlive: UInt64 = 0
    private var keepAliveTimer: DispatchSourceTimer?

    // All called on q.
    var onState: ((String) -> Void)?
    var onReady: (() -> Void)?
    var onClosed: ((Int) -> Void)?                    // voice gateway close code; no more reconnects
    var onOpus: ((UInt64, UInt16, Data) -> Void)?     // user, RTP sequence, Opus frame
    var onSpeaking: ((UInt64, Bool) -> Void)?
    var onUserJoined: ((UInt64) -> Void)?
    var onUserLeft: ((UInt64) -> Void)?
    var onPing: ((Int) -> Void)?

    var isReady: Bool { transport != nil }

    init(endpoint: String, serverId: String, userId: UInt64, channelId: UInt64, sessionId: String, token: String) {
        var host = endpoint
        while host.hasSuffix("/") { host.removeLast() }
        if !host.hasPrefix("ws://") && !host.hasPrefix("wss://") { host = "wss://" + host }
        url = URL(string: host + "/?v=8")!
        self.serverId = serverId
        self.userId = userId
        self.sessionId = sessionId
        self.token = token
        dave = DaveSession(selfUserId: userId, channelId: channelId)
        dave.onSendBinary = { [weak self] op, payload in self?.sendBinary(op, Data(payload)) }
        dave.onSendJson = { [weak self] op, d in self?.send(op, d) }
        dave.onLog = { Log.write($0) }
    }

    func start() { q.async { self.connect() } }

    func stop() {
        q.async {
            self.stopped = true
            self.heartbeat?.cancel()
            self.keepAliveTimer?.cancel()
            self.ws?.cancel(with: .normalClosure, reason: nil)
            self.udp?.cancel()
        }
    }

    private static func trace(_ s: String) { Log.write("voice: " + s) }

    // Codes after which reconnecting can't help.
    private static func fatal(_ code: Int) -> Bool { [4004, 4006, 4011, 4012, 4014, 4016, 4021, 4022].contains(code) }

    private func connect() {
        guard !stopped else { return }
        onState?(resumable ? "Reconnecting..." : "Connecting...")
        var req = URLRequest(url: url)
        req.setValue("https://discord.com", forHTTPHeaderField: "Origin")
        req.setValue(ClientInfo.userAgent, forHTTPHeaderField: "User-Agent")
        let task = http.webSocketTask(with: req)
        ws = task
        task.resume()
        VoiceConnection.trace((resumable ? "resuming " : "connecting to ") + (url.host ?? ""))
        if resumable {
            send(7, ["server_id": serverId, "session_id": sessionId, "token": token, "seq_ack": lastSeq])
        } else {
            send(0, ["server_id": serverId, "user_id": String(userId), "session_id": sessionId, "token": token,
                     "max_dave_protocol_version": DaveSession.maxProtocolVersion])
        }
        receive(task)
    }

    private func receive(_ task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            guard let self else { return }
            self.q.async {
                guard task === self.ws else { return }
                switch result {
                case .success(.data(let d)):
                    self.handleBinary(d)
                    self.receive(task)
                case .success(.string(let s)):
                    if let p = Json.parse(Data(s.utf8)) as? JObj { self.handleJson(p) }
                    self.receive(task)
                case .success:
                    self.receive(task)
                case .failure:
                    self.q.asyncAfter(deadline: .now() + 0.15) { self.closed(self.closeCodes.code(for: task)) }
                }
            }
        }
    }

    private func closed(_ code: Int) {
        heartbeat?.cancel()
        if stopped { return }
        VoiceConnection.trace("voice server closed the connection (code \(code))")
        attempts += 1
        if VoiceConnection.fatal(code) || attempts > 3 || transport == nil {
            onState?("Disconnected")
            onClosed?(code)
            stopped = true
            udp?.cancel()
            keepAliveTimer?.cancel()
            return
        }
        resumable = true
        q.asyncAfter(deadline: .now() + .seconds(attempts)) { self.connect() }
    }

    // Binary DAVE messages: sequence number, opcode, payload.
    private func handleBinary(_ data: Data) {
        let b = [UInt8](data)
        guard b.count >= 3 else { return }
        VoiceConnection.trace("received DAVE op \(b[2]) (\(b.count - 3) bytes)")
        lastSeq = Int64(b[0]) << 8 | Int64(b[1])
        dave.onBinary(op: Int(b[2]), payload: Array(b[3...]))
    }

    private func handleJson(_ p: JObj) {
        if let s = p["seq"] as? NSNumber { lastSeq = s.int64Value }
        let d = p.obj("d") ?? JObj()
        let op = p.long("op", -1)
        if op != 6 && op != 4 { VoiceConnection.trace("received op \(op): \(Json.string(d))") }
        switch op {
        case 12:  // a client's audio/video SSRCs; sent for people already in the call
            guard let uid = UInt64(d.str("user_id") ?? "") else { break }
            let audio = d.long("audio_ssrc")
            if audio > 0 { ssrcUsers[UInt32(truncatingIfNeeded: audio)] = uid }
            dave.setMember(uid, inCall: true)
        case 8:   // hello
            startHeartbeat(Int(d.long("heartbeat_interval", 13750)))
        case 2:   // ready
            ssrc = UInt32(truncatingIfNeeded: d.long("ssrc"))
            let modes = d.strs("modes")
            if !modes.isEmpty && !modes.contains(VoiceConnection.mode) {
                VoiceConnection.trace("voice server doesn't offer \(VoiceConnection.mode)")
                closed(4016)
                return
            }
            setUpUdp(d.str("ip") ?? "", UInt16(d.long("port")))
        case 4:   // session description
            VoiceConnection.trace("session description: mode \(d.str("mode") ?? ""), DAVE version \(d.long("dave_protocol_version"))")
            let key = (d.arr("secret_key") ?? []).compactMap { ($0 as? NSNumber)?.uint8Value }
            transport = SymmetricKey(data: key)
            dave.start(protocolVersion: Int(d.long("dave_protocol_version")))
            attempts = 0
            onState?("Connected")
            onReady?()
        case 9:   // resumed
            attempts = 0
            onState?("Connected")
        case 6:   // heartbeat ack
            if d.has("t") { onPing?(Int(Int64(Date().timeIntervalSince1970 * 1000) - d.long("t"))) }
        case 5:   // someone's speaking state, which also tells us their SSRC
            guard let uid = UInt64(d.str("user_id") ?? "") else { break }
            ssrcUsers[UInt32(truncatingIfNeeded: d.long("ssrc"))] = uid
            onSpeaking?(uid, d.long("speaking") != 0)
        case 11:  // clients connected
            for s in d.strs("user_ids") {
                guard let uid = UInt64(s) else { continue }
                dave.setMember(uid, inCall: true)
                onUserJoined?(uid)
            }
        case 13:  // client disconnected
            guard let uid = UInt64(d.str("user_id") ?? "") else { break }
            dave.setMember(uid, inCall: false)
            ssrcUsers = ssrcUsers.filter { $0.value != uid }
            onUserLeft?(uid)
        case 21: dave.onPrepareTransition(transitionId: Int(d.long("transition_id")), protocolVersion: Int(d.long("protocol_version")))
        case 22: dave.onExecuteTransition(transitionId: Int(d.long("transition_id")))
        case 24: dave.onPrepareEpoch(epoch: Int(d.long("epoch")), protocolVersion: Int(d.long("protocol_version")))
        default: break
        }
    }

    private func startHeartbeat(_ interval: Int) {
        heartbeat?.cancel()
        let t = DispatchSource.makeTimerSource(queue: q)
        t.schedule(deadline: .now(), repeating: .milliseconds(interval))
        t.setEventHandler { [weak self] in
            guard let self else { return }
            self.send(3, ["t": Int64(Date().timeIntervalSince1970 * 1000), "seq_ack": self.lastSeq])
        }
        heartbeat = t
        t.resume()
    }

    func send(_ op: Int, _ d: JObj) {
        if op == 0 { VoiceConnection.trace("sent op 0 (identify), max DAVE version \(DaveSession.maxProtocolVersion)") }
        else if op != 3 && op != 5 && op != 7 { VoiceConnection.trace("sent op \(op): \(Json.string(d))") }
        ws?.send(.string(Json.string(["op": op, "d": d]))) { e in if let e { Log.write("voice send: \(e.localizedDescription)") } }
    }

    private func sendBinary(_ op: Int, _ payload: Data) {
        VoiceConnection.trace("sent DAVE op \(op) (\(payload.count) bytes)")
        ws?.send(.data(Data([UInt8(op)]) + payload)) { e in if let e { Log.write("voice send: \(e.localizedDescription)") } }
    }

    // MARK: - UDP

    private func setUpUdp(_ ip: String, _ port: UInt16) {
        udp?.cancel()
        let c = NWConnection(host: NWEndpoint.Host(ip), port: NWEndpoint.Port(rawValue: port)!, using: .udp)
        udp = c
        c.start(queue: q)
        // IP discovery: ask the server what address and port it sees us on.
        var probe = [UInt8](repeating: 0, count: 74)
        probe[1] = 1
        probe[3] = 70
        VoiceConnection.writeU32(&probe, 4, ssrc)
        discover(c, Data(probe), attempt: 0)
    }

    private func discover(_ c: NWConnection, _ probe: Data, attempt: Int) {
        guard c === udp, !stopped else { return }
        if attempt >= 3 {
            VoiceConnection.trace("voice server didn't answer IP discovery")
            closed(4016)
            return
        }
        var answered = false
        c.send(content: probe, completion: .contentProcessed { _ in })
        c.receiveMessage { [weak self] data, _, _, _ in
            guard let self else { return }
            self.q.async {
                guard !answered, let r = data.map({ [UInt8]($0) }), r.count >= 74, r[1] == 2 else { return }
                answered = true
                var end = 8
                while end < 72 && r[end] != 0 { end += 1 }
                let address = String(decoding: r[8..<end], as: UTF8.self)
                let ourPort = Int(r[72]) << 8 | Int(r[73])
                self.send(1, ["protocol": "udp", "data": ["address": address, "port": ourPort, "mode": VoiceConnection.mode] as JObj])
                self.receiveUdp(c)
                self.startKeepAlive(c)
            }
        }
        q.asyncAfter(deadline: .now() + 2) { if !answered { self.discover(c, probe, attempt: attempt + 1) } }
    }

    // Keeps NAT mappings open while nobody talks.
    private func startKeepAlive(_ c: NWConnection) {
        keepAliveTimer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: q)
        t.schedule(deadline: .now() + 5, repeating: 5)
        t.setEventHandler { [weak self] in
            guard let self, c === self.udp else { return }
            var v = self.keepAlive.littleEndian
            self.keepAlive += 1
            c.send(content: Data(bytes: &v, count: 8), completion: .contentProcessed { _ in })
        }
        keepAliveTimer = t
        t.resume()
    }

    private func receiveUdp(_ c: NWConnection) {
        c.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            self.q.async {
                guard c === self.udp, !self.stopped else { return }
                if let data { self.handlePacket([UInt8](data)) }
                if error == nil || c.state == .ready { self.receiveUdp(c) }
            }
        }
    }

    private func handlePacket(_ packet: [UInt8]) {
        if packet.count <= 8 || (packet[1] >= 200 && packet[1] <= 204) { return }   // keepalive echoes and RTCP
        if packet[1] & 0x7F != VoiceConnection.payloadType { return }   // WebRTC clients also send video/RTX keepalives and padding probes
        guard let key = transport, let (seq, packetSsrc, payload) = VoiceConnection.openRtp(key, packet) else { return }
        guard let uid = ssrcUsers[packetSsrc] else { return }
        guard let frame = dave.decrypt(payload, from: uid) else { return }
        onOpus?(uid, seq, Data(frame))
    }

    // MARK: - RTP with aead_aes256_gcm_rtpsize

    // Sends one Opus frame (20 ms): DAVE first, then RTP and transport encryption.
    func sendOpus(_ opus: Data) {
        q.async {
            guard let key = self.transport, let c = self.udp else { return }
            let frame = self.dave.encrypt([UInt8](opus))
            self.sendNonce &+= 1
            guard let packet = VoiceConnection.sealRtp(key, self.rtpSeq, self.rtpTimestamp, self.ssrc, self.sendNonce, frame) else { return }
            self.rtpSeq &+= 1
            self.rtpTimestamp &+= 960
            c.send(content: Data(packet), completion: .contentProcessed { _ in })
        }
    }

    func setSpeaking(_ speaking: Bool) {
        q.async { self.send(5, ["speaking": speaking ? 1 : 0, "delay": 0, "ssrc": self.ssrc]) }
    }

    static func writeU32(_ b: inout [UInt8], _ o: Int, _ v: UInt32) {
        b[o] = UInt8(v >> 24); b[o + 1] = UInt8((v >> 16) & 0xFF); b[o + 2] = UInt8((v >> 8) & 0xFF); b[o + 3] = UInt8(v & 0xFF)
    }

    static func readU32(_ b: [UInt8], _ o: Int) -> UInt32 {
        UInt32(b[o]) << 24 | UInt32(b[o + 1]) << 16 | UInt32(b[o + 2]) << 8 | UInt32(b[o + 3])
    }

    private static func nonce(_ counter: ArraySlice<UInt8>) -> AES.GCM.Nonce {
        try! AES.GCM.Nonce(data: Array(counter) + [UInt8](repeating: 0, count: 8))
    }

    // Header (the AAD), then ciphertext and tag, then the 4-byte nonce counter.
    static func sealRtp(_ key: SymmetricKey, _ seq: UInt16, _ timestamp: UInt32, _ ssrc: UInt32, _ nonceValue: UInt32, _ payload: [UInt8]) -> [UInt8]? {
        var header = [UInt8](repeating: 0, count: 12)
        header[0] = 0x80
        header[1] = payloadType
        header[2] = UInt8(seq >> 8)
        header[3] = UInt8(seq & 0xFF)
        writeU32(&header, 4, timestamp)
        writeU32(&header, 8, ssrc)
        var counter = [UInt8](repeating: 0, count: 4)
        writeU32(&counter, 0, nonceValue)
        guard let box = try? AES.GCM.seal(payload, using: key, nonce: nonce(counter[...]), authenticating: header) else { return nil }
        return header + [UInt8](box.ciphertext) + [UInt8](box.tag) + counter
    }

    // The RTP payload of a voice packet, or nil for RTCP, junk or anything that fails to decrypt.
    // The header and any extension preamble are the AAD; the extension body is encrypted.
    static func openRtp(_ key: SymmetricKey, _ p: [UInt8]) -> (UInt16, UInt32, [UInt8])? {
        guard p.count >= 12 + 16 + 4, p[0] >> 6 == 2, !(p[1] >= 200 && p[1] <= 204) else { return nil }
        var header = 12 + 4 * Int(p[0] & 0x0F)
        let extensionPresent = p[0] & 0x10 != 0
        var extensionWords = 0
        if extensionPresent {
            header += 4
            guard p.count >= header + 16 + 4 else { return nil }
            extensionWords = Int(p[header - 2]) << 8 | Int(p[header - 1])
        }
        guard p.count >= header + 16 + 4 else { return nil }
        let seq = UInt16(p[2]) << 8 | UInt16(p[3])
        let ssrc = readU32(p, 8)
        let body = p[header..<(p.count - 4 - 16)]
        let tag = p[(p.count - 4 - 16)..<(p.count - 4)]
        guard let box = try? AES.GCM.SealedBox(nonce: nonce(p[(p.count - 4)...]), ciphertext: body, tag: tag),
              let plainData = try? AES.GCM.open(box, using: key, authenticating: p[0..<header]) else { return nil }
        let plain = [UInt8](plainData)
        let skip = extensionPresent ? extensionWords * 4 : 0
        var end = plain.count
        if p[0] & 0x20 != 0 && end > 0 { end -= Int(plain[end - 1]) }   // RTP padding
        guard skip <= end else { return nil }
        return (seq, ssrc, Array(plain[skip..<end]))
    }
}
