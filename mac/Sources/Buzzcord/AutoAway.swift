import AppKit
import CoreGraphics

// Yahoo-style Auto-Away: after a few minutes without using the Mac, or as soon as the screen
// locks, every Available account shows as Away; it goes back the moment you're back. Your
// chosen status is never changed, so Busy and Invisible stay as they are.
@MainActor
final class AutoAway {
    static let shared = AutoAway()

    var sessions: () -> [Session] = { [] }
    private var timer: Timer?
    private var locked = false

    static var minutes: Int { (UserDefaults.standard.object(forKey: "AutoAwayMinutes") as? NSNumber)?.intValue ?? 10 }
    static var whenLocked: Bool { Prefs.bool("AwayWhenLocked", true) }

    func start() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in MainActor.assumeIsolated { AutoAway.shared.check() } }
        let dc = DistributedNotificationCenter.default()
        dc.addObserver(self, selector: #selector(screenLocked(_:)), name: Notification.Name("com.apple.screenIsLocked"), object: nil)
        dc.addObserver(self, selector: #selector(screenUnlocked(_:)), name: Notification.Name("com.apple.screenIsUnlocked"), object: nil)
        let ws = NSWorkspace.shared.notificationCenter
        ws.addObserver(self, selector: #selector(screenLocked(_:)), name: NSWorkspace.screensDidSleepNotification, object: nil)
        ws.addObserver(self, selector: #selector(screenUnlocked(_:)), name: NSWorkspace.screensDidWakeNotification, object: nil)
    }

    @objc private func screenLocked(_ n: Notification) { locked = true; check() }
    @objc private func screenUnlocked(_ n: Notification) { locked = false; check() }

    // Seconds since the last key press, click or mouse move anywhere on the Mac.
    static var idleSeconds: Double {
        CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
    }

    static func shouldBeAway(idle: Double, locked: Bool, minutes: Int, whenLocked: Bool) -> Bool {
        (locked && whenLocked) || (minutes > 0 && idle >= Double(minutes * 60))
    }

    func check() {
        let away = Self.shouldBeAway(idle: Self.idleSeconds, locked: locked, minutes: Self.minutes, whenLocked: Self.whenLocked)
        for s in sessions() { s.setAutoAway(away) }
    }
}
