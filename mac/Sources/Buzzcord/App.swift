import AppKit
import Combine
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuItemValidation {
    var accounts: [AccountController] = []
    var login: LoginWindowController?
    private lazy var menuBar = MenuBarIcon(app: self)

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        if CommandLine.arguments.contains("--menubar-only") { MenuBarIcon.demoMenuBarOnly = true }
        menuBar.applyDockIcon()
        GlobalShortcut.shared.action = { [weak self] in self?.showAllBuddyLists() }
        GlobalShortcut.shared.apply()
        Notifier.shared.setUp()
        Notifier.shared.onClick = { [weak self] key, cid in
            guard let a = self?.accounts.first(where: { $0.session.tokenKey == key }) else { return }
            if let c = a.session.findChannel(cid) { a.openChat(c) } else { a.buddy.showWindow(nil) }
        }
        Notifier.shared.onReply = { [weak self] key, cid, mid, text in
            self?.accounts.first { $0.session.tokenKey == key }?.replyFromBanner(cid, mid, text)
        }
        Notifier.shared.onMarkRead = { [weak self] key, cid in
            guard let s = self?.accounts.first(where: { $0.session.tokenKey == key })?.session else { return }
            s.markRead(s.findChannel(cid))
        }
        Api.solveCaptcha = { challenge in await CaptchaWindowController.ask(challenge) }
        AutoAway.shared.sessions = { [weak self] in self?.accounts.map(\.session) ?? [] }
        AutoAway.shared.start()
        Playing.onChange = { [weak self] in self?.accounts.forEach { $0.session.pushPresence() } }
        RichPresence.shared.api = { [weak self] in self?.accounts.first { $0.session.isReady }?.session.api }
        RichPresence.shared.user = { [weak self] in
            guard let u = self?.accounts.first(where: { $0.session.isReady })?.session.me else { return nil }
            return ["id": u.id, "username": u.username ?? "", "discriminator": u.discriminator ?? "0", "global_name": u.globalName ?? JNull,
                    "avatar": u.avatar ?? JNull, "bot": false, "flags": 0, "premium_type": 0]
        }
        if CommandLine.arguments.contains("--demo") { Demo.run(self); return }
        if CommandLine.arguments.contains("--gateway-test") { Demo.gatewayTest(); return }
        if CommandLine.arguments.contains("--voice-test") { Demo.voiceTest(); return }
        if CommandLine.arguments.contains("--audio-test") { Demo.audioTest(); return }
        if CommandLine.arguments.contains("--audio-probe") { Demo.audioProbe(); return }
        if CommandLine.arguments.contains("--games-test") { Demo.gamesTest(); return }
        Task {
            await ClientInfo.loadBuildNumber()
            let tokens = Accounts.load()
            if tokens.isEmpty { showLogin() }
            for t in tokens { addAccount(t, save: false) }
            Playing.apply()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if accounts.isEmpty { showLogin() }
        for a in accounts { a.buddy.showWindow(nil) }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        VoiceManager.shared.leave()
        RichPresence.shared.server.stop()
        for a in accounts { a.session.stop() }
    }

    // MARK: - accounts

    func addAccount(_ token: String, save: Bool) {
        let t = token.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        guard !t.isEmpty else { return }
        if let existing = accounts.first(where: { $0.session.token == t }) { existing.buddy.showWindow(nil); return }
        let a = AccountController(token: t, app: self, index: accounts.count)
        accounts.append(a)
        if save { Accounts.save(accounts.map { $0.session.token }) }
        a.start()
    }

    func removeAccount(_ a: AccountController) {
        a.close()
        accounts.removeAll { $0 === a }
        Accounts.save(accounts.map { $0.session.token })
        updateBadge()
        if accounts.isEmpty { showLogin() }
    }

    func showLogin() {
        if login == nil {
            login = LoginWindowController { [weak self] token in
                self?.addAccount(token, save: true)
            }
            login?.onClose = { [weak self] in self?.login = nil }
        }
        login?.showWindow(nil)
        login?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func updateBadge() {
        let n = accounts.reduce(0) { $0 + $1.session.badgeCount }
        NSApp.dockTile.badgeLabel = n > 0 ? String(n) : nil
        menuBar.setUnread(n)
    }

    // Every account's buddy list, in front, even if minimized or on another Space.
    func showAllBuddyLists() {
        if accounts.isEmpty { showLogin(); return }
        for a in accounts { AppDelegate.bringForward(a.buddy.window) }
    }

    static func bringForward(_ w: NSWindow?) {
        guard let w else { return }
        NSApp.activate(ignoringOtherApps: true)
        if w.isMiniaturized { w.deminiaturize(nil) }
        w.makeKeyAndOrderFront(nil)
    }

    // MARK: - menu

    private func buildMenu() {
        let main = NSMenu()

        let app = NSMenu(title: "Buzzcord")
        app.addItem(withTitle: "About Buzzcord", action: #selector(about(_:)), keyEquivalent: "")
        app.addItem(withTitle: "Donate (PayPal)...", action: #selector(donate(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(withTitle: "Hide Buzzcord", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let others = app.addItem(withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        others.keyEquivalentModifierMask = [.command, .option]
        app.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(withTitle: "Quit Buzzcord", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        add(main, app)

        let messenger = NSMenu(title: "Messenger")
        messenger.addItem(withTitle: "Add a Friend...", action: #selector(AccountWindowController.addFriend(_:)), keyEquivalent: "n")
        let mentions = messenger.addItem(withTitle: "Mentions & Notifications", action: #selector(AccountWindowController.showAlerts(_:)), keyEquivalent: "m")
        mentions.keyEquivalentModifierMask = [.command, .shift]
        messenger.addItem(withTitle: "Buddy Lists", action: #selector(showBuddyLists(_:)), keyEquivalent: "0")
        let unread = messenger.addItem(withTitle: "Unread Messages", action: #selector(showUnread(_:)), keyEquivalent: "u")
        unread.keyEquivalentModifierMask = [.command, .shift]
        messenger.addItem(withTitle: "My Profile and Games...", action: #selector(AccountWindowController.showMyProfile(_:)), keyEquivalent: "")
        messenger.addItem(.separator())
        let addAcct = messenger.addItem(withTitle: "Add Account...", action: #selector(addAccountMenu(_:)), keyEquivalent: "n")
        addAcct.keyEquivalentModifierMask = [.command, .shift]
        messenger.addItem(withTitle: "Sign Out of This Account", action: #selector(AccountWindowController.signOut(_:)), keyEquivalent: "")
        messenger.addItem(.separator())
        messenger.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        add(main, messenger)

        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Find...", action: #selector(BuddyListWindowController.focusSearch(_:)), keyEquivalent: "f")
        edit.addItem(withTitle: "Emoji & Symbols", action: #selector(NSApplication.orderFrontCharacterPalette(_:)), keyEquivalent: "")
        add(main, edit)

        let status = NSMenu(title: "Status")
        status.addItem(withTitle: "Available", action: #selector(AccountWindowController.statusOnline(_:)), keyEquivalent: "1")
        status.addItem(withTitle: "Away", action: #selector(AccountWindowController.statusIdle(_:)), keyEquivalent: "2")
        status.addItem(withTitle: "Busy", action: #selector(AccountWindowController.statusDnd(_:)), keyEquivalent: "3")
        status.addItem(withTitle: "Invisible", action: #selector(AccountWindowController.statusInvisible(_:)), keyEquivalent: "4")
        status.addItem(.separator())
        status.addItem(withTitle: "Set Status Message...", action: #selector(AccountWindowController.setStatusMessage(_:)), keyEquivalent: "")
        add(main, status)

        let conv = NSMenu(title: "Conversation")
        let search = conv.addItem(withTitle: "Search Messages...", action: #selector(ChatWindowController.searchMessages(_:)), keyEquivalent: "f")
        search.keyEquivalentModifierMask = [.command, .shift]
        conv.addItem(withTitle: "Send File...", action: #selector(ChatWindowController.sendFile(_:)), keyEquivalent: "o")
        let callItem = conv.addItem(withTitle: "Call", action: #selector(ChatWindowController.call(_:)), keyEquivalent: "k")
        callItem.keyEquivalentModifierMask = [.command, .shift]
        conv.addItem(withTitle: "BUZZ!", action: #selector(ChatWindowController.buzz(_:)), keyEquivalent: "g")
        conv.addItem(withTitle: "Open Saved Files", action: #selector(ChatWindowController.openSaved(_:)), keyEquivalent: "")
        conv.addItem(withTitle: "Load Older Messages", action: #selector(ChatWindowController.loadOlderMenu(_:)), keyEquivalent: "")
        conv.addItem(.separator())
        conv.addItem(withTitle: "Pin to Favorites", action: #selector(ChatWindowController.togglePin(_:)), keyEquivalent: "")
        conv.addItem(withTitle: "Notify Me About Every Message", action: #selector(ChatWindowController.toggleFollow(_:)), keyEquivalent: "")
        conv.addItem(withTitle: "Always on Top", action: #selector(ChatWindowController.toggleOnTop(_:)), keyEquivalent: "")
        conv.addItem(withTitle: "Show Members", action: #selector(ChatWindowController.toggleMembers(_:)), keyEquivalent: "")
        add(main, conv)

        let options = NSMenu(title: "Options")
        options.addItem(withTitle: "Play Sounds", action: #selector(toggleSounds(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Show Notification Banners", action: #selector(toggleBanners(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Pop Up New IM Windows", action: #selector(togglePopups(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Friend Sign-in Alerts", action: #selector(toggleSignIns(_:)), keyEquivalent: "")
        options.addItem(.separator())
        let away = NSMenu(title: "Auto-Away")
        for m in [0, 5, 10, 15, 30] {
            away.addItem(withTitle: m == 0 ? "Off" : "After \(m) Minutes Idle", action: #selector(setAutoAwayMinutes(_:)), keyEquivalent: "").tag = m
        }
        away.addItem(.separator())
        away.addItem(withTitle: "When the Screen Locks", action: #selector(toggleAwayWhenLocked(_:)), keyEquivalent: "")
        let awayItem = options.addItem(withTitle: "Auto-Away", action: nil, keyEquivalent: "")
        awayItem.submenu = away
        options.addItem(.separator())
        options.addItem(withTitle: "Voice Settings...", action: #selector(voiceSettings(_:)), keyEquivalent: ",")
        options.addItem(withTitle: "Leave Call", action: #selector(leaveCall(_:)), keyEquivalent: "")
        options.addItem(.separator())
        options.addItem(withTitle: "Save Files from DMs", action: #selector(toggleSaveDms(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Save Files from Group Chats", action: #selector(toggleSaveGroups(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Keep a Chat Log", action: #selector(toggleArchive(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Change Save Folder...", action: #selector(changeSaveFolder(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Open Saved Files Folder", action: #selector(openSaveFolder(_:)), keyEquivalent: "")
        options.addItem(.separator())
        options.addItem(withTitle: "Show What I'm Playing", action: #selector(toggleDetectGames(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Share Game Activity on This Account", action: #selector(AccountWindowController.toggleShareGames(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Share Hidden Games Again", action: #selector(unhideGames(_:)), keyEquivalent: "")
        options.addItem(.separator())
        options.addItem(withTitle: "Open at Login", action: #selector(toggleLogin(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Show Icon in Menu Bar", action: #selector(toggleMenuBarIcon(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Menu Bar Only (Hide Dock Icon)", action: #selector(toggleMenuBarOnly(_:)), keyEquivalent: "")
        options.addItem(withTitle: "Buddy List Shortcut...", action: #selector(changeShortcut(_:)), keyEquivalent: "")
        add(main, options)

        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        window.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        window.addItem(.separator())
        window.addItem(withTitle: "Bring All to Front", action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")
        add(main, window)
        NSApp.windowsMenu = window

        let help = NSMenu(title: "Help")
        help.addItem(withTitle: "Rich Presence Status...", action: #selector(richPresenceStatus(_:)), keyEquivalent: "")
        help.addItem(withTitle: "Open Log Folder", action: #selector(openLogFolder(_:)), keyEquivalent: "")
        help.addItem(withTitle: "Show Crash Reports", action: #selector(showCrashReports(_:)), keyEquivalent: "")
        help.addItem(.separator())
        help.addItem(withTitle: "Donate (PayPal)...", action: #selector(donate(_:)), keyEquivalent: "")
        help.addItem(withTitle: "About Buzzcord", action: #selector(about(_:)), keyEquivalent: "")
        add(main, help)
        NSApp.helpMenu = help

        NSApp.mainMenu = main
    }

    private func add(_ main: NSMenu, _ sub: NSMenu) {
        let item = NSMenuItem(title: sub.title, action: nil, keyEquivalent: "")
        item.submenu = sub
        main.addItem(item)
    }

    static let donateUrl = "https://paypal.me/abstinencelolph"

    @objc func donate(_ sender: Any?) { openExternal(AppDelegate.donateUrl) }

    @objc func about(_ sender: Any?) {
        let small = NSFont.systemFont(ofSize: 11)
        let center = NSMutableParagraphStyle()
        center.alignment = .center
        let credits = NSMutableAttributedString(
            string: "A lightweight, old-school messenger for Discord.\nUnofficial: Discord doesn't allow third-party clients for user accounts, so using it carries some risk to the accounts you sign in with.\n\nIf Buzzcord makes your day a little better, you can buy me a coffee:\n",
            attributes: [.font: small, .foregroundColor: NSColor.labelColor, .paragraphStyle: center])
        credits.append(NSAttributedString(string: "Donate \u{2665} (PayPal)",
            attributes: [.font: NSFont.boldSystemFont(ofSize: 11), .link: URL(string: AppDelegate.donateUrl)!, .paragraphStyle: center]))
        NSApp.orderFrontStandardAboutPanel(options: [.applicationName: "Buzzcord", .credits: credits])
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func showBuddyLists(_ sender: Any?) { showAllBuddyLists() }

    @objc func showUnread(_ sender: Any?) { UnreadWindowController.show(self) }

    @objc func setAutoAwayMinutes(_ sender: NSMenuItem) {
        Prefs.set("AutoAwayMinutes", sender.tag)
        AutoAway.shared.check()
    }

    @objc func toggleAwayWhenLocked(_ sender: Any?) {
        Prefs.set("AwayWhenLocked", !AutoAway.whenLocked)
        AutoAway.shared.check()
    }

    @objc func toggleMenuBarIcon(_ sender: Any?) {
        Prefs.set("MenuBarIcon", !MenuBarIcon.enabled)
        menuBar.apply()
    }

    @objc func changeShortcut(_ sender: Any?) { ShortcutWindowController.show() }

    @objc func toggleMenuBarOnly(_ sender: Any?) {
        let on = !MenuBarIcon.menuBarOnly
        Prefs.set("MenuBarOnly", on)
        if on { Prefs.set("MenuBarIcon", true) }
        menuBar.applyDockIcon()
        GlobalShortcut.shared.action = { [weak self] in self?.showAllBuddyLists() }
        GlobalShortcut.shared.apply()
    }

    @objc func addAccountMenu(_ sender: Any?) { showLogin() }

    @objc func openLogFolder(_ sender: Any?) { NSWorkspace.shared.activateFileViewerSelecting([Log.url]) }

    // macOS writes one report per crash; the newest is selected in Finder.
    @objc func showCrashReports(_ sender: Any?) {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/DiagnosticReports")
        let reports = ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [])
            .filter { $0.lastPathComponent.hasPrefix("Buzzcord") && $0.pathExtension == "ips" }
            .sorted { (try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate ?? .distantPast) ?? .distantPast
                    > (try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate ?? .distantPast) ?? .distantPast }
        if let newest = reports.first { NSWorkspace.shared.activateFileViewerSelecting([newest]) }
        else { showError("Buzzcord hasn't crashed on this Mac. Good news!", title: "Crash reports") }
    }

    @objc func richPresenceStatus(_ sender: Any?) {
        var lines: [String] = []
        if !GameDetector.enabled {
            lines.append("Off. Turn on Options > Show What I'm Playing to share games.")
        } else {
            let paths = RichPresence.shared.server.paths
            lines.append(paths.isEmpty ? "Not listening: discord-ipc-0 to 9 are all taken." : "Listening on " + paths.map { ($0 as NSString).lastPathComponent }.joined(separator: " and ") + ".")
            if !paths.isEmpty && !paths.contains(where: { $0.hasSuffix("discord-ipc-0") }) {
                lines.append("The Discord app holds discord-ipc-0, so most games report to it. Quit Discord and Buzzcord takes over within a minute.")
            }
            let items = Playing.items
            lines.append(items.isEmpty ? "Nothing playing right now." : "Sharing: " + items.map { $0.name }.joined(separator: ", ") + ".")
            let off = accounts.filter { !$0.session.sharesGames }.compactMap { $0.session.me?.displayName }
            if !off.isEmpty { lines.append("Not shared on: " + off.joined(separator: ", ") + ".") }
            if !GameDetector.hidden.isEmpty { lines.append("Never shared: " + GameDetector.hidden.joined(separator: ", ") + ".") }
        }
        showError(lines.joined(separator: "\n\n"), title: "Rich presence")
    }

    @objc func toggleSounds(_ sender: Any?) { Prefs.set("Sounds", !Prefs.bool("Sounds", true)) }
    @objc func toggleBanners(_ sender: Any?) { Prefs.set("Banners", !Prefs.bool("Banners", true)) }
    @objc func togglePopups(_ sender: Any?) { Prefs.set("PopupIMs", !Prefs.bool("PopupIMs", true)) }
    @objc func toggleSignIns(_ sender: Any?) { Prefs.set("SignInAlerts", !Prefs.bool("SignInAlerts", true)) }

    @objc func voiceSettings(_ sender: Any?) { VoiceSettingsWindowController.show() }
    @objc func leaveCall(_ sender: Any?) { VoiceManager.shared.leave() }

    @objc func toggleSaveDms(_ sender: Any?) { Prefs.set("AutoSaveDMs", !AutoSaver.saveDms) }
    @objc func toggleSaveGroups(_ sender: Any?) { Prefs.set("AutoSaveGroups", !AutoSaver.saveGroups) }
    @objc func toggleArchive(_ sender: Any?) { Prefs.set("ArchiveChats", !Archive.enabled) }
    @objc func openSaveFolder(_ sender: Any?) { AutoSaver.reveal(AutoSaver.root) }

    @objc func changeSaveFolder(_ sender: Any?) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = AutoSaver.root
        panel.message = "Where should Buzzcord save files people send you, and the chat logs?"
        panel.prompt = "Use This Folder"
        if panel.runModal() == .OK, let u = panel.url { Prefs.set("SaveFolder", u.path) }
    }

    @objc func toggleDetectGames(_ sender: Any?) {
        Prefs.set("DetectGames", !GameDetector.enabled)
        Playing.apply()
    }

    @objc func unhideGames(_ sender: Any?) { GameDetector.unhideAll() }

    @objc func toggleLogin(_ sender: Any?) {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            showError("macOS didn't allow that: \(error.localizedDescription)\n\nYou can also add Buzzcord in System Settings > General > Login Items.")
        }
    }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(toggleSounds(_:)): item.state = Prefs.bool("Sounds", true) ? .on : .off
        case #selector(toggleBanners(_:)): item.state = Prefs.bool("Banners", true) ? .on : .off
        case #selector(togglePopups(_:)): item.state = Prefs.bool("PopupIMs", true) ? .on : .off
        case #selector(toggleSignIns(_:)): item.state = Prefs.bool("SignInAlerts", true) ? .on : .off
        case #selector(leaveCall(_:)): return VoiceManager.shared.current != nil
        case #selector(toggleSaveDms(_:)): item.state = AutoSaver.saveDms ? .on : .off
        case #selector(toggleSaveGroups(_:)): item.state = AutoSaver.saveGroups ? .on : .off
        case #selector(toggleArchive(_:)): item.state = Archive.enabled ? .on : .off
        case #selector(toggleDetectGames(_:)): item.state = GameDetector.enabled ? .on : .off
        case #selector(unhideGames(_:)): return !GameDetector.hidden.isEmpty
        case #selector(setAutoAwayMinutes(_:)): item.state = item.tag == AutoAway.minutes ? .on : .off
        case #selector(toggleAwayWhenLocked(_:)): item.state = AutoAway.whenLocked ? .on : .off
        case #selector(toggleMenuBarIcon(_:)):
            item.state = MenuBarIcon.enabled ? .on : .off
            return !MenuBarIcon.menuBarOnly   // the only way back to the windows
        case #selector(changeShortcut(_:)): item.title = "Buddy List Shortcut (\(GlobalShortcut.combo?.display ?? "off"))..."
        case #selector(toggleMenuBarOnly(_:)): item.state = MenuBarIcon.menuBarOnly ? .on : .off
        case #selector(toggleLogin(_:)): item.state = SMAppService.mainApp.status == .enabled ? .on : .off
        default: break
        }
        return true
    }
}

// One signed-in account: its session, its buddy list window and its open chats.
@MainActor
final class AccountController {
    let session: Session
    let alerts: AlertHistory
    weak var app: AppDelegate?
    private(set) var buddy: BuddyListWindowController!
    private var chats: [String: ChatWindowController] = [:]
    private var watchers: Set<AnyCancellable> = []

    init(token: String, app: AppDelegate, index: Int) {
        session = Session(token: token)
        alerts = AlertHistory(tokenKey: session.tokenKey)
        self.app = app
        buddy = BuddyListWindowController(account: self, index: index)
        buddy.showWindow(nil)

        session.onMessageCreate = { [weak self] m in self?.onMessage(m) }
        session.onMessageUpdate = { [weak self] m in self?.chats[m.str("channel_id") ?? ""]?.updated(m) }
        session.onMessageDelete = { [weak self] cid, mid in self?.chats[cid]?.deleted(mid) }
        session.onTyping = { [weak self] cid, uid in self?.chats[cid]?.someoneTyping(uid) }
        session.onReaction = { [weak self] t, d in self?.chats[d.str("channel_id") ?? ""]?.reactionChanged(t, d) }
        session.onPresence = { [weak self] uid in
            guard let self else { return }
            for c in self.chats.values where c.channel.recipientIds.contains(uid) { c.objectWillChange.send() }
        }
        session.onSignedOut = { [weak self] in
            guard let self else { return }
            showError("Discord signed \(self.session.me?.displayName ?? "this account") out (the token stopped working). Sign in again with Messenger > Add Account.")
            self.app?.removeAccount(self)
        }
        session.onFriendRequest = { [weak self] id in
            guard let self else { return }
            self.alert(.friendRequest, nil, id, "Friend request", "\(self.session.name(id)) wants to be your friend.", sound: "Glass")
        }
        session.onFriendAccepted = { [weak self] id in
            guard let self else { return }
            self.alert(.friendAccepted, nil, id, "Friend request accepted", "\(self.session.name(id)) accepted your friend request.", sound: "Glass")
        }
        session.onFriendSignedIn = { [weak self] id in
            guard let self, Prefs.bool("SignInAlerts", true), self.session.status != "dnd" else { return }
            self.alert(.signIn, nil, id, self.session.name(id), "signed in.", sound: "Purr")
        }
        session.onUnfriended = { [weak self] id in
            guard let self else { return }
            self.alert(.unfriended, nil, id, "Removed from friends", "\(self.session.name(id)) is no longer on your friends list.", sound: nil)
        }
        session.onIncomingCall = { [weak self] cid in
            guard let self, let c = self.session.findChannel(cid), !VoiceManager.shared.isIn(self.session, cid) else { return }
            self.alerts.record(AlertEntry(time: Date(), kind: .call, title: "\(self.session.title(c)) called", text: "", channel: cid))
            IncomingCallWindowController.show(self, c)
        }
        session.onCallChanged = { [weak self] cid in
            guard let self, !self.session.isRingingMe(cid) else { return }
            IncomingCallWindowController.dismiss(self, cid)
        }
        session.$revision.sink { [weak self] _ in
            DispatchQueue.main.async { self?.app?.updateBadge() }
        }.store(in: &watchers)
    }

    func start() { session.start() }

    func close() {
        session.stop()
        for c in chats.values { c.close() }
        chats.removeAll()
        buddy.closeForGood()
    }

    private func alert(_ kind: AlertKind, _ cid: String?, _ uid: String?, _ title: String, _ body: String, sound: String?, message: String? = nil) {
        alerts.record(AlertEntry(time: Date(), kind: kind, title: title, text: body, channel: cid, user: uid))
        let prefix = (app?.accounts.count ?? 1) > 1 ? "[\(session.me?.displayName ?? "")] " : ""
        Notifier.shared.alert(account: session.tokenKey, channel: cid, title: prefix + title, body: body,
                              sound: session.status == "dnd" ? nil : sound, message: message)
    }

    // Typed into a message banner. In a server channel it's sent as a reply to that message.
    func replyFromBanner(_ cid: String, _ messageId: String, _ text: String) {
        guard let c = session.findChannel(cid) else { return }
        let replyTo = c.isPrivate || messageId.isEmpty ? nil : messageId
        Task {
            do {
                try await session.api.sendMessage(cid, text, replyTo: replyTo, attachments: [])
                session.markRead(session.findChannel(cid))
            } catch {
                Log.write("banner reply: \(error.localizedDescription)")
                Notifier.shared.alert(account: session.tokenKey, channel: nil, title: "Your reply to \(session.title(c)) wasn't sent",
                                      body: "\(error.localizedDescription)\n\n\(text)", sound: "Basso")
            }
        }
    }

    @discardableResult
    func openChat(_ c: Channel, activate: Bool = true) -> ChatWindowController {
        if let existing = chats[c.id] {
            if activate { existing.showWindow(nil); existing.window?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
            return existing
        }
        let chat = ChatWindowController(account: self, channel: c)
        chats[c.id] = chat
        if activate {
            chat.showWindow(nil)
            NSApp.activate(ignoringOtherApps: true)
        } else {
            chat.window?.orderFrontRegardless()
        }
        return chat
    }

    func openChat(userId: String) {
        Task {
            do { openChat(try await session.openDm(userId)) }
            catch { showError(error.localizedDescription) }
        }
    }

    func chatClosed(_ cid: String) { chats.removeValue(forKey: cid) }

    func showProfile(_ userId: String, guildId: String?) {
        ProfileWindowController.open(account: self, userId: userId, guildId: guildId)
    }

    private func onMessage(_ m: JObj) {
        guard let cid = m.str("channel_id") else { return }
        let author = m.obj("author")
        let mine = author?.str("id") != nil && author?.str("id") == session.me?.id
        let chat = chats[cid]
        if let chat { chat.add(m) } else {
            AutoSaver.consider(session, m)
            Archive.consider(session, m)
        }
        guard !mine, let c = session.findChannel(cid) else { return }

        let from = session.name(author?.str("id"))
        let title: String
        let kind: AlertKind
        if c.isPrivate {
            if session.isMuted(c) { return }
            kind = .message
            title = c.isGroup ? from + " in " + session.title(c) : from
        } else if session.shouldNotifyMention(m) {
            kind = .mention
            title = from + " in " + session.title(c)
        } else if Favorites.isFollowed(session, cid) {
            kind = .followed
            title = from + " in " + session.title(c)
        } else { return }
        var text = m.str("content") ?? ""
        if text.isEmpty && !m.objs("attachments").isEmpty { text = "(sent a file)" }
        if text == ChatWindowController.buzzText { text = "BUZZ!!!" }

        if let chat {
            // The conversation is already open: bounce, ding if it's not in front.
            if chat.window?.isKeyWindow == true && NSApp.isActive { return }
            NSApp.requestUserAttention(.informationalRequest)
            alert(kind, cid, author?.str("id"), title, text, sound: "Pop", message: m.str("id"))
            return
        }
        if c.isPrivate && Prefs.bool("PopupIMs", true) && session.status != "dnd" {
            openChat(c, activate: false)   // the classic pop-up IM window, without stealing focus
        }
        alert(kind, cid, author?.str("id"), title, text, sound: "Pop", message: m.str("id"))
    }
}

// Pinned and followed channels, per account.
@MainActor
enum Favorites {
    static func pinned(_ s: Session) -> [String] { Prefs.strings("Favorites." + s.tokenKey) }
    static func isPinned(_ s: Session, _ cid: String) -> Bool { pinned(s).contains(cid) }
    static func setPinned(_ s: Session, _ cid: String, _ on: Bool) {
        var list = pinned(s).filter { $0 != cid }
        if on { list.append(cid) }
        Prefs.set("Favorites." + s.tokenKey, list)
        s.changed()
    }

    static func isFollowed(_ s: Session, _ cid: String) -> Bool { Prefs.strings("Followed." + s.tokenKey).contains(cid) }
    static func setFollowed(_ s: Session, _ cid: String, _ on: Bool) {
        var list = Prefs.strings("Followed." + s.tokenKey).filter { $0 != cid }
        if on { list.append(cid) }
        Prefs.set("Followed." + s.tokenKey, list)
        s.changed()
    }
}

// Status menu, Add a Friend and Sign Out work from any window of the account.
@MainActor
class AccountWindowController: NSWindowController, NSMenuItemValidation {
    weak var account: AccountController?

    var accountSession: Session? { account?.session }

    @objc func statusOnline(_ sender: Any?) { accountSession?.setStatus("online") }
    @objc func statusIdle(_ sender: Any?) { accountSession?.setStatus("idle") }
    @objc func statusDnd(_ sender: Any?) { accountSession?.setStatus("dnd") }
    @objc func statusInvisible(_ sender: Any?) { accountSession?.setStatus("invisible") }

    @objc func setStatusMessage(_ sender: Any?) {
        guard let s = accountSession else { return }
        if let text = askText(title: "Status message", message: "What should your friends see under your name? Leave it empty to clear it.",
                              initial: s.customStatusText ?? "", ok: "Set", window: window) {
            s.setCustomStatus(text.trimmingCharacters(in: .whitespaces))
        }
    }

    @objc func showMyProfile(_ sender: Any?) {
        if let a = account, let me = a.session.me { a.showProfile(me.id, guildId: nil) }
    }

    @objc func toggleShareGames(_ sender: Any?) { accountSession?.sharesGames.toggle() }

    @objc func addFriend(_ sender: Any?) { addFriendNamed(nil) }

    @objc func showAlerts(_ sender: Any?) { if let a = account { AlertsWindowController.show(a) } }

    func addFriendNamed(_ initial: String?) {
        guard let s = accountSession else { return }
        guard let name = askText(title: "Add a Friend", message: "Their Discord username:", initial: initial ?? "", ok: "Send Request", window: window),
              !name.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        Task {
            do {
                try await s.sendFriendRequest(name)
                showError("Friend request sent to \(name).", title: "Add a Friend")
            } catch { showError(error.localizedDescription, title: "Couldn't add \(name)") }
        }
    }

    @objc func signOut(_ sender: Any?) {
        guard let a = account, confirm("Sign out of \(a.session.me?.displayName ?? "this account")? Buzzcord forgets its sign-in.", ok: "Sign Out") else { return }
        a.app?.removeAccount(a)
    }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        let st = accountSession?.status
        switch item.action {
        case #selector(statusOnline(_:)): item.state = st == "online" ? .on : .off
        case #selector(statusIdle(_:)): item.state = st == "idle" ? .on : .off
        case #selector(statusDnd(_:)): item.state = st == "dnd" ? .on : .off
        case #selector(statusInvisible(_:)): item.state = st == "invisible" ? .on : .off
        case #selector(toggleShareGames(_:)): item.state = accountSession?.sharesGames ?? false ? .on : .off
        default: break
        }
        return validateExtra(item)
    }

    func validateExtra(_ item: NSMenuItem) -> Bool { true }
}
