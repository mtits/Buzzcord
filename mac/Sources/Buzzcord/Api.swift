import Foundation

struct ApiError: LocalizedError {
    let status: Int
    let body: JObj?
    let raw: String?

    var isCaptcha: Bool { body?.has("captcha_sitekey") == true }

    var errorDescription: String? {
        if isCaptcha { return "Discord asked for a captcha and it wasn't solved, so nothing was sent. Try again to get a new one." }
        if status == 413 { return "That file is too big for Discord (10 MB without Nitro)." }
        var msg = body?.str("message") ?? ""
        if msg.isEmpty { msg = (raw?.count ?? 999) < 200 ? raw ?? "request failed" : "request failed" }
        return "Discord said: \(msg) (\(status))"
    }
}

// Identity of this client. Discord expects every request to describe the client it comes
// from, so Buzzcord reports itself the way the Discord web app on a Mac does.
enum ClientInfo {
    static let userAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
    nonisolated(unsafe) static var build = 657850

    static func properties() -> JObj {
        [
            "os": "Mac OS X",
            "browser": "Chrome",
            "device": "",
            "system_locale": Locale.preferredLanguages.first ?? "en-US",
            "browser_user_agent": userAgent,
            "browser_version": "140.0.0.0",
            "os_version": "10.15.7",
            "referrer": "",
            "referring_domain": "",
            "referrer_current": "",
            "referring_domain_current": "",
            "release_channel": "stable",
            "client_build_number": build,
            "client_event_source": JNull,
        ]
    }

    static func superProperties() -> String { Json.data(properties()).base64EncodedString() }

    // The web app's build number changes every few days. It is read from the web app's own
    // files once a day; the last known value is kept in settings.
    static func loadBuildNumber() async {
        let d = UserDefaults.standard
        let saved = d.integer(forKey: "BuildNumber")
        if saved > 0 { build = saved }
        if let checked = d.object(forKey: "BuildNumberChecked") as? Date, Date().timeIntervalSince(checked) < 20 * 3600 { return }
        do {
            let html = try await Api.getString("https://discord.com/app")
            let re = try NSRegularExpression(pattern: "src=\"(/assets/sentry\\.[^\"]+\\.js)\"")
            for m in re.matches(in: html, range: NSRange(html.startIndex..., in: html)) {
                guard let r = Range(m.range(at: 1), in: html) else { continue }
                let js = try await Api.getString("https://discord.com" + html[r])
                let b = try NSRegularExpression(pattern: "\"buildNumber\",\"(\\d+)\"")
                if let bm = b.firstMatch(in: js, range: NSRange(js.startIndex..., in: js)), let br = Range(bm.range(at: 1), in: js), let n = Int(js[br]) {
                    build = n
                    d.set(n, forKey: "BuildNumber")
                    break
                }
            }
            d.set(Date(), forKey: "BuildNumberChecked")
        } catch {
            Log.write("build number: \(error.localizedDescription)")
        }
    }
}

final class Api: @unchecked Sendable {
    static let base = "https://discord.com/api/v9"
    // Plugged in by the app: shows Discord's captcha and returns the solved key (nil if closed).
    nonisolated(unsafe) static var solveCaptcha: ((JObj) async -> String?)?

    let token: String
    private let http: URLSession

    static let plain: URLSession = {
        let c = URLSessionConfiguration.default
        c.httpAdditionalHeaders = ["User-Agent": ClientInfo.userAgent, "Accept-Language": "en-US,en;q=0.9"]
        c.timeoutIntervalForRequest = 100
        c.urlCache = nil
        return URLSession(configuration: c)
    }()

    init(token: String) {
        self.token = token
        let c = URLSessionConfiguration.default
        c.httpAdditionalHeaders = [
            "User-Agent": ClientInfo.userAgent,
            "Accept-Language": "en-US,en;q=0.9",
            "Authorization": token,
            "X-Super-Properties": ClientInfo.superProperties(),
            "X-Discord-Locale": "en-US",
            "X-Discord-Timezone": TimeZone.current.identifier,
            "Origin": "https://discord.com",
            "Referer": "https://discord.com/channels/@me",
        ]
        c.timeoutIntervalForRequest = 100
        c.urlCache = nil
        http = URLSession(configuration: c)
    }

    @discardableResult func get(_ path: String) async throws -> Any? { try await send("GET", path, nil) }
    @discardableResult func post(_ path: String, _ body: Any?) async throws -> Any? { try await send("POST", path, body) }
    @discardableResult func patch(_ path: String, _ body: Any?) async throws -> Any? { try await send("PATCH", path, body) }
    @discardableResult func put(_ path: String, _ body: Any?) async throws -> Any? { try await send("PUT", path, body) }
    @discardableResult func delete(_ path: String) async throws -> Any? { try await send("DELETE", path, nil) }

    // Friend actions carry X-Context-Properties: where in the app they came from.
    @discardableResult func send(_ method: String, _ path: String, _ body: Any?, location: String) async throws -> Any? {
        let ctx = Json.data(["location": location]).base64EncodedString()
        return try await send(method, path, body, headers: ["X-Context-Properties": ctx])
    }

    // Fire and forget.
    func fire(_ method: String, _ path: String, _ body: Any?) {
        Task { _ = try? await self.send(method, path, body) }
    }

    @discardableResult func send(_ method: String, _ path: String, _ body: Any?, headers: [String: String] = [:]) async throws -> Any? {
        var headers = headers
        var captchaTried = false
        var attempt = 0
        while true {
            var req = URLRequest(url: URL(string: path.hasPrefix("http") ? path : Api.base + path)!)
            req.httpMethod = method
            for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
            if let body {
                req.httpBody = Json.data(body)
                req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            } else if method == "POST" || method == "PUT" {
                req.httpBody = Data()
                req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            }
            let (data, resp) = try await http.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            let text = String(decoding: data, as: UTF8.self)
            if code == 429 && attempt < 3 {
                attempt += 1
                let wait = (Json.parse(data) as? JObj).flatMap { ($0["retry_after"] as? NSNumber)?.doubleValue } ?? 1
                try await Task.sleep(nanoseconds: UInt64(min(max(wait, 0.5), 30) * 1e9))
                continue
            }
            if (200..<300).contains(code) { return data.isEmpty ? nil : Json.parse(data) }
            let err = Json.parse(data) as? JObj
            if code == 400, let err, err.has("captcha_sitekey"), !captchaTried, let solve = Api.solveCaptcha {
                // Show Discord's captcha to the user, then send the same request again with
                // their answer, exactly as the official app does.
                captchaTried = true
                if let answer = await solve(err) {
                    headers["X-Captcha-Key"] = answer
                    if let rq = err.str("captcha_rqtoken") { headers["X-Captcha-Rqtoken"] = rq }
                    if let sid = err.str("captcha_session_id") { headers["X-Captcha-Session-Id"] = sid }
                    continue
                }
            }
            throw ApiError(status: code, body: err, raw: text)
        }
    }

    static func nonce() -> String { Snowflake.now() }

    @discardableResult func sendMessage(_ channelId: String, _ content: String, replyTo: String?, attachments: [JObj]) async throws -> Any? {
        var body: JObj = ["content": content, "nonce": Api.nonce(), "tts": false, "flags": 0]
        if let replyTo { body["message_reference"] = ["channel_id": channelId, "message_id": replyTo] }
        if !attachments.isEmpty { body["attachments"] = attachments }
        return try await post("/channels/\(channelId)/messages", body)
    }

    // Discord's upload flow: ask for an upload slot, PUT the bytes there, then send a message
    // that names the uploaded file.
    func uploadFile(_ channelId: String, _ file: URL, index: Int) async throws -> JObj {
        let size = (try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        let slot = try await post("/channels/\(channelId)/attachments", ["files": [
            ["filename": file.lastPathComponent, "file_size": size, "id": String(index), "is_clip": false] as JObj,
        ]]) as? JObj
        guard let att = slot?.objs("attachments").first, let url = att.str("upload_url").flatMap(URL.init(string:)) else {
            throw ApiError(status: 0, body: nil, raw: "Discord did not give an upload slot.")
        }
        var req = URLRequest(url: url)
        req.httpMethod = "PUT"
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        req.timeoutInterval = 1800
        let (_, resp) = try await Api.plain.upload(for: req, fromFile: file)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if !(200..<300).contains(code) { throw ApiError(status: code, body: nil, raw: "Upload failed") }
        return ["id": String(index), "filename": file.lastPathComponent, "uploaded_filename": att.str("upload_filename") ?? ""]
    }

    static func getString(_ url: String) async throws -> String {
        let (data, _) = try await plain.data(from: URL(string: url)!)
        return String(decoding: data, as: UTF8.self)
    }
}

enum Log {
    static let url: URL = {
        let dir = AppPaths.support
        return dir.appendingPathComponent("buzzcord.log")
    }()

    static func write(_ line: String) {
        let stamp = ISO8601DateFormatter().string(from: Date())
        let data = Data("\(stamp) \(line)\n".utf8)
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile()
            h.write(data)
            try? h.close()
        } else {
            try? data.write(to: url)
        }
    }
}

enum AppPaths {
    static let support: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Buzzcord")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()
}
