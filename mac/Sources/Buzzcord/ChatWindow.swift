import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WebKit

// An IM window: history in a web view (MessageHtml), a toolbar, the input box and, for DMs,
// the other person's picture and what they're doing.
@MainActor
final class ChatWindowController: AccountWindowController, NSWindowDelegate, WKNavigationDelegate, ObservableObject {
    static let buzzText = "BUZZ!!!"
    static let smileys = ["😀", "😂", "🤣", "😊", "😍", "😘", "😜", "😎", "🤔", "😐", "🙄", "😭", "😡", "😱", "😴", "🥳",
                          "👍", "👎", "👏", "🙏", "🔥", "❤️", "💔", "💀", "👀", "🎉", "💯", "✅", "❌", "🎮", "🍺", "🍕"]

    let channel: Channel
    let web: LogWebView
    let input = InputTextView()

    @Published var statusText = ""
    @Published var banner: String?
    @Published var sending = false
    @Published var onTop = false
    @Published var showMembers = Prefs.bool("ShowMembers", true)

    private var messages: [String: JObj] = [:]
    private var oldestId: String?
    private var newestId: String?
    private var lastDay: Date?
    private var pageReady = false
    private var pendingScripts: [String] = []
    private var queued: [JObj] = []
    private var noMoreOlder = false
    private var loadingOlder = false
    private var lastCatchUp = Date.distantPast
    private var typing: [String: Date] = [:]
    private var typingTimer: Timer?
    private var lastTypingSent = Date.distantPast
    private var replyTo: String?
    private var editing: String?
    private(set) var pendingFiles: [URL] = []

    nonisolated(unsafe) private static var lastOrigin: NSPoint?

    init(account: AccountController, channel: Channel) {
        self.channel = channel
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        config.preferences.isElementFullscreenEnabled = true
        web = LogWebView(frame: .zero, configuration: config)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 500),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.minSize = NSSize(width: 420, height: 320)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        self.account = account
        w.delegate = self
        w.title = account.session.title(channel) + " - Instant Message"
        web.navigationDelegate = self
        web.setValue(false, forKey: "drawsBackground")
        web.onDropFiles = { [weak self] urls in self?.addFiles(urls) }
        input.onEnter = { [weak self] in self?.send() }
        input.onUp = { [weak self] in self?.editLast() ?? false }
        input.onEscape = { [weak self] in self?.clearMode() }
        input.onFiles = { [weak self] urls in self?.addFiles(urls) }
        input.onChange = { [weak self] in self?.sendTyping() }
        input.isEditable = account.session.canSend(channel)

        w.contentView = NSHostingView(rootView: ChatView(chat: self, session: account.session))
        if let o = ChatWindowController.lastOrigin, NSScreen.screens.contains(where: { $0.visibleFrame.contains(o) }) {
            w.setFrameTopLeftPoint(w.cascadeTopLeft(from: o))
        } else {
            w.center()
        }
        ChatWindowController.lastOrigin = NSPoint(x: w.frame.minX, y: w.frame.maxY)
        web.loadHTMLString(MessageHtml.page, baseURL: nil)
        loadHistory()
        typingTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tickTyping() }
        }
        if channel.guildId == nil { statusText = "" }
        DispatchQueue.main.async { [weak self] in self?.window?.makeFirstResponder(self?.input) }
    }

    required init?(coder: NSCoder) { fatalError() }

    var session: Session { account!.session }

    func windowDidBecomeKey(_ notification: Notification) {
        session.markRead(channel)
        catchUp()
    }

    func windowWillClose(_ notification: Notification) {
        typingTimer?.invalidate()
        web.navigationDelegate = nil
        web.stopLoading()
        account?.chatClosed(channel.id)
    }

    // MARK: - web view

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        pageReady = true
        let scripts = pendingScripts
        pendingScripts.removeAll()
        for s in scripts { webView.evaluateJavaScript(s) }
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { decisionHandler(.cancel); return }
        if url.absoluteString == "about:blank" || (action.navigationType == .other && !pageReady && url.scheme != "app") {
            decisionHandler(.allow)
            return
        }
        decisionHandler(.cancel)
        if url.scheme == "app" {
            let action = url.host ?? ""
            let arg = url.path.hasPrefix("/") ? String(url.path.dropFirst()) : url.path
            let raw = url.absoluteString
            // The argument is percent-encoded; take it from the raw string so slashes survive.
            let encoded = raw.components(separatedBy: "app://" + action + "/").dropFirst().first ?? arg
            let value = encoded.removingPercentEncoding ?? arg
            handleLink(action, value)
            return
        }
        openExternal(url.absoluteString)
    }

    private func handleLink(_ action: String, _ arg: String) {
        switch action {
        case "reply": startReply(arg)
        case "edit": startEdit(arg)
        case "delete": deleteMessage(arg)
        case "older": loadOlder()
        case "open": openExternal(arg)
        case "profile": account?.showProfile(arg, guildId: channel.guildId)
        case "react": toggleReaction(arg)
        case "reactpick": pickReaction(arg)
        default: break
        }
    }

    private func script(_ fn: String, _ args: String...) {
        let js = fn + "(" + args.map { Json.js($0) }.joined(separator: ",") + ")"
        if pageReady { web.evaluateJavaScript(js) } else { pendingScripts.append(js) }
    }

    // MARK: - history

    private func loadHistory() {
        statusText = "Loading messages..."
        Task {
            do {
                let list = try await session.api.get("/channels/\(channel.id)/messages?limit=50") as? [Any] ?? []
                let msgs = list.compactMap { $0 as? JObj }.reversed()
                for m in msgs { append(m) }
                if msgs.count < 50 { noMoreOlder = true }
                for m in queued where messages[m.str("id") ?? ""] == nil { append(m) }
                queued.removeAll()
                historyLoaded = true
                setOlderLink()
                statusText = msgs.last.map { "Last message " + ChatWindowController.stamp(Snowflake.time($0.str("id"))) } ?? "Say hi!"
                lastCatchUp = Date()
                if window?.isKeyWindow == true { session.markRead(channel) }
            } catch {
                historyLoaded = true
                statusText = error.localizedDescription
                for m in queued where messages[m.str("id") ?? ""] == nil { append(m) }
                queued.removeAll()
            }
        }
    }

    private var historyLoaded = false

    static func stamp(_ d: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = Calendar.current.isDateInToday(d) ? "h:mm a" : "MMM d, h:mm a"
        return f.string(from: d)
    }

    // Big servers don't always send every message live; when the window comes back into focus,
    // fetch anything newer than what's shown (at most twice a minute).
    private func catchUp() {
        guard historyLoaded, let newestId, !channel.isPrivate, Date().timeIntervalSince(lastCatchUp) > 30 else { return }
        lastCatchUp = Date()
        Task {
            guard let list = try? await session.api.get("/channels/\(channel.id)/messages?limit=50&after=\(newestId)") as? [Any] else { return }
            let msgs = list.compactMap { $0 as? JObj }.sorted { Snowflake.compare($0.str("id"), $1.str("id")) < 0 }
            for m in msgs where messages[m.str("id") ?? ""] == nil { append(m) }
        }
    }

    private func loadOlder() {
        guard !loadingOlder, !noMoreOlder, let oldestId else { return }
        loadingOlder = true
        script("setOlder", "Loading...")
        Task {
            defer { loadingOlder = false; setOlderLink() }
            do {
                let list = try await session.api.get("/channels/\(channel.id)/messages?limit=50&before=\(oldestId)") as? [Any] ?? []
                let msgs = Array(list.compactMap { $0 as? JObj }.reversed())
                if msgs.count < 50 { noMoreOlder = true }
                var html = ""
                var day: Date?
                for m in msgs {
                    messages[m.str("id") ?? ""] = m
                    AutoSaver.consider(session, m)
                    let t = Calendar.current.startOfDay(for: Snowflake.time(m.str("id")))
                    if t != day { html += MessageHtml.dayDivider(t); day = t }
                    html += MessageHtml.render(session, m)
                }
                if let first = msgs.first?.str("id") { self.oldestId = first }
                script("prepend", html)
            } catch { statusText = error.localizedDescription }
        }
    }

    private func setOlderLink() {
        script("setOlder", noMoreOlder ? "<span class=\"t\">This is the start of the conversation.</span>" : "<a href=\"app://older/\">Load older messages</a>")
    }

    private func append(_ m: JObj) {
        guard let id = m.str("id") else { return }
        messages[id] = m
        AutoSaver.consider(session, m)
        Archive.consider(session, m)
        if oldestId == nil || Snowflake.compare(id, oldestId) < 0 { oldestId = id }
        if newestId == nil || Snowflake.compare(id, newestId) > 0 { newestId = id }
        let day = Calendar.current.startOfDay(for: Snowflake.time(id))
        var html = ""
        if day != lastDay { html = MessageHtml.dayDivider(day); lastDay = day }
        script("append", html + MessageHtml.render(session, m))
    }

    func add(_ m: JObj) {
        if let a = m.obj("author")?.str("id") { typing.removeValue(forKey: a); tickTyping() }
        if !historyLoaded { queued.append(m); return }
        guard let id = m.str("id"), messages[id] == nil else { return }
        append(m)
        statusText = "Last message " + ChatWindowController.stamp(Date())
        let mine = m.obj("author")?.str("id") == session.me?.id
        if !mine && m.str("content") == ChatWindowController.buzzText { shake() }
        if window?.isKeyWindow == true && NSApp.isActive { session.markRead(channel) }
    }

    func updated(_ m: JObj) {
        guard let id = m.str("id"), var old = messages[id] else { return }
        for (k, v) in m { old[k] = v } // updates can be partial
        messages[id] = old
        script("replace", "m" + id, MessageHtml.render(session, old))
    }

    func deleted(_ id: String) {
        if messages.removeValue(forKey: id) != nil { script("removeMsg", "m" + id) }
    }

    func reactionChanged(_ t: String, _ d: JObj) {
        guard let me = session.me, let mid = d.str("message_id"), var m = messages[mid] else { return }
        Session.applyReaction(&m, t, d, myId: me.id)
        messages[mid] = m
        script("replace", "m" + mid, MessageHtml.render(session, m))
    }

    // arg is "messageId/emojiKey": adds your reaction, or removes it if it's already yours.
    private func toggleReaction(_ arg: String) {
        guard let slash = arg.firstIndex(of: "/") else { return }
        let mid = String(arg[..<slash]), key = String(arg[arg.index(after: slash)...])
        guard let m = messages[mid] else { return }
        let mine = m.objs("reactions").first { MessageHtml.emojiKey($0.obj("emoji")) == key }?.bool("me") ?? false
        react(mid, key, add: !mine)
    }

    private func react(_ mid: String, _ key: String, add: Bool) {
        let path = "/channels/\(channel.id)/messages/\(mid)/reactions/\(key.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? key)/@me"
        Task {
            do {
                if add { try await session.api.put(path + "?location=Message&type=0", nil) }
                else { try await session.api.delete(path + "?location=Message&burst=false") }
            } catch { statusText = error.localizedDescription }
        }
    }

    // The little smiley picker for "react", at the mouse.
    private func pickReaction(_ mid: String) {
        let menu = NSMenu()
        for e in ChatWindowController.smileys {
            let item = ClosureMenuItem(title: e) { [weak self] in self?.react(mid, e, add: true) }
            menu.addItem(item)
        }
        if let g = session.guild(of: channel), !g.emojis.isEmpty {
            menu.addItem(.separator())
            for em in g.emojis.prefix(60) where !(em.has("available") && !em.bool("available")) {
                let key = (em.str("name") ?? "") + ":" + (em.str("id") ?? "")
                let item = ClosureMenuItem(title: ":" + (em.str("name") ?? "") + ":") { [weak self] in self?.react(mid, key, add: true) }
                ImageCache.shared.load("https://cdn.discordapp.com/emojis/\(em.str("id") ?? "").png?size=32") { img in
                    img?.size = NSSize(width: 18, height: 18)
                    item.image = img
                }
                menu.addItem(item)
            }
        }
        let p = web.convert(window?.mouseLocationOutsideOfEventStream ?? .zero, from: nil)
        menu.popUp(positioning: nil, at: p, in: web)
    }

    // MARK: - composing

    private func startReply(_ id: String) {
        guard let m = messages[id] else { return }
        editing = nil
        replyTo = id
        banner = "Replying to " + session.name(m.obj("author")?.str("id")) + "  (Esc to cancel)"
        window?.makeFirstResponder(input)
    }

    private func startEdit(_ id: String) {
        guard let m = messages[id] else { return }
        replyTo = nil
        editing = id
        input.string = m.str("content") ?? ""
        banner = "Editing your message  (Esc to cancel)"
        window?.makeFirstResponder(input)
    }

    // Up arrow edits your last message, like Discord.
    private func editLast() -> Bool {
        guard let me = session.me else { return false }
        let last = messages.filter { $0.value.obj("author")?.str("id") == me.id }.keys.max { Snowflake.compare($0, $1) < 0 }
        guard let last else { return false }
        startEdit(last)
        return true
    }

    private func deleteMessage(_ id: String) {
        guard confirm("Delete this message?", ok: "Delete") else { return }
        Task {
            do { try await session.api.delete("/channels/\(channel.id)/messages/\(id)") }
            catch { showError(error.localizedDescription) }
        }
    }

    func clearMode() {
        if editing != nil { input.string = "" }
        replyTo = nil
        editing = nil
        pendingFiles.removeAll()
        banner = nil
    }

    func addFiles(_ urls: [URL]) {
        pendingFiles += urls.filter { (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true }
        updateFilesBanner()
        window?.makeFirstResponder(input)
    }

    private func updateFilesBanner() {
        if pendingFiles.isEmpty { if replyTo == nil && editing == nil { banner = nil }; return }
        banner = "Attached: " + pendingFiles.map(\.lastPathComponent).joined(separator: ", ") + "  - press Return to send (Esc to remove)"
    }

    func wrap(_ w: String) {
        let r = input.selectedRange()
        let selected = (input.string as NSString).substring(with: r)
        input.insertText(w + selected + w, replacementRange: r)
        if r.length == 0 { input.setSelectedRange(NSRange(location: r.location + w.count, length: 0)) }
        window?.makeFirstResponder(input)
    }

    func insert(_ text: String) {
        input.insertText(text, replacementRange: input.selectedRange())
        window?.makeFirstResponder(input)
    }

    private func sendTyping() {
        guard !input.string.isEmpty, editing == nil, Date().timeIntervalSince(lastTypingSent) > 8 else { return }
        lastTypingSent = Date()
        session.api.fire("POST", "/channels/\(channel.id)/typing", nil)
    }

    func sendText(_ text: String) {
        input.string = text
        send()
    }

    func send() {
        var text = input.string
        while text.last?.isWhitespace == true { text.removeLast() }
        guard !text.isEmpty || !pendingFiles.isEmpty, !sending else { return }
        sending = true
        input.isEditable = false
        let files = pendingFiles, reply = replyTo, edit = editing
        Task {
            defer {
                sending = false
                input.isEditable = session.canSend(channel)
                window?.makeFirstResponder(input)
            }
            do {
                if let edit {
                    try await session.api.patch("/channels/\(channel.id)/messages/\(edit)", ["content": text])
                } else {
                    var uploaded: [JObj] = []
                    for (i, f) in files.enumerated() {
                        statusText = "Uploading \(f.lastPathComponent) (\(i + 1)/\(files.count))..."
                        uploaded.append(try await session.api.uploadFile(channel.id, f, index: i))
                    }
                    try await session.api.sendMessage(channel.id, text, replyTo: reply, attachments: uploaded)
                    statusText = "Sent " + ChatWindowController.stamp(Date())
                }
                input.string = ""
                editing = nil
                clearMode()
                lastTypingSent = .distantPast
            } catch {
                statusText = error.localizedDescription
                showError(error.localizedDescription, title: "Couldn't send")
            }
        }
    }

    // MARK: - typing indicator

    func someoneTyping(_ userId: String) {
        typing[userId] = Date()
        tickTyping()
    }

    private func tickTyping() {
        typing = typing.filter { Date().timeIntervalSince($0.value) < 10 }
        if typing.isEmpty {
            if statusText.hasSuffix("typing...") { statusText = "" }
            return
        }
        let names = typing.keys.map { session.name($0) }
        statusText = names.count > 3 ? "Several people are typing..." : names.joined(separator: ", ") + (names.count == 1 ? " is typing..." : " are typing...")
    }

    // MARK: - buzz

    func shake() {
        guard let w = window else { return }
        if w.isMiniaturized { w.deminiaturize(nil) }
        w.orderFrontRegardless()
        let origin = w.frame.origin
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.025, repeats: true) { t in
            Task { @MainActor in
                step += 1
                if step > 16 { t.invalidate(); w.setFrameOrigin(origin); return }
                w.setFrameOrigin(NSPoint(x: origin.x + (step % 2 == 0 ? 8 : -8), y: origin.y + (step % 4 < 2 ? 4 : -4)))
            }
        }
        Notifier.play("Sosumi")
    }

    // MARK: - menu actions

    @objc func sendFile(_ sender: Any?) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.title = "Send files"
        panel.beginSheetModal(for: window!) { [weak self] r in
            if r == .OK { self?.addFiles(panel.urls) }
        }
    }

    @objc func searchMessages(_ sender: Any?) { if let a = account { SearchWindowController.show(a, channel, near: window) } }
    // Edit > Find in a chat window searches its whole history.
    @objc func focusSearch(_ sender: Any?) { searchMessages(sender) }

    @objc func openSaved(_ sender: Any?) { AutoSaver.reveal(AutoSaver.folder(session, channel)) }

    @objc func call(_ sender: Any?) {
        guard let a = account else { return }
        if VoiceManager.shared.isIn(session, channel.id) { VoiceManager.shared.leave() } else { VoiceManager.shared.join(a, channel) }
    }

    @objc func buzz(_ sender: Any?) { sendText(ChatWindowController.buzzText) }
    @objc func loadOlderMenu(_ sender: Any?) { loadOlder() }
    @objc func togglePin(_ sender: Any?) { Favorites.setPinned(session, channel.id, !Favorites.isPinned(session, channel.id)) }
    @objc func toggleFollow(_ sender: Any?) { Favorites.setFollowed(session, channel.id, !Favorites.isFollowed(session, channel.id)) }

    @objc func toggleMembers(_ sender: Any?) {
        showMembers.toggle()
        Prefs.set("ShowMembers", showMembers)
    }

    @objc func toggleOnTop(_ sender: Any?) {
        onTop.toggle()
        window?.level = onTop ? .floating : .normal
    }

    override func validateExtra(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(togglePin(_:)): item.title = Favorites.isPinned(session, channel.id) ? "Unpin from Favorites" : "Pin to Favorites"
        case #selector(toggleFollow(_:)): item.state = Favorites.isFollowed(session, channel.id) ? .on : .off; return !channel.isDm
        case #selector(toggleOnTop(_:)): item.state = onTop ? .on : .off
        case #selector(toggleMembers(_:)): item.state = showMembers ? .on : .off; return !channel.isDm
        case #selector(buzz(_:)): return channel.isPrivate && session.canSend(channel)
        case #selector(call(_:)):
            item.title = VoiceManager.shared.isIn(session, channel.id) ? "Hang Up" : "Call"
            return channel.isPrivate
        case #selector(sendFile(_:)): return session.canSend(channel)
        case #selector(openSaved(_:)): return channel.isPrivate
        default: break
        }
        return true
    }
}

final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
    }

    required init(coder: NSCoder) { fatalError() }

    @objc private func run() { handler() }
}

// The history view; dropping files on it attaches them instead of opening them.
final class LogWebView: WKWebView {
    var onDropFiles: (([URL]) -> Void)?

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        fileURLs(sender).isEmpty ? super.draggingEntered(sender) : .copy
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        fileURLs(sender).isEmpty ? super.draggingUpdated(sender) : .copy
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = fileURLs(sender)
        if urls.isEmpty { return false }
        onDropFiles?(urls)
        return true
    }

    private func fileURLs(_ sender: NSDraggingInfo) -> [URL] {
        sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
    }
}

// The message box: Return sends, Shift-Return makes a new line, Up edits your last message,
// Esc cancels a reply/edit, and pasted or dropped files become attachments.
final class InputTextView: NSTextView {
    var onEnter: (() -> Void)?
    var onUp: (() -> Bool)?
    var onEscape: (() -> Void)?
    var onFiles: (([URL]) -> Void)?
    var onChange: (() -> Void)?

    override func keyDown(with event: NSEvent) {
        let shift = event.modifierFlags.contains(.shift)
        switch event.keyCode {
        case 36, 76:
            if hasMarkedText() { super.keyDown(with: event); return }
            if shift || event.modifierFlags.contains(.option) { insertNewlineIgnoringFieldEditor(nil) } else { onEnter?() }
        case 126 where string.isEmpty:
            if onUp?() != true { super.keyDown(with: event) }
        case 53:
            onEscape?()
        default:
            super.keyDown(with: event)
        }
    }

    override func didChangeText() {
        super.didChangeText()
        onChange?()
    }

    override func paste(_ sender: Any?) {
        let pb = NSPasteboard.general
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            onFiles?(urls)
            return
        }
        if pb.string(forType: .string) == nil, let img = NSImage(pasteboard: pb), let tiff = img.tiffRepresentation,
           let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Buzzcord", isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let f = DateFormatter()
            f.dateFormat = "yyyyMMdd-HHmmss"
            let url = dir.appendingPathComponent("image-\(f.string(from: Date())).png")
            if (try? png.write(to: url)) != nil { onFiles?([url]); return }
        }
        pasteAsPlainText(sender)
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            onFiles?(urls)
            return true
        }
        return super.performDragOperation(sender)
    }
}

struct WebHost: NSViewRepresentable {
    let web: WKWebView
    func makeNSView(context: Context) -> WKWebView { web }
    func updateNSView(_ nsView: WKWebView, context: Context) {}
}

struct InputHost: NSViewRepresentable {
    let input: InputTextView

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.borderType = .noBorder
        scroll.drawsBackground = false
        input.isRichText = false
        input.allowsUndo = true
        input.font = .systemFont(ofSize: 13)
        input.isAutomaticQuoteSubstitutionEnabled = false
        input.isAutomaticDashSubstitutionEnabled = false
        input.isContinuousSpellCheckingEnabled = true
        input.textContainerInset = NSSize(width: 4, height: 5)
        input.drawsBackground = false
        input.isVerticallyResizable = true
        input.isHorizontallyResizable = false
        input.autoresizingMask = [.width]
        input.textContainer?.widthTracksTextView = true
        input.minSize = NSSize(width: 0, height: 0)
        input.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        scroll.documentView = input
        return scroll
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {}
}

struct ChatView: View {
    @ObservedObject var chat: ChatWindowController
    @ObservedObject var session: Session

    private var c: Channel { chat.channel }

    var body: some View {
        VStack(spacing: 0) {
            header
            HStack(alignment: .top, spacing: 8) {
                VStack(spacing: 6) {
                    WebHost(web: chat.web)
                        .overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
                    toolbar
                    if let banner = chat.banner {
                        Button { chat.clearMode() } label: {
                            Text(banner).font(.system(size: 11)).foregroundStyle(Theme.accent).lineLimit(1)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }.buttonStyle(.plain)
                    }
                    HStack(spacing: 6) {
                        InputHost(input: chat.input)
                            .background(Theme.window)
                            .overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
                        Button { chat.send() } label: { Text("Send").frame(width: 50, height: 44) }
                            .disabled(chat.sending || !session.canSend(c))
                    }
                    .frame(height: 64)
                }
                side
            }
            .padding(8)
            HStack {
                Text(chat.statusText).font(.system(size: 11)).foregroundStyle(Theme.sub).lineLimit(1)
                Spacer()
            }
            .padding(.horizontal, 10).padding(.bottom, 5)
        }
        .background(Theme.panel)
    }

    private var header: some View {
        HStack(spacing: 8) {
            let _ = session.revision
            if c.isDm, let id = c.recipientIds.first {
                StatusIcon(status: session.presence(id).status, size: 20)
            } else if c.isGroup {
                Image(systemName: "person.2.fill").foregroundStyle(Color(red: 0.95, green: 0.7, blue: 0.1))
            } else if let g = session.guild(of: c), let icon = g.iconUrl(64) {
                RemoteImage(url: icon, corner: 10).frame(width: 20, height: 20)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(session.title(c)).font(.system(size: 15, weight: .bold)).lineLimit(1)
                Text(subtitle).font(.system(size: 11)).foregroundStyle(Theme.sub).lineLimit(1)
            }
            Spacer()
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .frame(maxWidth: .infinity)
        .background(Theme.header)
    }

    private var subtitle: String {
        if c.isDm, let id = c.recipientIds.first {
            let p = session.presence(id)
            return p.summary ?? Theme.statusLabel(p.status)
        }
        if c.isGroup { return "\(c.recipientIds.count + 1) people" }
        return c.topic ?? ""
    }

    private var toolbar: some View {
        HStack(spacing: 2) {
            fmt("B", "**", .bold, "Bold")
            fmt("I", "*", .italic, "Italic")
            fmt("U", "__", .underline, "Underline")
            fmt("S", "~~", .strike, "Strikethrough")
            fmt("||", "||", .plain, "Spoiler")
            Divider().frame(height: 14).padding(.horizontal, 4)
            Menu {
                ForEach(ChatWindowController.smileys, id: \.self) { e in Button(e) { chat.insert(e) } }
                if let g = session.guild(of: c), !g.emojis.isEmpty {
                    Divider()
                    Menu(g.name + " emoji") {
                        ForEach(Array(g.emojis.prefix(200).enumerated()), id: \.offset) { _, em in
                            Button(":" + (em.str("name") ?? "") + ":") { chat.insert("<:\(em.str("name") ?? ""):\(em.str("id") ?? "")>") }
                        }
                    }
                }
            } label: { Image(systemName: "face.smiling") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .help("Smileys")
            Button("Send File") { chat.sendFile(nil) }.disabled(!session.canSend(c))
            if c.isPrivate {
                Button { chat.buzz(nil) } label: { Text("BUZZ!").italic().bold().foregroundStyle(Theme.buzz) }
                    .help("Shake their window (if they use Buzzcord)")
                    .disabled(!session.canSend(c))
                CallButton(chat: chat)
            }
            Spacer()
        }
        .buttonStyle(.borderless)
        .font(.system(size: 12))
    }

    private enum Style { case bold, italic, underline, strike, plain }

    private func fmt(_ label: String, _ wrap: String, _ style: Style, _ tip: String) -> some View {
        Button { chat.wrap(wrap) } label: {
            Text(label)
                .font(.system(size: 13, weight: .bold, design: .serif))
                .italic(style == .italic).underline(style == .underline).strikethrough(style == .strike)
                .frame(width: 20)
        }.help(tip)
    }

    @ViewBuilder private var side: some View {
        if c.isDm, let id = c.recipientIds.first {
            let p = session.presence(id)
            VStack(spacing: 8) {
                Button { chat.account?.showProfile(id, guildId: nil) } label: {
                    RemoteImage(url: session.user(id).avatarUrl(256), corner: 4)
                        .frame(width: 128, height: 128)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.accentLight))
                }.buttonStyle(.plain).help("View profile (right-click for the full-size picture)")
                .contextMenu {
                    let u = session.user(id)
                    if let url = Picture.avatar(u) {
                        PictureMenu(url: url, title: session.name(id) + " - Profile Picture", fileName: Picture.fileName(u, "profile picture", url: url))
                    }
                }
                if let act = p.mainActivity { ActivityCard(activity: act) }
                Spacer()
                if let me = session.me {
                    RemoteImage(url: me.avatarUrl(128), corner: 4).frame(width: 64, height: 64)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.accentLight))
                }
            }
            .frame(width: p.mainActivity != nil ? 220 : 140)
        } else if chat.showMembers {
            MemberPanel(account: chat.account, session: session, channel: c)
        }
    }
}

// What a friend is playing or listening to, with art and elapsed time.
struct ActivityCard: View {
    let activity: JObj

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(Presence.describe(activity) ?? "").font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.accent).lineLimit(2)
            HStack(alignment: .top, spacing: 6) {
                if let art = artUrl {
                    RemoteImage(url: art, corner: 3).frame(width: 48, height: 48)
                }
                VStack(alignment: .leading, spacing: 2) {
                    if let d = activity.str("details") { Text(d).lineLimit(2) }
                    if let s = activity.str("state") { Text(s).lineLimit(2) }
                    if let start = activity.obj("timestamps")?.long("start"), start > 0 {
                        TimelineView(.periodic(from: .now, by: 1)) { ctx in
                            Text(ActivityCard.elapsed(from: start, now: ctx.date)).foregroundStyle(Theme.sub)
                        }
                    }
                }
                .font(.system(size: 11))
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.window)
        .overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
    }

    private var artUrl: String? {
        guard let img = activity.obj("assets")?.str("large_image") else { return nil }
        if img.hasPrefix("spotify:") { return "https://i.scdn.co/image/" + img.dropFirst(8) }
        if img.hasPrefix("mp:") { return "https://media.discordapp.net/" + img.dropFirst(3) }
        if let app = activity.str("application_id") { return "https://cdn.discordapp.com/app-assets/\(app)/\(img).png?size=128" }
        return nil
    }

    static func elapsed(from start: Int64, now: Date) -> String {
        let secs = max(0, Int(now.timeIntervalSince1970) - Int(start / 1000))
        let h = secs / 3600, m = (secs % 3600) / 60, s = secs % 60
        return (h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)) + " elapsed"
    }
}

struct CallButton: View {
    @ObservedObject var chat: ChatWindowController
    @ObservedObject var manager = VoiceManager.shared

    var body: some View {
        let inCall = manager.isIn(chat.session, chat.channel.id)
        Button { chat.call(nil) } label: {
            Label(inCall ? "Hang Up" : "Call", systemImage: inCall ? "phone.down.fill" : "phone.fill")
                .foregroundStyle(inCall ? Color.red : Theme.activity).bold()
        }
        .help(inCall ? "Leave the call" : "Voice call (rings them if nobody's in the call yet)")
    }
}
