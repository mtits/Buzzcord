import XCTest
@testable import Buzzcord

// The first-run notice must carry the same safety points as the Windows NoticeForm.
@MainActor
final class NoticeTests: XCTestCase {
    func testNoticeSaysWhatWindowsSays() {
        let b = Notice.body
        for phrase in ["unofficial Discord client", "Terms of Service", "Use it at your own risk",
                       "never asks for your password", "Never paste a token",
                       "app.marcturtal.com or github.com/mtits/Buzzcord", "SHA-256"] {
            XCTAssertTrue(b.contains(phrase), "notice is missing: \(phrase)")
        }
    }

    func testMarkAllWaitsLongerThanTheAckThrottle() {
        // Session.markRead drops acks closer than 1 s apart.
        XCTAssertGreaterThan(UnreadModel.markAllSpacing, 1_000_000_000)
    }
}
