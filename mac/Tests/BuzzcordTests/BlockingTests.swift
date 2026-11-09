import XCTest
@testable import Buzzcord

@MainActor
final class BlockingTests: XCTestCase {
    private func session() -> Session {
        let s = Session(token: "test.token")
        s.inject("READY", [
            "user": ["id": "1", "username": "me"],
            "users": [["id": "2", "username": "alyssa"], ["id": "3", "username": "ben"], ["id": "4", "username": "zed"]],
            "relationships": [["id": "2", "type": 1], ["id": "3", "type": 1], ["id": "4", "type": 2]],
            "private_channels": [
                ["id": "10", "type": 1, "recipient_ids": ["2"]],
                ["id": "11", "type": 3, "recipient_ids": ["2", "4"]],
            ],
        ] as JObj)
        return s
    }

    func testBlockListComesFromRelationships() {
        let s = session()
        XCTAssertTrue(s.isBlocked("4"))
        XCTAssertFalse(s.isBlocked("2"))
        XCTAssertFalse(s.isBlocked(nil))
        XCTAssertEqual(Blocking.list(s), ["4"])
    }

    // Blocking a friend yourself isn't reported as them unfriending you, whichever way Discord
    // sends it (remove + add, or one update).
    func testBlockingAFriendYourselfDoesNotAlert() {
        let s = session()
        var fired: [String] = []
        s.onUnfriended = { fired.append($0) }
        s.noteRemovedByMe("2")
        s.inject("RELATIONSHIP_REMOVE", ["id": "2", "type": 1])
        s.inject("RELATIONSHIP_ADD", ["id": "2", "type": 2, "user": ["id": "2", "username": "alyssa"]])
        s.noteRemovedByMe("3")
        s.inject("RELATIONSHIP_UPDATE", ["id": "3", "type": 2])
        XCTAssertEqual(fired, [])
        XCTAssertEqual(Blocking.list(s), ["2", "3", "4"].sorted { s.name($0) < s.name($1) })
        s.inject("RELATIONSHIP_REMOVE", ["id": "4", "type": 2])   // unblocked
        XCTAssertFalse(s.isBlocked("4"))
        XCTAssertEqual(fired, [])
    }

    func testBlockedMessagesAreFoldedAndDontCount() {
        let s = session()
        let theirs: JObj = ["id": "500", "channel_id": "11", "content": "hello there", "author": ["id": "4", "username": "zed"]]
        let html = MessageHtml.render(s, theirs)
        XCTAssertTrue(html.contains("<details class=\"blk\">"))
        XCTAssertTrue(html.contains("Blocked message"))
        XCTAssertEqual(html.components(separatedBy: "id=\"m500\"").count, 2, "one element with the message's id, so edits and deletes find it")

        let friend: JObj = ["id": "501", "channel_id": "11", "content": "hi", "author": ["id": "2", "username": "alyssa"],
                            "referenced_message": ["id": "500", "content": "hello there", "author": ["id": "4"]]]
        let reply = MessageHtml.render(s, friend)
        XCTAssertFalse(reply.contains("<details"))
        XCTAssertTrue(reply.contains("(blocked message)"))
        XCTAssertFalse(reply.contains("hello there"), "a reply doesn't quote the blocked message")

        s.inject("MESSAGE_CREATE", theirs)
        XCTAssertEqual(s.mentionCount("11"), 0, "a blocked user's message in a group chat doesn't count as new")
        s.inject("MESSAGE_CREATE", friend)
        XCTAssertEqual(s.mentionCount("11"), 1)
    }

    // Remove from Recent closes a DM; for a group that would mean leaving it, so it never runs.
    func testRemoveFromRecentNeverTouchesGroups() async throws {
        let s = session()
        let group = try XCTUnwrap(s.findChannel("11"))
        try await s.closeDm(group)
        XCTAssertNotNil(s.privateChannels["11"])
    }
}
