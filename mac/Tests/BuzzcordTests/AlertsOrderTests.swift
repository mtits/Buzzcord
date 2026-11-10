import XCTest
@testable import Buzzcord

// Mentions & notifications read like a chat log: oldest at the top, newest at the bottom.
@MainActor
final class AlertsOrderTests: XCTestCase {
    private func pos(_ html: String, _ s: String) -> Int {
        html.range(of: s).map { html.distance(from: html.startIndex, to: $0.lowerBound) } ?? -1
    }

    func testHistoryShowsNewestAtTheBottom() {
        // AlertHistory keeps (and saves) them newest first.
        let now = Date()
        let entries = [
            AlertEntry(time: now, kind: .message, title: "Newest", text: "c", channel: "10"),
            AlertEntry(time: now.addingTimeInterval(-60), kind: .mention, title: "Middle", text: "b", channel: "11", seen: true),
            AlertEntry(time: now.addingTimeInterval(-120), kind: .signIn, title: "Oldest", text: "a", user: "2", seen: true),
        ]
        let html = AlertsWindowController.historyHtml(entries)
        let o = pos(html, "Oldest"), m = pos(html, "Middle"), n = pos(html, "Newest")
        XCTAssertTrue(o >= 0 && o < m && m < n, html)
        // Unseen ones stay highlighted; seen ones don't.
        XCTAssertTrue(html.hasSuffix("</div>"))
        XCTAssertEqual(html.components(separatedBy: "class=\"m hl\"").count - 1, 1)
        XCTAssertTrue(html.range(of: "class=\"m hl\"")!.lowerBound > html.range(of: "Middle")!.lowerBound)
        XCTAssertEqual(AlertsWindowController.historyHtml([]), "")
    }

    func testRecordingKeepsTheSavedOrderNewestFirst() {
        let key = "test-order-\(UUID().uuidString)"
        let h = AlertHistory(tokenKey: key)
        defer { try? FileManager.default.removeItem(at: AppPaths.support.appendingPathComponent("alerts-\(key).json")) }
        h.record(AlertEntry(time: Date().addingTimeInterval(-10), kind: .signIn, title: "first", text: ""))
        h.record(AlertEntry(time: Date(), kind: .signIn, title: "second", text: ""))
        XCTAssertEqual(h.entries.map(\.title), ["second", "first"])
    }

    func testMentionsPageShowsNewestAtTheBottom() {
        let s = Session(token: "test.token")
        s.inject("READY", [
            "user": ["id": "1", "username": "me"],
            "users": [["id": "2", "username": "alyssa"]],
            "guilds": [["id": "20", "properties": ["name": "PH Gamers"], "channels": [["id": "21", "type": 0, "name": "general"]]]],
        ] as JObj)
        // Discord's inbox sends newest first.
        let page: [JObj] = [
            ["id": "300", "channel_id": "21", "author": ["id": "2"], "content": "third mention", "type": 0],
            ["id": "200", "channel_id": "21", "author": ["id": "2"], "content": "second mention", "type": 0],
            ["id": "100", "channel_id": "21", "author": ["id": "2"], "content": "first mention", "type": 0],
        ]
        let html = AlertsWindowController.mentionsHtml(s, page)
        let a = pos(html, "first mention"), b = pos(html, "second mention"), c = pos(html, "third mention")
        XCTAssertTrue(a >= 0 && a < b && b < c, html)
        XCTAssertTrue(html.contains("app://channel/21"))
    }
}
