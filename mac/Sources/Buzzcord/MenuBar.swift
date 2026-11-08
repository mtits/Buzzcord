import AppKit
import SwiftUI

// The menu bar icon, like the Windows tray icon: double-click brings back every account's
// buddy list; a single click (or right-click) opens a menu of accounts. Shows the unread count.
@MainActor
final class MenuBarIcon: NSObject {
    private weak var app: AppDelegate?
    private var item: NSStatusItem?
    private var pendingMenu: Timer?

    static var enabled: Bool { Prefs.bool("MenuBarIcon", true) || menuBarOnly }
    // No Dock icon: Buzzcord lives in the menu bar only (then the icon can't be turned off).
    static var menuBarOnly: Bool { demoMenuBarOnly ?? Prefs.bool("MenuBarOnly", false) }
    static var demoMenuBarOnly: Bool?   // --demo --menubar-only

    // Dock icon or not. Windows that were open stay in front.
    func applyDockIcon() {
        let policy: NSApplication.ActivationPolicy = Self.menuBarOnly ? .accessory : .regular
        apply()
        guard NSApp.activationPolicy() != policy else { return }
        let front = NSApp.windows.filter { $0.isVisible }
        NSApp.setActivationPolicy(policy)
        // Changing the policy sends the app to the background; bring it straight back.
        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            for w in front { w.orderFront(nil) }
        }
    }

    init(app: AppDelegate) {
        self.app = app
        super.init()
    }

    func apply() {
        if Self.enabled { install() } else if let item { NSStatusBar.system.removeStatusItem(item); self.item = nil }
    }

    private func install() {
        guard item == nil else { return }
        let it = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let img = (NSApp.applicationIconImage.copy() as? NSImage) ?? NSImage()
        img.size = NSSize(width: 18, height: 18)
        it.button?.image = img
        it.button?.imagePosition = .imageLeading
        it.button?.target = self
        it.button?.action = #selector(clicked(_:))
        it.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        it.button?.toolTip = "Buzzcord: double-click for your buddy lists"
        item = it
        setUnread(unread)
    }

    private var unread = 0

    func setUnread(_ n: Int) {
        unread = n
        item?.button?.title = n > 0 ? " \(n)" : ""
        item?.button?.toolTip = n > 0 ? "Buzzcord: \(n) unread" : "Buzzcord: double-click for your buddy lists"
    }

    @objc private func clicked(_ sender: NSStatusBarButton) {
        let e = NSApp.currentEvent
        if e?.type == .rightMouseUp || e?.modifierFlags.contains(.control) == true { showMenu(); return }
        if (e?.clickCount ?? 1) >= 2 {
            pendingMenu?.invalidate()
            pendingMenu = nil
            app?.showAllBuddyLists()
            return
        }
        // Wait to see whether a second click is coming.
        pendingMenu = Timer.scheduledTimer(withTimeInterval: NSEvent.doubleClickInterval, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.pendingMenu = nil; self?.showMenu() }
        }
    }

    private func showMenu() {
        guard let app, let button = item?.button else { return }
        let menu = NSMenu()
        for a in app.accounts {
            let s = a.session
            let label = s.shownStatus != s.status ? "Away (idle)" : Theme.statusLabel(s.status)
            let title = (s.me?.displayName ?? "Signing in...") + "  " + label + (s.badgeCount > 0 ? "  (\(s.badgeCount) unread)" : "")
            let mi = NSMenuItem(title: title, action: #selector(showAccount(_:)), keyEquivalent: "")
            mi.target = self
            mi.representedObject = a
            mi.image = Self.statusImage(s.status == "invisible" ? "offline" : s.shownStatus)
            menu.addItem(mi)
        }
        if !app.accounts.isEmpty { menu.addItem(.separator()) }
        let total = app.accounts.reduce(0) { $0 + $1.session.badgeCount }
        add(menu, total > 0 ? "Unread Messages (\(total))..." : "Unread Messages...", #selector(showUnread(_:))).isHidden = app.accounts.isEmpty
        add(menu, "Show All Buddy Lists", #selector(showAll(_:)))
        add(menu, "Mentions & Notifications", #selector(showAlerts(_:))).isHidden = app.accounts.isEmpty
        add(menu, "Add Account...", #selector(addAccount(_:)))
        menu.addItem(.separator())
        // With no Dock icon the app's own menu bar never shows, so its menus live here.
        if Self.menuBarOnly, let main = NSApp.mainMenu {
            for title in ["Options", "Help"] {
                guard let sub = main.items.first(where: { $0.title == title })?.submenu?.copy() as? NSMenu else { continue }
                let mi = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                mi.submenu = sub
                menu.addItem(mi)
            }
            add(menu, "About Buzzcord", #selector(about(_:)))
            menu.addItem(.separator())
        }
        add(menu, "Quit Buzzcord", #selector(quit(_:)))
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height + 4), in: button)
    }

    @discardableResult private func add(_ menu: NSMenu, _ title: String, _ action: Selector) -> NSMenuItem {
        let mi = menu.addItem(withTitle: title, action: action, keyEquivalent: "")
        mi.target = self
        return mi
    }

    private static func statusImage(_ status: String) -> NSImage? {
        let r = ImageRenderer(content: StatusIcon(status: status, size: 14))
        r.scale = NSScreen.main?.backingScaleFactor ?? 2
        return r.nsImage
    }

    @objc private func showAccount(_ sender: NSMenuItem) {
        guard let a = sender.representedObject as? AccountController else { return }
        AppDelegate.bringForward(a.buddy.window)
    }

    @objc private func showAll(_ sender: Any?) { app?.showAllBuddyLists() }

    @objc private func showUnread(_ sender: Any?) { if let app { UnreadWindowController.show(app) } }

    @objc private func showAlerts(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        for a in app?.accounts ?? [] { AlertsWindowController.show(a) }
    }

    @objc private func addAccount(_ sender: Any?) { app?.showLogin() }

    @objc private func about(_ sender: Any?) { app?.about(nil) }

    @objc private func quit(_ sender: Any?) { NSApp.terminate(nil) }
}
