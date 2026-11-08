import AppKit
import SwiftUI

@MainActor
final class BuddyListWindowController: AccountWindowController, NSWindowDelegate, ObservableObject {
    @Published var filter = ""
    @Published var collapsed: Set<String>
    @Published var openGuilds: Set<String>
    @Published var selected: String?
    @Published var focusSearchTick = 0
    private var closingForGood = false

    init(account: AccountController, index: Int) {
        let key = account.session.tokenKey
        collapsed = Set(Prefs.strings("Collapsed." + key))
        openGuilds = Set(Prefs.strings("OpenGuilds." + key))
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 290, height: 640),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.title = "Buzzcord"
        w.minSize = NSSize(width: 230, height: 360)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        self.account = account
        w.delegate = self
        w.contentView = NSHostingView(rootView: BuddyListView(session: account.session, list: self))
        if !w.setFrameUsingName("Buddy." + key) {
            if let vf = NSScreen.main?.visibleFrame {
                w.setFrame(NSRect(x: vf.maxX - 300 - CGFloat(index) * 300, y: vf.maxY - 680, width: 290, height: min(660, vf.height - 40)), display: false)
            }
        }
        w.setFrameAutosaveName("Buddy." + key)
    }

    required init?(coder: NSCoder) { fatalError() }

    func saveState() {
        guard let key = accountSession?.tokenKey else { return }
        Prefs.set("Collapsed." + key, Array(collapsed))
        Prefs.set("OpenGuilds." + key, Array(openGuilds))
    }

    func toggle(_ key: String) {
        if collapsed.contains(key) { collapsed.remove(key) } else { collapsed.insert(key) }
        saveState()
    }

    func toggleGuild(_ gid: String) {
        if openGuilds.contains(gid) { openGuilds.remove(gid) } else { openGuilds.insert(gid) }
        saveState()
    }

    // Closing the buddy list only hides it; the Dock icon (or Messenger > Buddy Lists) brings it back.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if closingForGood { return true }
        sender.orderOut(nil)
        return false
    }

    func closeForGood() {
        closingForGood = true
        window?.close()
    }

    @objc func focusSearch(_ sender: Any?) {
        window?.makeKeyAndOrderFront(nil)
        focusSearchTick += 1
    }

    func open(_ c: Channel) { account?.openChat(c) }
}

// MARK: - rows

enum RowKind {
    case group(String, String)           // key, title
    case friend(String)                  // user id
    case dm(Channel)
    case groupChat(Channel)
    case guild(Guild, Bool)              // open
    case category(String)
    case channel(Channel, Guild?, Bool)  // show guild name
    case voice(Channel, Guild?)
    case voiceUser(String)
    case pending(String, Bool)           // user id, incoming
    case note(String)
    case addFriend(String)
}

struct BuddyRow: Identifiable {
    let id: String
    let kind: RowKind
    var indent: CGFloat = 0
}

@MainActor
enum BuddyRows {
    static let order = ["Favorites", "Recent", "Groups", "Friends", "Offline", "Servers", "Requests"]

    static func build(_ s: Session, _ list: BuddyListWindowController) -> [BuddyRow] {
        guard s.isReady, let me = s.me else { return [BuddyRow(id: "wait", kind: .note(s.connectionState == "Online" ? "Signing in..." : s.connectionState))] }
        let q = list.filter.trimmingCharacters(in: .whitespaces)
        if !q.isEmpty { return search(s, q, me.id) }

        var rows: [BuddyRow] = []
        func group(_ key: String, _ title: String, _ children: () -> Void) {
            rows.append(BuddyRow(id: "h" + key, kind: .group(key, title)))
            if !list.collapsed.contains(key) { children() }
        }
        let byName: (String, String) -> Bool = { s.name($0).localizedCaseInsensitiveCompare(s.name($1)) == .orderedAscending }

        var sections: [String: () -> Void] = [:]

        let incoming = s.relationships(ofType: 3).sorted(by: byName)
        let outgoing = s.relationships(ofType: 4).sorted(by: byName)
        sections["Requests"] = {
            if incoming.isEmpty && outgoing.isEmpty { return }
            group("Requests", "Friend requests" + (incoming.isEmpty ? " (\(outgoing.count) sent)" : " (\(incoming.count) new)")) {
                for id in incoming { rows.append(BuddyRow(id: "p" + id, kind: .pending(id, true))) }
                for id in outgoing { rows.append(BuddyRow(id: "p" + id, kind: .pending(id, false))) }
            }
        }

        let pinned = Favorites.pinned(s).compactMap { s.findChannel($0) }
        sections["Favorites"] = {
            if pinned.isEmpty { return }
            group("Favorites", "Favorites (\(pinned.count))") {
                for c in pinned {
                    if c.isDm { rows.append(BuddyRow(id: "fav" + c.id, kind: .dm(c))) }
                    else if c.isGroup { rows.append(BuddyRow(id: "fav" + c.id, kind: .groupChat(c))) }
                    else { rows.append(BuddyRow(id: "fav" + c.id, kind: .channel(c, s.guild(of: c), true))) }
                }
            }
        }

        let friends = s.relationships.filter { $0.value == 1 }.map(\.key)
        let online = friends.filter { s.presence($0).status != "offline" }.sorted(by: byName)
        let offline = friends.filter { s.presence($0).status == "offline" }.sorted(by: byName)
        sections["Friends"] = {
            group("Friends", "Friends (\(online.count)/\(friends.count))") {
                for id in online { rows.append(BuddyRow(id: "f" + id, kind: .friend(id))) }
            }
        }
        sections["Offline"] = {
            group("Offline", "Offline friends (\(offline.count))") {
                for id in offline { rows.append(BuddyRow(id: "f" + id, kind: .friend(id))) }
            }
        }

        let recent: (Channel, Channel) -> Bool = { Snowflake.compare($0.lastMessageId ?? $0.id, $1.lastMessageId ?? $1.id) > 0 }
        let dms = s.privateChannels.values.filter { !$0.isGroup }.sorted(by: recent)
        let groups = s.privateChannels.values.filter { $0.isGroup }.sorted(by: recent)
        let unreadDms = dms.filter { s.isUnread($0) }.count
        sections["Recent"] = {
            group("Recent", "Recent chats" + (unreadDms > 0 ? " (\(unreadDms) new)" : "")) {
                for c in dms.prefix(25) { rows.append(BuddyRow(id: "c" + c.id, kind: .dm(c))) }
            }
        }
        sections["Groups"] = {
            if groups.isEmpty { return }
            let unread = groups.filter { s.isUnread($0) }.count
            group("Groups", "Group chats (\(groups.count))" + (unread > 0 ? " - \(unread) new" : "")) {
                for c in groups { rows.append(BuddyRow(id: "c" + c.id, kind: .groupChat(c))) }
            }
        }

        sections["Servers"] = {
            group("Servers", "Servers (\(s.guildOrder.count))") {
                for gid in s.guildOrder {
                    guard let g = s.guilds[gid] else { continue }
                    let open = list.openGuilds.contains(gid)
                    rows.append(BuddyRow(id: "g" + gid, kind: .guild(g, open)))
                    if !open { continue }
                    let cats = g.channels.values.filter(\.isCategory).reduce(into: [String: Channel]()) { $0[$1.id] = $1 }
                    let visible = g.channels.values.filter { ($0.isText || $0.isVoice) && g.canView($0, me: me.id) }.sorted { a, b in
                        let pa = a.parentId.flatMap { cats[$0]?.position } ?? -1
                        let pb = b.parentId.flatMap { cats[$0]?.position } ?? -1
                        if pa != pb { return pa < pb }
                        if a.parentId != b.parentId { return (a.parentId ?? "") < (b.parentId ?? "") }
                        if a.isVoice != b.isVoice { return !a.isVoice }
                        return a.position != b.position ? a.position < b.position : Snowflake.compare(a.id, b.id) < 0
                    }
                    var lastParent: String? = "\0"
                    for c in visible {
                        if c.parentId != lastParent, let pid = c.parentId, let cat = cats[pid] {
                            rows.append(BuddyRow(id: "cat" + pid, kind: .category((cat.name ?? "").uppercased()), indent: 14))
                        }
                        lastParent = c.parentId
                        if c.isVoice {
                            rows.append(BuddyRow(id: "v" + c.id, kind: .voice(c, g), indent: 14))
                            for uid in s.voiceMembers(c.id).sorted(by: byName) {
                                rows.append(BuddyRow(id: "vu" + c.id + uid, kind: .voiceUser(uid), indent: 30))
                            }
                        } else {
                            rows.append(BuddyRow(id: "c" + c.id, kind: .channel(c, g, false), indent: 14))
                        }
                    }
                }
            }
        }

        for key in order { sections[key]?() }
        return rows
    }

    // Everything whose name contains the search text, flat: friends, chats, servers, channels.
    static func search(_ s: Session, _ q: String, _ me: String) -> [BuddyRow] {
        func hit(_ x: String?) -> Bool { x?.localizedCaseInsensitiveContains(q) == true }
        var users: [String] = []
        for (id, rel) in s.relationships where rel != 2 {
            let u = s.user(id)
            if hit(s.name(id)) || hit(u.username) || hit(u.globalName) { users.append(id) }
        }
        for c in s.privateChannels.values where c.isDm && c.recipientIds.count == 1 && !users.contains(c.recipientIds[0]) {
            let u = s.user(c.recipientIds[0])
            if hit(s.name(u.id)) || hit(u.username) { users.append(u.id) }
        }
        users.sort { s.name($0).localizedCaseInsensitiveCompare(s.name($1)) == .orderedAscending }
        var rows: [BuddyRow] = []
        var body: [BuddyRow] = []
        for id in users {
            let rel = s.relationships[id] ?? 0
            body.append(BuddyRow(id: "f" + id, kind: rel == 3 || rel == 4 ? .pending(id, rel == 3) : .friend(id)))
        }
        for c in s.privateChannels.values where c.isGroup && hit(s.title(c)) { body.append(BuddyRow(id: "c" + c.id, kind: .groupChat(c))) }
        for gid in s.guildOrder {
            guard let g = s.guilds[gid] else { continue }
            for c in g.channels.values where c.isText && hit(c.name) && g.canView(c, me: me) {
                body.append(BuddyRow(id: "c" + c.id, kind: .channel(c, g, true)))
            }
        }
        rows.append(BuddyRow(id: "hSearch", kind: .note("\(body.count) result\(body.count == 1 ? "" : "s") for \"\(q)\"")))
        rows += body
        if q.count >= 2 && !q.contains(" ") { rows.append(BuddyRow(id: "add", kind: .addFriend(q))) }
        return rows
    }
}

// MARK: - views

struct BuddyListView: View {
    @ObservedObject var session: Session
    @ObservedObject var list: BuddyListWindowController
    @FocusState private var searchFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            BuddyHeader(session: session, account: list.account)
            TextField("Search friends, chats, servers...", text: $list.filter)
                .textFieldStyle(.roundedBorder)
                .focused($searchFocused)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .onChange(of: list.focusSearchTick) { searchFocused = true }
                .onExitCommand { list.filter = "" }
            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    let _ = session.revision
                    ForEach(BuddyRows.build(session, list)) { row in
                        RowView(row: row, session: session, list: list)
                    }
                }
            }
            .background(Theme.window)
            VoiceBarHost(session: session)
            Divider()
            HStack(spacing: 14) {
                Button { list.addFriend(nil) } label: { Label("Add a Friend", systemImage: "person.badge.plus") }
                if let alerts = list.account?.alerts { MentionsButton(alerts: alerts) { list.showAlerts(nil) } }
                Spacer()
            }
            .buttonStyle(.borderless)
            .font(.system(size: 12))
            .padding(.horizontal, 10).padding(.vertical, 6)
            .background(Theme.panel)
            HStack {
                Text(session.connectionState).font(.system(size: 11)).foregroundStyle(Theme.sub)
                Spacer()
            }
            .padding(.horizontal, 10).padding(.bottom, 5)
            .background(Theme.panel)
        }
        .frame(minWidth: 230)
    }
}

struct BuddyHeader: View {
    @ObservedObject var session: Session
    let account: AccountController?
    @ObservedObject private var games = GameDetector.shared
    @ObservedObject private var rich = RichPresence.shared

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            RemoteImage(url: session.me?.avatarUrl(128), corner: 4)
                .frame(width: 56, height: 56)
                .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.accentLight))
                .onTapGesture { if let me = session.me { account?.showProfile(me.id, guildId: nil) } }
                .help("Your profile and game lists")
            VStack(alignment: .leading, spacing: 3) {
                let _ = session.revision
                Text(session.me?.displayName ?? "Signing in...").font(.system(size: 15, weight: .bold)).lineLimit(1)
                Menu {
                    Button("Available") { session.setStatus("online") }
                    Button("Away") { session.setStatus("idle") }
                    Button("Busy") { session.setStatus("dnd") }
                    Button("Invisible") { session.setStatus("invisible") }
                } label: {
                    HStack(spacing: 4) {
                        StatusIcon(status: session.status == "invisible" ? "offline" : session.shownStatus, size: 13)
                        Text(session.shownStatus != session.status ? "Away (idle)" : Theme.statusLabel(session.status)).underline().foregroundStyle(Theme.accent)
                    }
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .font(.system(size: 12))
                Button {
                    if let text = askText(title: "Status message", message: "What should your friends see under your name? Leave it empty to clear it.",
                                          initial: session.customStatusText ?? "", ok: "Set", window: nil) {
                        session.setCustomStatus(text.trimmingCharacters(in: .whitespaces))
                    }
                } label: {
                    Text(session.customStatusText.flatMap { $0.isEmpty ? nil : $0 } ?? "[set a status message]")
                        .italic().foregroundStyle(Theme.sub).lineLimit(1)
                }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                let _ = (games.running, rich.entries)
                ForEach(Playing.items) { g in playing(g) }
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(Theme.header)
    }
}

extension BuddyHeader {
    // What this account tells friends it's playing, with the game's options.
    private func playing(_ g: Playing.Item) -> some View {
        Menu {
            if let id = g.appId {
                GameBoard.addMenu(session, appId: id, name: g.name) { showError($0, title: g.name) }
            }
            Button("Don't Share \(g.name)") { GameDetector.hide(g.name) }
            Button(session.sharesGames ? "Stop Sharing Games on This Account" : "Share Games on This Account") { session.sharesGames.toggle() }
        } label: {
            Text((session.sharesGames ? "Playing " : "Playing (not shared) ") + g.name)
                .foregroundStyle(session.sharesGames ? Theme.activity : Theme.sub).lineLimit(1)
                .help([g.activity.str("details"), g.activity.str("state")].compactMap { $0 }.joined(separator: "\n"))
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .font(.system(size: 11))
    }
}

// One click selects a row, two open it. A row with buttons (friend requests) must not have this
// on the whole row: the row's tap gestures take the click and the buttons never fire.
struct RowClicks: ViewModifier {
    let enabled: Bool
    let single: () -> Void
    let double: () -> Void

    func body(content: Content) -> some View {
        if enabled {
            content.gesture(TapGesture(count: 2).onEnded { double() }.exclusively(before: TapGesture().onEnded { single() }))
        } else {
            content
        }
    }
}

extension BuddyRow {
    // Rows whose own buttons must get their clicks.
    var hasButtons: Bool { if case .pending = kind { return true } else { return false } }
}

struct RowView: View {
    let row: BuddyRow
    let session: Session
    @ObservedObject var list: BuddyListWindowController

    var body: some View {
        content
            .padding(.leading, row.indent)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(list.selected == row.id ? Theme.selected : Color.clear)
            .contentShape(Rectangle())
            .modifier(RowClicks(enabled: !row.hasButtons, single: tap, double: activate))
            .contextMenu { menu }
    }

    private var account: AccountController? { list.account }

    // Friend request buttons: logged, and a failure says why instead of doing nothing.
    static func friendAction(_ session: Session, _ what: String, _ id: String, _ op: @escaping () async throws -> Void) {
        Log.write("friend: \(what) \(id)")
        Task { @MainActor in
            do { try await op() } catch {
                Log.write("friend: \(what) \(id) failed: \(error.localizedDescription)")
                showError("Couldn't \(what) the friend request from \(session.name(id)): \(error.localizedDescription)")
            }
        }
    }

    private func tap() {
        switch row.kind {
        case .group(let key, _): list.toggle(key)
        case .guild(let g, _): list.toggleGuild(g.id)
        default: list.selected = row.id
        }
    }

    private func activate() {
        switch row.kind {
        case .friend(let id), .pending(let id, _): account?.openChat(userId: id)
        case .dm(let c), .groupChat(let c), .channel(let c, _, _): account?.openChat(c)
        case .voice(let c, _): if let account { VoiceManager.shared.join(account, c) }
        case .voiceUser(let id): account?.showProfile(id, guildId: nil)
        case .addFriend(let q): list.addFriendNamed(q)
        default: tap()
        }
    }

    @ViewBuilder private var menu: some View {
        switch row.kind {
        case .friend(let id):
            Button("Send Instant Message") { account?.openChat(userId: id) }
            Button("View Profile") { account?.showProfile(id, guildId: nil) }
            if let c = dmChannel(id) { pinButton(c) }
            Divider()
            Button("Remove Friend...") {
                guard confirm("Remove \(session.name(id)) from your friends?", ok: "Remove") else { return }
                Task { try? await session.removeRelationship(id, location: "Friends") }
            }
        case .dm(let c):
            Button("Send Instant Message") { account?.openChat(c) }
            if let id = c.recipientIds.first { Button("View Profile") { account?.showProfile(id, guildId: nil) } }
            pinButton(c)
            Button("Mark as Read") { session.markRead(c) }
        case .groupChat(let c), .channel(let c, _, _):
            Button("Open") { account?.openChat(c) }
            pinButton(c)
            Button(Favorites.isFollowed(session, c.id) ? "Stop Notifying Me About Every Message" : "Notify Me About Every Message") {
                Favorites.setFollowed(session, c.id, !Favorites.isFollowed(session, c.id))
            }
            Button("Mark as Read") { session.markRead(c) }
        case .guild(let g, _):
            Button("Mark Server as Read") { for c in g.channels.values where session.isUnread(c) { session.markRead(c) } }
        case .pending(let id, let incoming):
            if incoming { Button("Accept") { RowView.friendAction(session, "accept", id) { try await session.addOrAcceptFriend(id, location: "Friends") } } }
            Button(incoming ? "Ignore" : "Cancel Request") { RowView.friendAction(session, incoming ? "ignore" : "cancel", id) { try await session.removeRelationship(id, location: "Friends") } }
            Button("View Profile") { account?.showProfile(id, guildId: nil) }
        case .voiceUser(let id):
            Button("View Profile") { account?.showProfile(id, guildId: nil) }
        case .voice(let c, _):
            if VoiceManager.shared.isIn(session, c.id) { Button("Leave Voice Channel") { VoiceManager.shared.leave() } }
            else { Button("Join Voice Channel") { if let account { VoiceManager.shared.join(account, c) } } }
        default:
            EmptyView()
        }
    }

    private func dmChannel(_ uid: String) -> Channel? {
        session.privateChannels.values.first { $0.isDm && $0.recipientIds == [uid] }
    }

    @ViewBuilder private func pinButton(_ c: Channel) -> some View {
        let pinned = Favorites.isPinned(session, c.id)
        Button(pinned ? "Unpin from Favorites" : "Pin to Favorites") { Favorites.setPinned(session, c.id, !pinned) }
    }

    @ViewBuilder private var content: some View {
        switch row.kind {
        case .group(let key, let title):
            HStack(spacing: 5) {
                Image(systemName: list.collapsed.contains(key) ? "chevron.right" : "chevron.down").font(.system(size: 9, weight: .bold))
                Text(title).font(.system(size: 12, weight: .bold))
            }
            .foregroundStyle(Theme.groupText)
            .padding(.horizontal, 8).padding(.vertical, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.group)
        case .friend(let id):
            person(id, unread: dmChannel(id).map { session.isUnread($0) } ?? false, mentions: dmChannel(id).map { session.mentionCount($0.id) } ?? 0)
        case .dm(let c):
            person(c.recipientIds.first, unread: session.isUnread(c), mentions: session.mentionCount(c.id))
        case .groupChat(let c):
            HStack(spacing: 6) {
                Image(systemName: "person.2.fill").foregroundStyle(Color(red: 0.95, green: 0.7, blue: 0.1)).frame(width: 18)
                Text(session.title(c)).font(.system(size: 12, weight: session.isUnread(c) ? .bold : .semibold)).lineLimit(1)
                Spacer(minLength: 0)
                badge(session.mentionCount(c.id))
            }
            .padding(.horizontal, 8).padding(.vertical, 4)
        case .guild(let g, let open):
            HStack(spacing: 6) {
                Image(systemName: open ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .bold)).foregroundStyle(Theme.sub).frame(width: 8)
                if let icon = g.iconUrl(64) {
                    RemoteImage(url: icon, corner: 9).frame(width: 18, height: 18)
                } else {
                    Circle().fill(Theme.accentLight).frame(width: 18, height: 18)
                        .overlay(Text(String(g.name.prefix(1))).font(.system(size: 9, weight: .bold)))
                }
                let meId = session.me?.id ?? ""
                let unread = g.channels.values.contains { $0.isText && session.isUnread($0) && g.canView($0, me: meId) }
                Text(g.name).font(.system(size: 12, weight: unread ? .bold : .regular)).lineLimit(1)
                    .foregroundStyle(session.isGuildMuted(g.id) ? Theme.sub : Color.primary)
                Spacer(minLength: 0)
                badge(g.channels.keys.reduce(0) { $0 + session.mentionCount($1) })
            }
            .padding(.horizontal, 8).padding(.vertical, 4)
        case .category(let name):
            Text(name).font(.system(size: 10, weight: .semibold)).foregroundStyle(Theme.sub)
                .padding(.horizontal, 8).padding(.top, 5).padding(.bottom, 1)
        case .channel(let c, let g, let showGuild):
            HStack(spacing: 4) {
                Text("#").foregroundStyle(Theme.sub)
                Text(c.name ?? "").font(.system(size: 12, weight: session.isUnread(c) ? .bold : .regular)).lineLimit(1)
                    .foregroundStyle(session.isMuted(c) ? Theme.sub : Color.primary)
                if showGuild, let g { Text("- " + g.name).font(.system(size: 11)).foregroundStyle(Theme.sub).lineLimit(1) }
                if Favorites.isFollowed(session, c.id) { Circle().fill(Theme.accent).frame(width: 5, height: 5) }
                Spacer(minLength: 0)
                badge(session.mentionCount(c.id))
            }
            .font(.system(size: 12))
            .padding(.horizontal, 8).padding(.vertical, 2)
        case .voice(let c, _):
            HStack(spacing: 4) {
                Image(systemName: "speaker.wave.2.fill").font(.system(size: 10))
                    .foregroundStyle(VoiceManager.shared.isIn(session, c.id) ? Theme.activity : Theme.sub)
                Text(c.name ?? "").font(.system(size: 12)).lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8).padding(.vertical, 2)
        case .voiceUser(let id):
            HStack(spacing: 5) {
                RemoteImage(url: session.user(id).avatarUrl(64), corner: 8).frame(width: 16, height: 16)
                Text(session.name(id)).font(.system(size: 11)).lineLimit(1)
            }
            .padding(.horizontal, 8).padding(.vertical, 1)
        case .pending(let id, let incoming):
            HStack(spacing: 6) {
                // Clicks on the picture and name select / open the chat; the buttons get their own.
                HStack(spacing: 6) {
                    RemoteImage(url: session.user(id).avatarUrl(64), corner: 3).frame(width: 26, height: 26)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(session.name(id)).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                        Text(incoming ? "wants to be your friend" : "request sent").font(.system(size: 10)).foregroundStyle(Theme.sub)
                    }
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
                .modifier(RowClicks(enabled: true, single: tap, double: activate))
                if incoming {
                    Button("Accept") { RowView.friendAction(session, "accept", id) { try await session.addOrAcceptFriend(id, location: "Friends") } }
                    Button("Ignore") { RowView.friendAction(session, "ignore", id) { try await session.removeRelationship(id, location: "Friends") } }
                } else {
                    Button("Cancel") { RowView.friendAction(session, "cancel", id) { try await session.removeRelationship(id, location: "Friends") } }
                }
            }
            .controlSize(.small)
            .padding(.horizontal, 8).padding(.vertical, 4)
        case .note(let text):
            Text(text).font(.system(size: 12)).foregroundStyle(Theme.sub).padding(10)
        case .addFriend(let q):
            Label("Add \"\(q)\" as a friend...", systemImage: "person.badge.plus").font(.system(size: 12))
                .foregroundStyle(Theme.accent).padding(.horizontal, 8).padding(.vertical, 5)
        }
    }

    private func person(_ id: String?, unread: Bool, mentions: Int) -> some View {
        let p = session.presence(id)
        return HStack(spacing: 6) {
            StatusIcon(status: p.status, size: 16)
            VStack(alignment: .leading, spacing: 1) {
                Text(session.name(id)).font(.system(size: 12, weight: unread ? .heavy : .semibold)).lineLimit(1)
                if let sum = p.summary, !sum.isEmpty {
                    Text(sum).font(.system(size: 11)).foregroundStyle(p.mainActivity != nil ? Theme.activity : Theme.sub).lineLimit(1)
                }
            }
            Spacer(minLength: 0)
            badge(unread ? max(mentions, 1) : 0)
            RemoteImage(url: session.user(id).avatarUrl(64), corner: 3).frame(width: 30, height: 30)
        }
        .padding(.horizontal, 8).padding(.vertical, 3)
    }

    @ViewBuilder private func badge(_ n: Int) -> some View {
        if n > 0 {
            Text(n > 99 ? "99+" : "\(n)").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                .padding(.horizontal, 5).padding(.vertical, 1)
                .background(Capsule().fill(Color.red))
        }
    }
}

struct MentionsButton: View {
    @ObservedObject var alerts: AlertHistory
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(alerts.unseen > 0 ? "Mentions (\(alerts.unseen))" : "Mentions", systemImage: alerts.unseen > 0 ? "bell.badge" : "bell")
                .fontWeight(alerts.unseen > 0 ? .bold : .regular)
        }
        .help("Mentions & notifications (Shift-Cmd-M)")
    }
}
