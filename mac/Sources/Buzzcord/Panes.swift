import AppKit
import SwiftUI
import WebKit

// A read-only message list (search results, mentions, alert history) in the chat page's
// HTML and styles. Links arrive as (action, argument) like in the chat window.
@MainActor
final class HtmlPane: NSObject, WKNavigationDelegate {
    let web = WKWebView(frame: .zero, configuration: WKWebViewConfiguration())
    var onLink: ((String, String) -> Void)?
    private var ready = false
    private var pending: [String] = []

    override init() {
        super.init()
        web.navigationDelegate = self
        web.setValue(false, forKey: "drawsBackground")
        web.loadHTMLString(MessageHtml.page, baseURL: nil)
    }

    func call(_ fn: String, _ args: String...) {
        let js = fn + "(" + args.map { Json.js($0) }.joined(separator: ",") + ")"
        if ready { web.evaluateJavaScript(js) } else { pending.append(js) }
    }

    func clear() { call("setOlder", ""); call("clearLog") }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        ready = true
        for js in pending { webView.evaluateJavaScript(js) }
        pending.removeAll()
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { decisionHandler(.cancel); return }
        if !ready && url.scheme != "app" && url.scheme != "http" && url.scheme != "https" { decisionHandler(.allow); return }
        decisionHandler(.cancel)
        if url.scheme == "app" {
            let a = url.host ?? ""
            let raw = url.absoluteString.components(separatedBy: "app://" + a + "/").dropFirst().first ?? ""
            onLink?(a, raw.removingPercentEncoding ?? raw)
        } else {
            openExternal(url.absoluteString)
        }
    }

    // Links every list understands; returns false for ones the caller handles itself.
    static func standard(_ account: AccountController?, _ action: String, _ arg: String, guildId: String?) -> Bool {
        switch action {
        case "open": openExternal(arg)
        case "profile": account?.showProfile(arg, guildId: guildId)
        case "channel": if let c = account?.session.findChannel(arg) { account?.openChat(c) }
        default: return false
        }
        return true
    }
}

final class PaneModel: ObservableObject {
    @Published var status = ""
    @Published var query = ""
    @Published var wholeServer = false
    @Published var tab = 0
}

// MARK: - search

// Searches a conversation's history with Discord's own search (the same index the official
// app uses), so it finds messages from years ago, not just what's loaded.
@MainActor
final class SearchWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: SearchWindowController] = [:]
    private static let pageSize = 25
    private let account: AccountController
    private let channel: Channel
    private let pane = HtmlPane()
    private let model = PaneModel()
    private var query = ""
    private var offset = 0
    private var busy = false

    static func show(_ account: AccountController, _ channel: Channel, near: NSWindow?) {
        let key = account.session.tokenKey + channel.id
        if let w = open[key] { w.window?.makeKeyAndOrderFront(nil); return }
        let w = SearchWindowController(account, channel)
        open[key] = w
        if let near, let win = w.window { win.setFrameTopLeftPoint(NSPoint(x: near.frame.maxX - 40, y: near.frame.maxY - 20)) }
        w.showWindow(nil)
    }

    private init(_ account: AccountController, _ channel: Channel) {
        self.account = account
        self.channel = channel
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 560), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "Search " + account.session.title(channel)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        model.status = "Type something and press Return."
        w.contentView = NSHostingView(rootView: SearchView(model: model, pane: pane, isServer: channel.guildId != nil) { [weak self] in self?.start() })
        w.center()
        pane.onLink = { [weak self] a, arg in
            guard let self else { return }
            if a == "more" { self.pane.call("removeMsg", "more"); self.fetch(); return }
            _ = HtmlPane.standard(self.account, a, arg, guildId: self.channel.guildId)
        }
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        SearchWindowController.open.removeValue(forKey: account.session.tokenKey + channel.id)
    }

    private func start() {
        query = model.query.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty, !busy else { return }
        offset = 0
        pane.clear()
        fetch()
    }

    private func fetch() {
        busy = true
        model.status = "Searching..."
        let s = account.session
        let q = query.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? query
        var path = channel.guildId.map { "/guilds/\($0)/messages/search?content=\(q)" + (model.wholeServer ? "" : "&channel_id=\(channel.id)") }
            ?? "/channels/\(channel.id)/messages/search?content=\(q)"
        if offset > 0 { path += "&offset=\(offset)" }
        Task {
            defer { busy = false }
            do {
                guard let r = try await s.api.get(path) as? JObj, let groups = r.arr("messages") else {
                    // 202: Discord is still building the search index for this chat.
                    model.status = "Discord is still indexing this chat. Try again in a few seconds."
                    return
                }
                let total = Int(r.long("total_results"))
                var html = ""
                let f = DateFormatter()
                f.dateFormat = "MMM d, yyyy"
                for case let group as [Any] in groups {
                    let msgs = group.compactMap { $0 as? JObj }
                    guard let hit = msgs.first(where: { $0.bool("hit") }) ?? msgs.first else { continue }
                    var whereText = f.string(from: Snowflake.time(hit.str("id")))
                    if let c = s.findChannel(hit.str("channel_id")), c.id != channel.id { whereText += " in #" + (c.name ?? "") }
                    html += "<div class=\"day\" style=\"text-align:left\">" + MessageHtml.e(whereText) + "</div>" + MessageHtml.render(s, hit)
                }
                offset += groups.count
                pane.call("append", html)
                model.status = total == 0 ? "Nothing found." : "Showing \(offset) of \(total) result\(total == 1 ? "" : "s")."
                if offset < total {
                    pane.call("append", "<div id=\"more\" style=\"text-align:center;margin:8px\"><a href=\"app://more/\">Show more results</a></div>")
                }
            } catch {
                model.status = error.localizedDescription
            }
        }
    }
}

struct SearchView: View {
    @ObservedObject var model: PaneModel
    let pane: HtmlPane
    let isServer: Bool
    let go: () -> Void
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 6) {
            HStack {
                TextField("Words to find...", text: $model.query).textFieldStyle(.roundedBorder).focused($focused).onSubmit(go)
                if isServer { Toggle("Whole server", isOn: $model.wholeServer) }
                Button("Search", action: go).keyboardShortcut(.defaultAction)
            }
            WebHost(web: pane.web).overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
            HStack { Text(model.status).font(.system(size: 11)).foregroundStyle(Theme.sub); Spacer() }
        }
        .padding(8)
        .background(Theme.panel)
        .onAppear { focused = true }
    }
}

// MARK: - alerts history

enum AlertKind: String, Codable { case message, mention, friendRequest, friendAccepted, signIn, followed, unfriended, call }

struct AlertEntry: Codable {
    var time: Date
    var kind: AlertKind
    var title: String
    var text: String
    var channel: String?
    var user: String?
    var seen = false
}

// Every alert Buzzcord raised, per account, newest first (the last 300), so nothing gets missed.
@MainActor
final class AlertHistory: ObservableObject {
    static let keep = 300
    @Published private(set) var entries: [AlertEntry] = []
    private let url: URL

    init(tokenKey: String) {
        url = AppPaths.support.appendingPathComponent("alerts-\(tokenKey).json")
        if let data = try? Data(contentsOf: url) {
            let d = JSONDecoder()
            d.dateDecodingStrategy = .iso8601
            entries = (try? d.decode([AlertEntry].self, from: data)) ?? []
        }
    }

    var unseen: Int { entries.filter { !$0.seen }.count }

    func record(_ e: AlertEntry) {
        var e = e
        e.text = e.text.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
        if e.text.count > 300 { e.text = String(e.text.prefix(300)) + "..." }
        entries.insert(e, at: 0)
        if entries.count > AlertHistory.keep { entries.removeLast(entries.count - AlertHistory.keep) }
        save()
    }

    func markSeen() {
        guard entries.contains(where: { !$0.seen }) else { return }
        for i in entries.indices { entries[i].seen = true }
        save()
    }

    func clear() { entries.removeAll(); save() }

    private func save() {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        try? e.encode(entries).write(to: url, options: .atomic)
    }
}

// "So we don't miss things": Discord's own mentions inbox (it remembers mentions from before
// Buzzcord was open) and Buzzcord's history of every alert it raised.
@MainActor
final class AlertsWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: AlertsWindowController] = [:]
    private let account: AccountController
    private let pane = HtmlPane()
    private let model = PaneModel()
    private var oldest: String?
    private var noMore = false
    private var loading = false
    private var watch: Any?
    private var shownCount = -1

    static func show(_ account: AccountController) {
        let key = account.session.tokenKey
        if let w = open[key] { w.window?.makeKeyAndOrderFront(nil); w.select(account.alerts.unseen > 0 ? 1 : w.model.tab); return }
        let w = AlertsWindowController(account)
        open[key] = w
        if let near = account.buddy.window, let win = w.window {
            win.setFrameTopLeftPoint(NSPoint(x: max(0, near.frame.minX - win.frame.width - 8), y: near.frame.maxY))
        }
        w.showWindow(nil)
    }

    private init(_ account: AccountController) {
        self.account = account
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 600), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "Mentions & notifications" + (account.session.me.map { " - " + $0.displayName } ?? "")
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: AlertsView(model: model, pane: pane, select: { [weak self] in self?.select($0) },
                                                          clearHistory: { [weak self] in self?.clearHistory() }))
        w.center()
        pane.onLink = { [weak self] a, arg in
            guard let self else { return }
            if a == "more" { self.pane.call("removeMsg", "more"); self.loadMentions(); return }
            _ = HtmlPane.standard(self.account, a, arg, guildId: nil)
        }
        // Redraw when an alert comes in (not when they're only marked seen, so new ones stay highlighted).
        watch = account.alerts.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async {
                guard let self, self.model.tab == 1, self.account.alerts.entries.count != self.shownCount else { return }
                self.showHistory()
            }
        }
        select(account.alerts.unseen > 0 ? 1 : 0)
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        AlertsWindowController.open.removeValue(forKey: account.session.tokenKey)
    }

    private func select(_ tab: Int) {
        model.tab = tab
        pane.clear()
        if tab == 0 { oldest = nil; noMore = false; loadMentions() } else { showHistory() }
    }

    private func clearHistory() {
        guard confirm("Clear the notification history?", ok: "Clear") else { return }
        account.alerts.clear()
        showHistory()
    }

    private func loadMentions() {
        guard !loading, !noMore else { return }
        loading = true
        model.status = "Loading mentions..."
        let s = account.session
        let path = "/users/@me/mentions?limit=25&roles=true&everyone=true" + (oldest.map { "&before=\($0)" } ?? "")
        Task {
            defer { loading = false }
            do {
                let list = try await s.api.get(path) as? [Any] ?? []
                guard model.tab == 0 else { return }
                var html = ""
                let f = DateFormatter()
                f.dateFormat = "MMM d, h:mm a"
                var n = 0
                for case let m as JObj in list {
                    n += 1
                    oldest = m.str("id")
                    let c = s.findChannel(m.str("channel_id"))
                    html += "<div class=\"day\" style=\"text-align:left\">" + MessageHtml.e(c.map { s.title($0) } ?? "a channel you can't see")
                        + " &middot; " + MessageHtml.e(f.string(from: Snowflake.time(m.str("id"))))
                    if let c { html += " &middot; <a href=\"app://channel/" + MessageHtml.e(c.id) + "\">open chat</a>" }
                    html += "</div>" + MessageHtml.render(s, m)
                }
                if n < 25 { noMore = true }
                pane.call("append", html)
                if !noMore { pane.call("append", "<div id=\"more\" style=\"text-align:center;margin:8px\"><a href=\"app://more/\">Show older mentions</a></div>") }
                model.status = oldest == nil ? "No mentions. Nice and quiet." : "Mentions from Discord's inbox, newest first."
            } catch {
                model.status = error.localizedDescription
            }
        }
    }

    private func showHistory() {
        pane.clear()
        let list = account.alerts.entries
        shownCount = list.count
        let f = DateFormatter()
        f.dateFormat = "MMM d, h:mm a"
        var html = ""
        for a in list {
            html += "<div class=\"m" + (a.seen ? "" : " hl") + "\"><span class=\"t\">(" + MessageHtml.e(f.string(from: a.time)) + ")</span> "
                + AlertsWindowController.label(a.kind) + " <b>" + MessageHtml.e(a.title) + "</b>"
            if !a.text.isEmpty { html += ": " + MessageHtml.e(a.text) }
            if let c = a.channel, !c.isEmpty { html += " <a href=\"app://channel/" + MessageHtml.e(c) + "\">open</a>" }
            else if let u = a.user { html += " <a href=\"app://profile/" + MessageHtml.e(u) + "\">profile</a>" }
            html += "</div>"
        }
        pane.call("append", html)
        model.status = list.isEmpty ? "Nothing yet. Alerts Buzzcord shows you will be listed here." : "\(list.count) alert\(list.count == 1 ? "" : "s"), newest first."
        DispatchQueue.main.async { self.account.alerts.markSeen() }
    }

    static func label(_ k: AlertKind) -> String {
        switch k {
        case .mention: return "<span class=\"mention\">@</span>"
        case .friendRequest: return "<span class=\"re\">friend request</span>"
        case .friendAccepted: return "<span class=\"re\">new friend</span>"
        case .signIn: return "<span class=\"re\">online</span>"
        case .followed: return "<span class=\"re\">followed</span>"
        case .unfriended: return "<span class=\"re\">unfriended</span>"
        case .call: return "<span class=\"re\">call</span>"
        case .message: return "<span class=\"re\">message</span>"
        }
    }
}

struct AlertsView: View {
    @ObservedObject var model: PaneModel
    let pane: HtmlPane
    let select: (Int) -> Void
    let clearHistory: () -> Void

    var body: some View {
        VStack(spacing: 6) {
            HStack {
                Picker("", selection: Binding(get: { model.tab }, set: { select($0) })) {
                    Text("Mentions").tag(0)
                    Text("Notification history").tag(1)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                Spacer()
                if model.tab == 1 { Button("Clear history", action: clearHistory) }
            }
            WebHost(web: pane.web).overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
            HStack { Text(model.status).font(.system(size: 11)).foregroundStyle(Theme.sub); Spacer() }
        }
        .padding(8)
        .background(Theme.panel)
    }
}
