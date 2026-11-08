import XCTest
@testable import Buzzcord

@MainActor
final class AutoAwayTests: XCTestCase {
    func testWhenToBeAway() {
        XCTAssertFalse(AutoAway.shouldBeAway(idle: 30, locked: false, minutes: 10, whenLocked: true))
        XCTAssertTrue(AutoAway.shouldBeAway(idle: 600, locked: false, minutes: 10, whenLocked: true))
        XCTAssertFalse(AutoAway.shouldBeAway(idle: 6000, locked: false, minutes: 0, whenLocked: true), "Off means never by idle time")
        XCTAssertTrue(AutoAway.shouldBeAway(idle: 0, locked: true, minutes: 0, whenLocked: true), "locking the screen")
        XCTAssertFalse(AutoAway.shouldBeAway(idle: 0, locked: true, minutes: 10, whenLocked: false))
        XCTAssertGreaterThanOrEqual(AutoAway.idleSeconds, 0, "reads the Mac's idle time")
    }

    func testShownStatus() {
        let s = Session(token: "test.token")
        s.status = "online"
        s.setAutoAway(true)
        XCTAssertEqual(s.shownStatus, "idle", "Available shows as Away")
        s.status = "dnd"
        XCTAssertEqual(s.shownStatus, "dnd", "Busy stays Busy")
        s.status = "invisible"
        XCTAssertEqual(s.shownStatus, "invisible", "Invisible stays invisible")
        s.status = "online"
        s.setAutoAway(false)
        XCTAssertEqual(s.shownStatus, "online", "back again")
    }
}
