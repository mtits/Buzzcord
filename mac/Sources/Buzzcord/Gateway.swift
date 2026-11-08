import Foundation

// Remembers the close code each websocket ended with; URLSession reports it on the delegate,
// sometimes after receive() has already failed.
final class SocketCloseCodes: NSObject, URLSessionWebSocketDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var codes: [Int: Int] = [:]

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        lock.lock(); codes[webSocketTask.taskIdentifier] = closeCode.rawValue; lock.unlock()
    }

    func code(for task: URLSessionWebSocketTask) -> Int {
        lock.lock(); defer { lock.unlock() }
        if let c = codes.removeValue(forKey: task.taskIdentifier) { return c }
        let raw = task.closeCode.rawValue
        return raw == URLSessionWebSocketTask.CloseCode.invalid.rawValue ? 0 : raw
    }

    static func makeSession(_ delegate: SocketCloseCodes) -> URLSession {
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = 3600 * 24 * 365
        return URLSession(configuration: c, delegate: delegate, delegateQueue: nil)
    }
}

// One live connection to Discord's gateway for one account. Runs on the main actor (awaiting
// never blocks it; big payloads are parsed off it) and reconnects and resumes by itself.
@MainActor
final class Gateway {
    static let defaultUrl = "wss://gateway.discord.gg"
    // DEDUPE_USER_OBJECTS: users are sent once in READY.users and referenced by id elsewhere.
    static let capabilities = 16

    private let token: String
    private let presenceSource: () -> JObj
    private let closeCodes = SocketCloseCodes()
    private lazy var urlSession = SocketCloseCodes.makeSession(closeCodes)
    private var ws: URLSessionWebSocketTask?
    private var heartbeat: Task<Void, Never>?
    private var sessionId: String?
    private var resumeUrl: String?
    private var seq: Int64 = -1
    private var acked = true
    private var stopped = false
    private(set) var connected = false

    var onDispatch: ((String, JObj) -> Void)?
    var onState: ((String) -> Void)?
    var onAuthFailed: (() -> Void)?
    var onClosed: ((Int) -> Void)?   // close code from Discord

    init(token: String, presence: @escaping () -> JObj) {
        self.token = token
        presenceSource = presence
    }

    func start() {
        stopped = false
        Task { await runLoop() }
    }

    func stop() {
        stopped = true
        heartbeat?.cancel()
        ws?.cancel(with: .normalClosure, reason: nil)
    }

    private func runLoop() async {
        var failures = 0
        while !stopped {
            let resume = sessionId != nil && seq >= 0
            let url = URL(string: (resume ? resumeUrl ?? Gateway.defaultUrl : Gateway.defaultUrl) + "/?encoding=json&v=9")!
            raise(resume ? "Reconnecting..." : "Connecting...")
            var req = URLRequest(url: url)
            req.setValue("https://discord.com", forHTTPHeaderField: "Origin")
            req.setValue(ClientInfo.userAgent, forHTTPHeaderField: "User-Agent")
            let task = urlSession.webSocketTask(with: req)
            task.maximumMessageSize = 256 << 20   // READY for a big account is many megabytes
            ws = task
            task.resume()
            let (closeCode, gotHello) = await receiveLoop(task, resume: resume)
            connected = false
            heartbeat?.cancel()
            task.cancel()
            if stopped { break }
            failures = gotHello ? 0 : failures + 1
            if closeCode != 0 { onClosed?(closeCode) }
            if closeCode == 4004 {
                raise("Signed out (token no longer valid)")
                onAuthFailed?()
                return
            }
            // These codes mean the session can't be resumed.
            if [4003, 4007, 4009].contains(closeCode) { sessionId = nil; seq = -1 }
            raise("Disconnected, retrying...")
            let delay = min(30_000, 1000 * (1 << min(failures, 5))) + Int.random(in: 0..<500)
            try? await Task.sleep(nanoseconds: UInt64(delay) * 1_000_000)
        }
        raise("Offline")
    }

    private func receiveLoop(_ task: URLSessionWebSocketTask, resume: Bool) async -> (Int, Bool) {
        var gotHello = false
        while !stopped {
            let msg: URLSessionWebSocketTask.Message
            do { msg = try await task.receive() } catch {
                if !stopped { Log.write("gateway: \(error.localizedDescription)") }
                try? await Task.sleep(nanoseconds: 150_000_000)
                return (closeCodes.code(for: task), gotHello)
            }
            let data: Data
            switch msg {
            case .string(let s): data = Data(s.utf8)
            case .data(let d): data = d
            @unknown default: continue
            }
            let parsed = await Task.detached(priority: .userInitiated) { Json.parse(data) as? JObj }.value
            guard let p = parsed else { continue }
            switch p.long("op", -1) {
            case 10: // hello
                gotHello = true
                startHeartbeat(task, interval: Int(p.obj("d")?.long("heartbeat_interval", 41250) ?? 41250))
                if resume { send(["op": 6, "d": ["token": token, "session_id": sessionId ?? "", "seq": seq] as JObj]) }
                else { identify() }
            case 11:
                acked = true
            case 1:
                send(["op": 1, "d": seq >= 0 ? seq as Any : JNull])
            case 7: // server asks us to reconnect and resume
                // A 1000 close would end the session; any 4000-range code keeps it resumable.
                task.cancel(with: URLSessionWebSocketTask.CloseCode(rawValue: 4000) ?? .goingAway, reason: nil)
                return (0, gotHello)
            case 9: // invalid session
                let canResume = p["d"] as? Bool ?? false
                if !canResume { sessionId = nil; seq = -1 }
                try? await Task.sleep(nanoseconds: UInt64(1000 + Int.random(in: 0..<4000)) * 1_000_000)
                if canResume { send(["op": 6, "d": ["token": token, "session_id": sessionId ?? "", "seq": seq] as JObj]) }
                else { identify() }
            case 0:
                if let s = p["s"] as? NSNumber { seq = s.int64Value }
                let t = p.str("t") ?? ""
                var d = p.obj("d")
                // A few events (SESSIONS_REPLACE) carry a bare array; hand it over as d.list.
                if d == nil, let list = p["d"] as? [Any] { d = ["list": list] }
                if t == "READY" {
                    sessionId = d?.str("session_id")
                    resumeUrl = d?.str("resume_gateway_url")
                    connected = true
                    raise("Online")
                } else if t == "RESUMED" {
                    connected = true
                    raise("Online")
                }
                if let d { onDispatch?(t, d) }
            default:
                break
            }
        }
        return (0, gotHello)
    }

    private func identify() {
        send(["op": 2, "d": [
            "token": token,
            "capabilities": Gateway.capabilities,
            "properties": ClientInfo.properties(),
            "presence": presenceSource(),
            "compress": false,
            "client_state": ["guild_versions": JObj()] as JObj,
        ] as JObj])
    }

    private func startHeartbeat(_ task: URLSessionWebSocketTask, interval: Int) {
        acked = true
        heartbeat?.cancel()
        heartbeat = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(Double(interval) * Double.random(in: 0..<1)) * 1_000_000)
            while !Task.isCancelled {
                guard let self, self.ws === task else { return }
                if !self.acked {
                    // No answer to the last beat: the connection is dead. Drop it and resume.
                    Log.write("gateway: heartbeat not acknowledged, reconnecting")
                    task.cancel(with: URLSessionWebSocketTask.CloseCode(rawValue: 4000) ?? .goingAway, reason: nil)
                    return
                }
                self.acked = false
                self.send(["op": 1, "d": self.seq >= 0 ? self.seq as Any : JNull])
                try? await Task.sleep(nanoseconds: UInt64(interval) * 1_000_000)
            }
        }
    }

    func send(_ payload: JObj) {
        guard let ws, ws.state == .running else { return }
        let text = Json.string(payload)
        ws.send(.string(text)) { error in
            if let error { Log.write("gateway send: \(error.localizedDescription)") }
        }
    }

    func updatePresence(_ presence: JObj) { send(["op": 3, "d": presence]) }

    private func raise(_ state: String) { onState?(state) }
}
