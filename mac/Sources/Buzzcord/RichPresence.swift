import Foundation
import Darwin

// Stands in for the Discord app on the local socket games use for rich presence
// ($TMPDIR/discord-ipc-N, the same place Discord's own Mac app listens). Games connect here
// instead and their activity goes to every signed-in account. Nothing here touches the game
// process itself. Port of src/RpcServer.cs.
final class RpcServer: @unchecked Sendable {
    private let dir: String
    private let lock = NSLock()
    private var listeners: [(fd: Int32, path: String)] = []
    private var conns: Set<Int32> = []
    private var stopped = true
    private var nextId = 0

    // Called on a socket thread: connection id, the game's Discord app id, its activity (nil clears it).
    var onActivity: @Sendable (Int, String?, JObj?) -> Void = { _, _, _ in }
    // The signed-in user the game is told about.
    var user: @Sendable () -> JObj = { ["id": "0", "username": "buzzcord", "discriminator": "0"] }
    var log: @Sendable (String) -> Void = { Log.write($0) }

    // Discord's clients look in XDG_RUNTIME_DIR, TMPDIR, TMP, TEMP, then /tmp.
    init(dir: String? = nil) {
        let env = ProcessInfo.processInfo.environment
        self.dir = dir ?? ["XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"].lazy.compactMap { env[$0] }.first ?? "/tmp"
    }

    var paths: [String] { lock.withLock { listeners.map(\.path) } }

    private func path(_ i: Int) -> String { URL(fileURLWithPath: dir).appendingPathComponent("discord-ipc-\(i)").path }

    // Listens on the first free discord-ipc-N. False when all ten are taken.
    @discardableResult func start() -> Bool {
        lock.withLock { stopped = false }
        if !paths.isEmpty { return true }
        for i in 0..<10 where listen(on: path(i)) { return true }
        return false
    }

    // Starts listening on discord-ipc-0 too once nothing else holds it (games try 0 first).
    func claimPrimary() {
        let p = path(0)
        if lock.withLock({ stopped }) || paths.contains(p) { return }
        if listen(on: p) { log("rich presence: now listening on discord-ipc-0") }
    }

    func stop() {
        let (ls, cs) = lock.withLock { () -> ([(fd: Int32, path: String)], Set<Int32>) in
            stopped = true
            defer { listeners = []; conns = [] }
            return (listeners, conns)
        }
        for l in ls { Darwin.shutdown(l.fd, SHUT_RDWR); close(l.fd); unlink(l.path) }
        for c in cs { Darwin.shutdown(c, SHUT_RDWR) }
    }

    private static func address(_ path: String) -> sockaddr_un? {
        var a = sockaddr_un()
        a.sun_family = sa_family_t(AF_UNIX)
        a.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        let bytes = Array(path.utf8)
        guard bytes.count < MemoryLayout.size(ofValue: a.sun_path) else { return nil }
        withUnsafeMutableBytes(of: &a.sun_path) { buf in
            buf.copyBytes(from: bytes)
            buf[bytes.count] = 0
        }
        return a
    }

    private static func with(_ a: inout sockaddr_un, _ body: (UnsafePointer<sockaddr>, socklen_t) -> Int32) -> Int32 {
        withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { body($0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
    }

    // Something (usually the Discord app) already answers on this socket.
    static func inUse(_ path: String) -> Bool {
        guard var a = address(path) else { return true }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return true }
        defer { close(fd) }
        return with(&a) { connect(fd, $0, $1) } == 0
    }

    private func listen(on path: String) -> Bool {
        guard !Self.inUse(path), var a = Self.address(path) else { return false }
        unlink(path)   // a socket file left behind by an app that quit
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        guard Self.with(&a, { Darwin.bind(fd, $0, $1) }) == 0, Darwin.listen(fd, 8) == 0 else {
            log("rich presence: can't listen on \(path): \(String(cString: strerror(errno)))")
            close(fd)
            return false
        }
        lock.withLock { listeners.append((fd, path)) }
        log("rich presence: listening on \(path)")
        Thread { [self] in acceptLoop(fd) }.start()
        return true
    }

    private func acceptLoop(_ fd: Int32) {
        while true {
            let c = accept(fd, nil, nil)
            if c < 0 {
                if lock.withLock({ stopped || !listeners.contains { $0.fd == fd } }) { return }
                usleep(200_000)
                continue
            }
            var on: Int32 = 1
            setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            let id = lock.withLock { () -> Int in nextId += 1; conns.insert(c); return nextId }
            Thread { [self] in serve(c, id) }.start()
        }
    }

    private func serve(_ fd: Int32, _ connId: Int) {
        var clientId: String?
        defer {
            onActivity(connId, clientId, nil)
            if let clientId { log("rich presence: game disconnected (app \(clientId))") }
            lock.withLock { _ = conns.remove(fd) }
            close(fd)
        }
        while let header = readExactly(fd, 8) {
            let op = Int32(littleEndian: header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: Int32.self) })
            let len = Int32(littleEndian: header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 4, as: Int32.self) })
            guard len >= 0, len <= 1 << 20, let body = readExactly(fd, Int(len)) else { return }
            let msg = Json.parse(body) as? JObj
            switch op {
            case 0:   // handshake
                clientId = msg?.str("client_id")
                log("rich presence: game connected (app \(clientId ?? "?"))")
                write(fd, 1, ["cmd": "DISPATCH", "evt": "READY", "nonce": JNull, "data": [
                    "v": 1,
                    "config": ["cdn_host": "cdn.discordapp.com", "api_endpoint": "//discord.com/api", "environment": "production"],
                    "user": user()] as JObj])
            case 1:
                if let msg { command(fd, connId, clientId, msg) }
            case 3:   // ping
                writeRaw(fd, 4, body)
            case 2:   // close
                return
            default:
                break
            }
        }
    }

    private func command(_ fd: Int32, _ connId: Int, _ clientId: String?, _ msg: JObj) {
        let cmd = msg.str("cmd") ?? ""
        let nonce = msg["nonce"] ?? JNull
        let args = msg.obj("args") ?? [:]
        switch cmd {
        case "SET_ACTIVITY":
            let activity = args.obj("activity")
            onActivity(connId, clientId, activity)
            write(fd, 1, ["cmd": cmd, "evt": JNull, "nonce": nonce, "data": activity ?? JNull])
        case "SUBSCRIBE", "UNSUBSCRIBE":
            write(fd, 1, ["cmd": cmd, "evt": JNull, "nonce": nonce, "data": ["evt": msg.str("evt") ?? JNull] as JObj])
        default:
            write(fd, 1, ["cmd": cmd, "evt": "ERROR", "nonce": nonce,
                          "data": ["code": 1000, "message": "Buzzcord only supports rich presence"] as JObj])
        }
    }

    private func write(_ fd: Int32, _ op: Int32, _ payload: JObj) { writeRaw(fd, op, Json.data(payload)) }

    private func writeRaw(_ fd: Int32, _ op: Int32, _ body: Data) {
        var frame = Data()
        withUnsafeBytes(of: op.littleEndian) { frame.append(contentsOf: $0) }
        withUnsafeBytes(of: Int32(body.count).littleEndian) { frame.append(contentsOf: $0) }
        frame.append(body)
        frame.withUnsafeBytes { buf in
            var sent = 0
            while sent < buf.count {
                let n = Darwin.write(fd, buf.baseAddress! + sent, buf.count - sent)
                if n <= 0 { return }
                sent += n
            }
        }
    }

    private func readExactly(_ fd: Int32, _ len: Int) -> Data? {
        var out = Data(count: len)
        var got = 0
        let ok = out.withUnsafeMutableBytes { buf -> Bool in
            while got < len {
                let n = read(fd, buf.baseAddress! + got, len - got)
                if n <= 0 { return false }
                got += n
            }
            return true
        }
        return ok ? out : nil
    }
}

// What games report over the socket, turned into gateway activities and shared on every
// account. Runs only while Options > Show What I'm Playing is on.
@MainActor
final class RichPresence: ObservableObject {
    static let shared = RichPresence()

    struct Entry { let appId: String; let activity: JObj; let order: Int }

    @Published private(set) var entries: [Int: Entry] = [:]
    var api: () -> Api? = { nil }
    var user: () -> JObj? = { nil }
    let server = RpcServer()
    private var order = 0
    private var claimTimer: Timer?
    private var assetIds: [String: [String: String]] = [:]
    private var externalAssets: [String: String] = [:]

    init() {
        server.onActivity = { conn, appId, activity in
            Task { @MainActor in RichPresence.shared.set(conn, appId, activity) }
        }
        server.user = {
            DispatchQueue.main.sync {
                MainActor.assumeIsolated { RichPresence.shared.user() } ?? ["id": "0", "username": "buzzcord", "discriminator": "0"]
            }
        }
    }

    // Newest first, one per game.
    var activities: [JObj] {
        var seen = Set<String>()
        return entries.values.sorted { $0.order > $1.order }.filter { seen.insert($0.appId).inserted }.map(\.activity)
    }

    func apply() {
        if GameDetector.enabled {
            if !server.start() { Log.write("rich presence: discord-ipc-0 to 9 are all taken") }
            if claimTimer == nil {
                // The Discord app may hold discord-ipc-0 now and quit later.
                claimTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
                    MainActor.assumeIsolated { RichPresence.shared.server.claimPrimary() }
                }
            }
        } else {
            server.stop()
            claimTimer?.invalidate()
            claimTimer = nil
            if !entries.isEmpty { entries = [:]; Playing.changed() }
        }
    }

    private func set(_ conn: Int, _ appId: String?, _ activity: JObj?) {
        guard let appId, let activity, GameDetector.enabled else {
            if entries.removeValue(forKey: conn) != nil { Playing.changed() }
            return
        }
        Task {
            let converted = await convert(appId, activity)
            entries[conn] = Entry(appId: appId, activity: converted, order: { order += 1; return order }())
            Playing.changed()
        }
    }

    // Turns what a game sends into what the gateway expects: app name filled in, timestamps in
    // milliseconds, image keys and URLs turned into Discord asset ids.
    private func convert(_ appId: String, _ a: JObj) async -> JObj {
        let name = await GameBoard.info(appId).name ?? "a game"
        return await Self.convert(appId, a, name: name) { [weak self] in await self?.resolveAsset(appId, $0) ?? $0 }
    }

    static func convert(_ appId: String, _ a: JObj, name: String, resolve: (String) async -> String) async -> JObj {
        var out: JObj = ["application_id": appId, "type": a.has("type") ? a.long("type") : 0, "name": name]
        for key in ["details", "state", "instance", "flags", "status_display_type", "details_url", "state_url"] {
            if let v = a[key], !(v is NSNull) { out[key] = v }
        }
        if let ts = a.obj("timestamps") {
            var t: JObj = [:]
            for k in ["start", "end"] where ts[k] != nil && !(ts[k] is NSNull) {
                let v = ts.long(k)
                t[k] = v < 100_000_000_000 ? v * 1000 : v   // seconds -> ms
            }
            out["timestamps"] = t
        }
        if let party = a.obj("party") { out["party"] = party }
        if let assets = a.obj("assets") {
            var o: JObj = [:]
            for k in ["large_text", "small_text"] { if let v = assets.str(k) { o[k] = v } }
            for k in ["large_image", "small_image"] { if let v = assets.str(k) { o[k] = await resolve(v) } }
            out["assets"] = o
        }
        let buttons = a.objs("buttons")
        if !buttons.isEmpty {
            out["buttons"] = buttons.map { $0.str("label") ?? "" }
            out["metadata"] = ["button_urls": buttons.map { $0.str("url") ?? "" }]
        }
        return out
    }

    private func resolveAsset(_ appId: String, _ value: String) async -> String {
        if value.hasPrefix("mp:") || value.hasPrefix("spotify:") || value.hasPrefix("twitch:") { return value }
        guard let api = api() else { return value }
        if value.hasPrefix("http://") || value.hasPrefix("https://") {
            if let mp = externalAssets[value] { return mp }
            do {
                let r = try await api.post("/applications/\(appId)/external-assets", ["urls": [value]]) as? [Any]
                if let path = (r?.first as? JObj)?.str("external_asset_path") {
                    externalAssets[value] = "mp:" + path
                    return "mp:" + path
                }
            } catch { Log.write("external asset: \(error.localizedDescription)") }
            return value
        }
        if assetIds[appId] == nil {
            var map: [String: String] = [:]
            do {
                for case let j as JObj in (try await api.get("/oauth2/applications/\(appId)/assets") as? [Any]) ?? [] {
                    if let n = j.str("name"), let id = j.str("id") { map[n.lowercased()] = id }
                }
            } catch { Log.write("assets \(appId): \(error.localizedDescription)") }
            assetIds[appId] = map
        }
        return assetIds[appId]?[value.lowercased()] ?? value
    }
}

// Everything this Mac says you're playing: rich presence from games first, then games found
// running. "Don't Share" hides a game from both.
@MainActor
enum Playing {
    struct Item: Identifiable {
        let key: String, name: String, appId: String?, activity: JObj
        var id: String { key }
    }

    static var onChange: (() -> Void)?

    static var items: [Item] {
        let hidden = Set(GameDetector.hidden)
        var out: [Item] = []
        for a in RichPresence.shared.activities {
            let name = a.str("name") ?? "a game"
            if !hidden.contains(name) { out.append(Item(key: a.str("application_id") ?? name, name: name, appId: a.str("application_id"), activity: a)) }
        }
        for (g, a) in zip(GameDetector.shared.running, GameDetector.shared.activities) where !out.contains(where: { $0.key == g.key || ($0.appId != nil && $0.appId == g.appId) }) {
            out.append(Item(key: g.key, name: g.name, appId: g.appId, activity: a))
        }
        return out
    }

    static var activities: [JObj] { items.map(\.activity) }

    static func changed() { onChange?() }

    static func apply() {
        GameDetector.shared.start()
        RichPresence.shared.apply()
    }
}
