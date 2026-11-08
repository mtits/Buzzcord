import XCTest
@testable import Buzzcord

// Port of UnfriendTest in src/SelfTest.cs.
@MainActor
final class UnfriendTests: XCTestCase {
    func testOnlyTheirUnfriendingAlerts() {
        let s = Session(token: "test.token")
        var fired: [String] = []
        s.onUnfriended = { fired.append($0) }
        for (id, type) in [("201", 1), ("202", 1), ("205", 3)] {
            s.inject("RELATIONSHIP_ADD", ["id": id, "type": type, "user": ["id": id, "username": "u\(id)"]])
        }
        s.inject("RELATIONSHIP_REMOVE", ["id": "201", "type": 1])   // they removed you
        s.noteRemovedByMe("202")                                     // you removed them
        s.inject("RELATIONSHIP_REMOVE", ["id": "202", "type": 1])
        s.inject("RELATIONSHIP_REMOVE", ["id": "205", "type": 3])   // a request going away isn't an unfriend
        XCTAssertEqual(fired, ["201"])
        XCTAssertNil(s.relationships["201"])
        XCTAssertNil(s.relationships["202"])
        XCTAssertNil(s.relationships["205"])

        // A later unfriend by them, after you'd removed them once before, still alerts.
        s.inject("RELATIONSHIP_ADD", ["id": "202", "type": 1])
        s.inject("RELATIONSHIP_REMOVE", ["id": "202", "type": 1])
        XCTAssertEqual(fired, ["201", "202"])
    }
}
