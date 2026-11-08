import AppKit
import Combine
import SwiftUI

// Everything behind the unread count on the menu bar icon and the Dock: one row per chat,
// on every account, with the messages themselves and Open / Mark as Read.
@MainActor
final class UnreadWindowController: NSWindowController, NSWindowDelegate {
    private static var shared: UnreadWindowController?
    private let model: UnreadModel

    static func show(_ app: AppDelegate) {
        if shared == nil { shared = UnreadWindowController(app) }
        shared?.model.reload()
        AppDelegate.bringForward(shared?.window)
    }

    private init(_ app: AppDelegate) {
        model = UnreadModel(app)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: 520), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "Unread Messages"
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 360, height: 240)
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: UnreadView(model: model))
        w.center()
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) { UnreadWindowController.shared = nil }
}

@MainActor
final class UnreadModel: ObservableObject {
    struct Row: Identifiable {
        let account: AccountController
        let channelId: String
        let count: Int
        let id: String   // account + channel
    }

    struct Preview: Identifiable {
        let id: String
        let from: String
        let text: String
    }

    // What's loaded for a row: its messages, and for a chat Buzzcord hasn't got (a thread) its name.
    struct Detail {
        var previews: [Preview] = []
        var title: String?
        var lastId: String?
        var problem: String?
    }

    private weak var app: AppDelegate?
    @Published private(set) var details: [String: Detail] = [:]
    private var watches: [AnyCancellable] = []
    private var loading: Set<String> = []

    init(_ app: AppDelegate) {
        self.app = app
        reload()
    }

    var accounts: [AccountController] { app?.accounts ?? [] }

    func rows(_ a: AccountController) -> [Row] {
        a.session.unreadEntries.map { Row(account: a, channelId: $0.channelId, count: $0.count, id: a.session.tokenKey + "/" + $0.channelId) }
    }

    var total: Int { accounts.reduce(0) { $0 + $1.session.badgeCount } }

    // Follows every account, so rows disappear as chats get read anywhere.
    func reload() {
        watches = accounts.map { a in
            a.session.objectWillChange.sink { [weak self] _ in
                DispatchQueue.main.async { self?.objectWillChange.send(); self?.loadMissing() }
            }
        }
        objectWillChange.send()
        loadMissing()
    }

    private func loadMissing() {
        for a in accounts {
            for r in rows(a) where details[r.id] == nil && !loading.contains(r.id) && loading.count < 20 {
                loading.insert(r.id)
                Task { await load(r); loading.remove(r.id) }
            }
        }
    }

    // The unread messages themselves: the newest few in a DM or group, and in a server channel
    // the ones that mention you.
    private func load(_ r: Row) async {
        let s = r.account.session
        var d = Detail()
        do {
            if s.findChannel(r.channelId) == nil, let j = try await s.api.get("/channels/\(r.channelId)") as? JObj {
                d.title = "#" + (j.str("name") ?? "thread") + (j.str("guild_id").flatMap { s.guilds[$0]?.name }.map { " — " + $0 } ?? "")
                d.lastId = j.str("last_message_id")
            }
            let c = s.findChannel(r.channelId)
            let read = s.readStates[r.channelId]
            let isPrivate = c?.isPrivate ?? false
            var q = "/channels/\(r.channelId)/messages?limit=" + (isPrivate ? String(min(5, max(r.count, 1))) : "50")
            if !isPrivate, let read { q += "&after=\(read)" }
            var msgs = (try await s.api.get(q) as? [Any] ?? []).compactMap { $0 as? JObj }
            msgs.sort { Snowflake.compare($0.str("id") ?? "", $1.str("id") ?? "") < 0 }   // oldest first
            if let read { msgs = msgs.filter { Snowflake.compare($0.str("id") ?? "", read) > 0 } }
            if !isPrivate {
                let me = s.me?.id ?? ""
                let mentioning = msgs.filter { m in m.bool("mention_everyone") || m.objs("mentions").contains { $0.str("id") == me } || !m.strs("mention_roles").isEmpty }
                if !mentioning.isEmpty { msgs = mentioning }
            }
            d.lastId = d.lastId ?? msgs.last?.str("id")
            d.previews = msgs.suffix(3).map { m in
                var text = m.str("content") ?? ""
                if text.isEmpty { text = m.objs("attachments").isEmpty ? (m.objs("embeds").isEmpty ? "(no text)" : "(a link or embed)") : "(sent a file)" }
                if text == ChatWindowController.buzzText { text = "BUZZ!!!" }
                return Preview(id: m.str("id") ?? UUID().uuidString, from: s.name(m.obj("author")?.str("id")), text: text)
            }
        } catch {
            d.problem = "Couldn't load the messages: \(error.localizedDescription)"
        }
        details[r.id] = d
    }

    func title(_ r: Row) -> String {
        let s = r.account.session
        if let c = s.findChannel(r.channelId) {
            if c.isPrivate { return s.title(c) }
            return "#" + (c.name ?? "") + (s.guild(of: c).map { " — " + $0.name } ?? "")
        }
        return details[r.id]?.title ?? "A thread or channel"
    }

    func kind(_ r: Row) -> String {
        let s = r.account.session
        guard let c = s.findChannel(r.channelId) else { return r.count == 1 ? "1 mention" : "\(r.count) mentions" }
        if c.isPrivate {
            let what = c.isGroup ? "Group chat" : "Direct message"
            return s.readStates[c.id] == nil ? what + ", never opened" : what
        }
        return r.count == 1 ? "1 mention" : "\(r.count) mentions"
    }

    func when(_ r: Row) -> String {
        let id = r.account.session.findChannel(r.channelId)?.lastMessageId ?? details[r.id]?.lastId
        guard let id else { return "" }
        return RelativeDateTimeFormatter().localizedString(for: Snowflake.time(id), relativeTo: Date())
    }

    func canOpen(_ r: Row) -> Bool { r.account.session.findChannel(r.channelId) != nil }

    func open(_ r: Row) {
        guard let c = r.account.session.findChannel(r.channelId) else { return }
        r.account.openChat(c)
    }

    func markRead(_ r: Row) {
        let s = r.account.session
        if let c = s.findChannel(r.channelId), c.lastMessageId != nil { s.markRead(c) }
        else if let last = details[r.id]?.lastId { s.markRead(channelId: r.channelId, upTo: last) }
        details.removeValue(forKey: r.id)
    }

    func markAllRead() {
        for a in accounts { for r in rows(a) { markRead(r) } }
    }
}

struct UnreadView: View {
    @ObservedObject var model: UnreadModel

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    let many = model.accounts.count > 1
                    ForEach(model.accounts, id: \.session.tokenKey) { a in
                        let rows = model.rows(a)
                        if many && !rows.isEmpty {
                            Text((a.session.me?.displayName ?? "Account") + " (\(a.session.badgeCount))")
                                .font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.groupText)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal, 10).padding(.vertical, 4)
                                .background(Theme.group)
                        }
                        ForEach(rows) { r in row(r) }
                    }
                }
            }
            .overlay {
                if model.total == 0 { Text("Nothing unread.").foregroundStyle(Theme.sub) }
            }
            Divider()
            HStack {
                Text(model.total == 1 ? "1 unread" : "\(model.total) unread").font(.system(size: 11)).foregroundStyle(Theme.sub)
                Spacer()
                Button("Mark All as Read") {
                    if confirm("Mark everything here as read, on every account? Discord's other apps will agree.", ok: "Mark All as Read") { model.markAllRead() }
                }
                .disabled(model.total == 0)
            }
            .padding(8)
            .background(Theme.panel)
        }
        .background(Theme.window)
    }

    private func row(_ r: UnreadModel.Row) -> some View {
        let d = model.details[r.id]
        return VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(model.title(r)).font(.system(size: 13, weight: .bold)).lineLimit(1)
                Text("\(r.count)").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                    .padding(.horizontal, 5).padding(.vertical, 1).background(Capsule().fill(Color.red))
                Spacer()
                Text(model.when(r)).font(.system(size: 11)).foregroundStyle(Theme.sub)
            }
            Text(model.kind(r)).font(.system(size: 11)).foregroundStyle(Theme.sub)
            if let d {
                ForEach(d.previews) { p in
                    (Text(p.from + ": ").bold() + Text(p.text)).font(.system(size: 12)).lineLimit(2)
                }
                if let problem = d.problem { Text(problem).font(.system(size: 11)).foregroundStyle(Theme.sub) }
            } else {
                Text("Loading...").font(.system(size: 11)).foregroundStyle(Theme.sub)
            }
            HStack(spacing: 8) {
                if model.canOpen(r) { Button("Open") { model.open(r) } }
                Button("Mark as Read") { model.markRead(r) }
            }
            .controlSize(.small)
        }
        .padding(.horizontal, 10).padding(.vertical, 8)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { model.open(r) }
        .overlay(alignment: .bottom) { Divider() }
    }
}
