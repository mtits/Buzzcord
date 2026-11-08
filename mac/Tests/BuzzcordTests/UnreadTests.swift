import XCTest
@testable import Buzzcord

@MainActor
final class UnreadTests: XCTestCase {
    func snowflake(daysAgo: Double) -> String {
        let ms = Int64((Date().timeIntervalSince1970 - daysAgo * 86400) * 1000)
        return String((ms - 1_420_070_400_000) << 22)
    }

    func testWhatCounts() {
        let s = Session(token: "test.token")
        let old = snowflake(daysAgo: 200), recent = snowflake(daysAgo: 1), older = snowflake(daysAgo: 2)
        s.inject("READY", [
            "user": ["id": "1", "username": "me"],
            "users": [["id": "2", "username": "a"], ["id": "3", "username": "b"]],
            "private_channels": [
                ["id": "10", "type": 1, "recipient_ids": ["2"], "last_message_id": old],        // never opened, months old
                ["id": "11", "type": 1, "recipient_ids": ["3"], "last_message_id": recent],     // never opened, yesterday
                ["id": "12", "type": 1, "recipient_ids": ["2"], "last_message_id": recent],     // read up to an older message
                ["id": "13", "type": 1, "recipient_ids": ["3"], "last_message_id": recent, "is_message_request": true],
                ["id": "14", "type": 3, "recipient_ids": ["2", "3"], "last_message_id": old],   // read, nothing new
            ],
            "guilds": [[
                "id": "20", "properties": ["name": "G", "owner_id": "9"],
                "roles": [["id": "20", "name": "@everyone", "permissions": "1071698660929", "position": 0]],
                "channels": [["id": "21", "type": 0, "name": "general", "position": 0, "last_message_id": recent]],
            ]],
            "read_state": ["entries": [
                ["id": "12", "last_message_id": older],
                ["id": "14", "last_message_id": old],
                ["id": "21", "last_message_id": older, "mention_count": 2],
                ["id": "99", "last_message_id": older, "mention_count": 1],   // a thread Buzzcord hasn't loaded
            ]],
        ] as JObj)
        XCTAssertFalse(s.isUnread(s.findChannel("10")), "months-old DM nobody opened stays quiet")
        XCTAssertTrue(s.isUnread(s.findChannel("11")), "a new DM nobody opened yet counts")
        XCTAssertTrue(s.isUnread(s.findChannel("12")))
        XCTAssertFalse(s.isUnread(s.findChannel("13")), "message requests don't count")
        XCTAssertFalse(s.isUnread(s.findChannel("14")))
        let entries = Dictionary(uniqueKeysWithValues: s.unreadEntries.map { ($0.channelId, $0.count) })
        XCTAssertEqual(entries, ["11": 1, "12": 1, "21": 2, "99": 1])
        XCTAssertEqual(s.badgeCount, 5, "the menu bar number is the Unread window's rows added up")

        s.markRead(channelId: "99", upTo: recent)
        XCTAssertNil(s.unreadEntries.first { $0.channelId == "99" }, "marking a thread read clears it")
        XCTAssertEqual(s.badgeCount, 4)
    }
}
