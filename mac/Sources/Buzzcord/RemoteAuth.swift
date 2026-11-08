import CryptoKit
import Foundation
import Security

// "Log in with QR code": the same flow the Discord web app uses. The phone app scans a QR code
// holding a fingerprint of a key made here; Discord then sends the account token back
// encrypted with that key, so it never travels in the clear.
@MainActor
final class RemoteAuth {
    static let url = URL(string: "wss://remote-auth-gateway.discord.gg/?v=2")!

    var onQr: ((String) -> Void)?                 // URL to put in the QR code
    var onScanned: ((String, String) -> Void)?    // username, avatar URL
    var onSuccess: ((String) -> Void)?            // token
    var onFailed: ((String) -> Void)?             // message

    private let privateKey: SecKey
    private let closeCodes = SocketCloseCodes()
    private lazy var session = SocketCloseCodes.makeSession(closeCodes)
    private var ws: URLSessionWebSocketTask?
    private var heartbeat: Task<Void, Never>?
    private var cancelled = false

    init() {
        let attrs: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeRSA, kSecAttrKeySizeInBits as String: 2048]
        privateKey = SecKeyCreateRandomKey(attrs as CFDictionary, nil)!
    }

    func start() { Task { await run() } }

    func cancel() {
        cancelled = true
        heartbeat?.cancel()
        ws?.cancel(with: .normalClosure, reason: nil)
    }

    private func run() async {
        var finished = false
        var req = URLRequest(url: RemoteAuth.url)
        req.setValue("https://discord.com", forHTTPHeaderField: "Origin")
        req.setValue(ClientInfo.userAgent, forHTTPHeaderField: "User-Agent")
        let task = session.webSocketTask(with: req)
        ws = task
        task.resume()
        defer { heartbeat?.cancel(); task.cancel() }
        do {
            while !cancelled {
                let msg = try await task.receive()
                let data: Data
                switch msg {
                case .string(let s): data = Data(s.utf8)
                case .data(let d): data = d
                @unknown default: continue
                }
                guard let p = Json.parse(data) as? JObj else { continue }
                switch p.str("op") {
                case "hello":
                    startHeartbeat(Int(p.long("heartbeat_interval", 41250)))
                    try await send(["op": "init", "encoded_public_key": publicKeyDer().base64EncodedString()])
                case "nonce_proof":
                    let nonce = try decrypt(p.str("encrypted_nonce") ?? "")
                    let hash = Data(SHA256.hash(data: nonce))
                    let proof = hash.base64EncodedString().replacingOccurrences(of: "=", with: "")
                        .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
                    try await send(["op": "nonce_proof", "proof": proof])
                case "pending_remote_init":
                    onQr?("https://discord.com/ra/" + (p.str("fingerprint") ?? ""))
                case "pending_ticket":
                    // "id:discriminator:avatar:username"
                    let text = String(decoding: try decrypt(p.str("encrypted_user_payload") ?? ""), as: UTF8.self)
                    let parts = text.split(separator: ":", maxSplits: 3, omittingEmptySubsequences: false).map(String.init)
                    if parts.count == 4 {
                        let u = User()
                        u.id = parts[0]; u.discriminator = parts[1]; u.avatar = parts[2] == "0" ? nil : parts[2]; u.username = parts[3]
                        onScanned?(u.username ?? "", u.avatarUrl(128))
                    }
                case "pending_login":
                    let token = try await exchangeTicket(p.str("ticket") ?? "")
                    finished = true
                    onSuccess?(token)
                    return
                case "finish": // older protocol variant
                    finished = true
                    onSuccess?(String(decoding: try decrypt(p.str("encrypted_token") ?? ""), as: UTF8.self))
                    return
                case "cancel":
                    finished = true
                    onFailed?("Login was cancelled on the phone.")
                    return
                default:
                    break
                }
            }
        } catch {
            if !cancelled && !finished {
                let code = closeCodes.code(for: task)
                onFailed?(code != 0 ? "The QR code expired. Click \"New code\" to get another." : error.localizedDescription)
            }
        }
    }

    private func exchangeTicket(_ ticket: String) async throws -> String {
        var req = URLRequest(url: URL(string: Api.base + "/users/@me/remote-auth/login")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(ClientInfo.superProperties(), forHTTPHeaderField: "X-Super-Properties")
        req.setValue("https://discord.com", forHTTPHeaderField: "Origin")
        req.setValue("https://discord.com/login", forHTTPHeaderField: "Referer")
        req.httpBody = Json.data(["ticket": ticket])
        let (data, resp) = try await Api.plain.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        let j = Json.parse(data) as? JObj
        guard (200..<300).contains(code), let enc = j?.str("encrypted_token") else {
            throw ApiError(status: code, body: j, raw: String(decoding: data, as: UTF8.self))
        }
        return String(decoding: try decrypt(enc), as: UTF8.self)
    }

    private func startHeartbeat(_ interval: Int) {
        heartbeat = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(interval) * 1_000_000)
                guard let self, !Task.isCancelled else { return }
                try? await self.send(["op": "heartbeat"])
            }
        }
    }

    private func send(_ o: JObj) async throws {
        try await ws?.send(.string(Json.string(o)))
    }

    private func decrypt(_ b64: String) throws -> Data {
        guard let data = Data(base64Encoded: b64) else { throw ApiError(status: 0, body: nil, raw: "bad data from Discord") }
        var err: Unmanaged<CFError>?
        guard let out = SecKeyCreateDecryptedData(privateKey, .rsaEncryptionOAEPSHA256, data as CFData, &err) as Data? else {
            throw err!.takeRetainedValue() as Error
        }
        return out
    }

    // SubjectPublicKeyInfo DER; the Security framework only exports the bare PKCS#1 key.
    private func publicKeyDer() -> Data {
        let pub = SecKeyCopyPublicKey(privateKey)!
        let pkcs1 = SecKeyCopyExternalRepresentation(pub, nil)! as Data
        let algo = RemoteAuth.der(0x30, Data([0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00]))
        let bits = RemoteAuth.der(0x03, Data([0]) + pkcs1)
        return RemoteAuth.der(0x30, algo + bits)
    }

    private static func der(_ tag: UInt8, _ content: Data) -> Data {
        var d = Data([tag])
        let len = content.count
        if len < 0x80 { d.append(UInt8(len)) }
        else if len < 0x100 { d.append(contentsOf: [0x81, UInt8(len)]) }
        else { d.append(contentsOf: [0x82, UInt8(len >> 8), UInt8(len & 0xFF)]) }
        return d + content
    }
}
