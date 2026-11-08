import AppKit
import ServiceManagement

// Shown once, before the first sign-in: what Buzzcord is, the account risk, and how to stay
// safe with tokens. Same wording as the Windows NoticeForm. Open at Login is offered here,
// unticked, instead of being turned on silently.
@MainActor
enum Notice {
    static let key = "NoticeAccepted"
    static var accepted: Bool { Prefs.bool(key, false) }

    static let title = "Before you sign in"
    static let body =
        "Buzzcord is an unofficial Discord client. It isn't made, endorsed or supported by Discord Inc.\n\n" +
        "Discord's Terms of Service don't allow third-party clients on user accounts. Buzzcord behaves like " +
        "Discord's own web app and never automates anything, but Discord could still limit or ban accounts " +
        "that use it. Use it at your own risk.\n\n" +
        "Keeping your account safe:\n" +
        "• Sign in with the QR code. Buzzcord never asks for your password.\n" +
        "• Never paste a token, script or \"login code\" someone sends you. That's how accounts get stolen.\n" +
        "• Your sign-in stays on this Mac, in your keychain. Buzzcord connects only to Discord " +
        "and the services Discord's own app uses (hCaptcha, Spotify album art), plus GitHub to check for updates. No ads, no tracking.\n" +
        "• Only download Buzzcord from app.marcturtal.com or github.com/mtits/Buzzcord, and compare the SHA-256 listed with each release."

    // True if the user accepted (now or before); remembers it so the notice only shows once.
    static func accept() -> Bool {
        if accepted { return true }
        guard let openAtLogin = show() else { return false }
        Prefs.set(key, true)
        if openAtLogin { try? SMAppService.mainApp.register() }
        return true
    }

    // Shows the notice. nil for Quit, else whether Open at Login was ticked. Saves nothing
    // (the demo uses this too).
    static func show() -> Bool? {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = title
        a.informativeText = body
        a.addButton(withTitle: "I understand")
        a.addButton(withTitle: "Quit")
        let login = NSButton(checkboxWithTitle: "Open Buzzcord at login", target: nil, action: nil)
        login.state = .off
        login.sizeToFit()
        a.accessoryView = login
        guard a.runModal() == .alertFirstButtonReturn else { return nil }
        return login.state == .on
    }
}
