import Darwin
import XCTest
@testable import Buzzcord

// A pretend game talks to the rich presence socket the way Discord's SDKs do.
final class RichPresenceTests: XCTestCase {
    final class Seen: @unchecked Sendable {
        let lock = NSLock()
        var calls: [(Int, String?, JObj?)] = []
        func add(_ c: (Int, String?, JObj?)) { lock.withLock { calls.append(c) } }
        var all: [(Int, String?, JObj?)] { lock.withLock { calls } }
    }

    private func connectClient(_ path: String) throws -> Int32 {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        var a = sockaddr_un()
        a.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &a.sun_path) { buf in
            let b = Array(path.utf8); buf.copyBytes(from: b); buf[b.count] = 0
        }
        let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        XCTAssertEqual(r, 0, "connect to \(path)")
        var tv = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        return fd
    }

    private func send(_ fd: Int32, _ op: Int32, _ body: Data) {
        var frame = Data()
        withUnsafeBytes(of: op.littleEndian) { frame.append(contentsOf: $0) }
        withUnsafeBytes(of: Int32(body.count).littleEndian) { frame.append(contentsOf: $0) }
        frame.append(body)
        _ = frame.withUnsafeBytes { write(fd, $0.baseAddress!, $0.count) }
    }

    private func receive(_ fd: Int32) -> (Int32, Data)? {
        func exactly(_ n: Int) -> Data? {
            var d = Data(count: n), got = 0
            let ok = d.withUnsafeMutableBytes { b -> Bool in
                while got < n { let r = read(fd, b.baseAddress! + got, n - got); if r <= 0 { return false }; got += r }
                return true
            }
            return ok ? d : nil
        }
        guard let h = exactly(8) else { return nil }
        let op = h.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: Int32.self) }
        let len = h.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 4, as: Int32.self) }
        guard let body = exactly(Int(len)) else { return nil }
        return (op, body)
    }

    func testGameSetsActivity() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("rpc-\(UUID().uuidString.prefix(8))")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let server = RpcServer(dir: dir.path)
        let seen = Seen()
        server.onActivity = { seen.add(($0, $1, $2)) }
        server.user = { ["id": "42", "username": "marc", "discriminator": "0"] }
        server.log = { _ in }

        // Something already on discord-ipc-0 (the Discord app): Buzzcord takes the next one.
        let busy = RpcServer(dir: dir.path)
        busy.log = { _ in }
        XCTAssertTrue(busy.start())
        XCTAssertTrue(server.start())
        XCTAssertEqual(server.paths, [dir.appendingPathComponent("discord-ipc-1").path])
        busy.stop()
        server.claimPrimary()
        XCTAssertEqual(server.paths.count, 2, "takes discord-ipc-0 once it's free")
        defer { server.stop() }

        let fd = try connectClient(dir.appendingPathComponent("discord-ipc-0").path)
        defer { close(fd) }
        send(fd, 0, Json.data(["v": 1, "client_id": "356875890958925834"]))
        let ready = try XCTUnwrap(receive(fd))
        let readyMsg = try XCTUnwrap(Json.parse(ready.1) as? JObj)
        XCTAssertEqual(ready.0, 1)
        XCTAssertEqual(readyMsg.str("evt"), "READY")
        XCTAssertEqual(readyMsg.obj("data")?.obj("user")?.str("id"), "42")

        let activity: JObj = ["details": "Ranked", "state": "In Queue", "timestamps": ["start": 1_700_000_000], "party": ["size": [2, 5]]]
        send(fd, 1, Json.data(["cmd": "SET_ACTIVITY", "nonce": "n1", "args": ["pid": 123, "activity": activity]]))
        let reply = try XCTUnwrap(receive(fd))
        let replyMsg = try XCTUnwrap(Json.parse(reply.1) as? JObj)
        XCTAssertEqual(replyMsg.str("cmd"), "SET_ACTIVITY")
        XCTAssertEqual(replyMsg.str("nonce"), "n1")
        XCTAssertEqual(seen.all.last?.1, "356875890958925834")
        XCTAssertEqual(seen.all.last?.2?.str("details"), "Ranked")

        send(fd, 3, Data("{\"x\":1}".utf8))
        XCTAssertEqual(receive(fd)?.0, 4, "ping gets a pong")

        send(fd, 1, Json.data(["cmd": "AUTHORIZE", "nonce": "n2", "args": [:] as JObj]))
        XCTAssertEqual((Json.parse(try XCTUnwrap(receive(fd)).1) as? JObj)?.str("evt"), "ERROR")

        close(fd)
        let deadline = Date().addingTimeInterval(3)
        while seen.all.last?.2 != nil && Date() < deadline { usleep(20_000) }
        XCTAssertNil(seen.all.last?.2, "disconnecting clears the activity")
    }

    func testConvert() async {
        let a: JObj = ["details": "Ranked", "state": NSNull(), "timestamps": ["start": 1_700_000_000, "end": 1_700_000_000_123],
                       "assets": ["large_image": "map_bind", "large_text": "Bind", "small_image": "https://x.test/a.png"],
                       "buttons": [["label": "Join", "url": "https://x.test/join"]]]
        let out = await RichPresence.convert("7", a, name: "VALORANT") { $0 == "map_bind" ? "111" : "mp:external/abc" }
        XCTAssertEqual(out.str("name"), "VALORANT")
        XCTAssertEqual(out.str("application_id"), "7")
        XCTAssertEqual(out.long("type"), 0)
        XCTAssertNil(out["state"], "nulls are dropped")
        XCTAssertEqual(out.obj("timestamps")?.long("start"), 1_700_000_000_000, "seconds become ms")
        XCTAssertEqual(out.obj("timestamps")?.long("end"), 1_700_000_000_123, "ms stay ms")
        XCTAssertEqual(out.obj("assets")?.str("large_image"), "111")
        XCTAssertEqual(out.obj("assets")?.str("small_image"), "mp:external/abc")
        XCTAssertEqual(out.obj("assets")?.str("large_text"), "Bind")
        XCTAssertEqual(out.strs("buttons"), ["Join"])
        XCTAssertEqual(out.obj("metadata")?.strs("button_urls"), ["https://x.test/join"])
    }
}
