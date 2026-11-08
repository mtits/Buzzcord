import XCTest
@testable import Buzzcord

// Friend request rows have Accept / Ignore / Cancel buttons. The row's own click handling must
// stay off those rows (it went on the whole row before 1.3.2 and swallowed the buttons' clicks).
final class BuddyRowTests: XCTestCase {
    func testOnlyRequestRowsKeepTheirClicksForButtons() {
        XCTAssertTrue(BuddyRow(id: "a", kind: .pending("1", true)).hasButtons)
        XCTAssertTrue(BuddyRow(id: "b", kind: .pending("1", false)).hasButtons)
        XCTAssertFalse(BuddyRow(id: "c", kind: .friend("1")).hasButtons)
        XCTAssertFalse(BuddyRow(id: "d", kind: .note("x")).hasButtons)
    }
}
