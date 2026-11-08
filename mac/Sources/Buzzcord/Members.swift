import SwiftUI

// "Who's here" beside a server channel or group chat: Discord's member list for servers (role
// groups, then Online/Offline, coloured names), or the group's members, online first.
struct MemberPanel: View {
    let account: AccountController?
    @ObservedObject var session: Session
    let channel: Channel
    @State private var opened = Date()

    private struct Row: Identifiable {
        let id: String
        var header: String?
        var userId: String?
        var name = ""
        var status = "offline"
        var activity: String?
        var color = 0
    }

    var body: some View {
        let (title, rows) = build()
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 12, weight: .bold)).foregroundStyle(Theme.accent).padding(.leading, 2)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(rows) { r in
                        if let h = r.header {
                            Text(h).font(.system(size: 10, weight: .bold)).foregroundStyle(Theme.sub)
                                .padding(.horizontal, 6).padding(.top, 8).padding(.bottom, 2)
                        } else {
                            member(r)
                        }
                    }
                }
            }
            .background(Theme.window)
            .overlay(RoundedRectangle(cornerRadius: 3).stroke(Theme.accentLight))
        }
        .frame(width: 200)
        .onAppear { if channel.guildId != nil { session.subscribeMembers(channel) } }
        .onDisappear { if channel.guildId != nil { session.unsubscribeMembers(channel) } }
        .task {
            // If Discord never answers, say so instead of "Loading..." forever.
            try? await Task.sleep(nanoseconds: 8_000_000_000)
            opened = .distantPast
        }
    }

    private func member(_ r: Row) -> some View {
        Button { if let id = r.userId { account?.showProfile(id, guildId: channel.guildId) } } label: {
            HStack(spacing: 6) {
                ZStack(alignment: .bottomTrailing) {
                    RemoteImage(url: session.user(r.userId).avatarUrl(64), corner: 13).frame(width: 26, height: 26)
                    StatusIcon(status: r.status, size: 10).offset(x: 2, y: 2)
                }
                VStack(alignment: .leading, spacing: 0) {
                    Text(r.name).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                        .foregroundStyle(r.color != 0 ? Color(red: Double((r.color >> 16) & 0xFF) / 255, green: Double((r.color >> 8) & 0xFF) / 255, blue: Double(r.color & 0xFF) / 255) : Color.primary)
                        .opacity(r.status == "offline" ? 0.55 : 1)
                    if let a = r.activity, !a.isEmpty { Text(a).font(.system(size: 10)).foregroundStyle(Theme.sub).lineLimit(1) }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 6).padding(.vertical, 3)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func build() -> (String, [Row]) {
        let _ = session.memberRevision
        let _ = session.revision
        func row(_ id: String, _ nick: String?, _ color: Int) -> Row {
            let p = session.presence(id)
            return Row(id: "u" + id, userId: id, name: nick ?? session.name(id), status: p.status, activity: p.summary, color: color)
        }
        if channel.isGroup {
            var ids = channel.recipientIds
            if let me = session.me, !ids.contains(me.id) { ids.append(me.id) }
            let byName: (String, String) -> Bool = { session.name($0).localizedCaseInsensitiveCompare(session.name($1)) == .orderedAscending }
            let online = ids.filter { session.presence($0).status != "offline" }.sorted(by: byName)
            let offline = ids.filter { session.presence($0).status == "offline" }.sorted(by: byName)
            var rows: [Row] = []
            if !online.isEmpty { rows.append(Row(id: "h-on", header: "ONLINE — \(online.count)")) }
            rows += online.map { row($0, nil, 0) }
            if !offline.isEmpty { rows.append(Row(id: "h-off", header: "OFFLINE — \(offline.count)")) }
            rows += offline.map { row($0, nil, 0) }
            return ("Members — \(ids.count)", rows)
        }
        let g = session.guild(of: channel)
        guard let ml = session.members(for: channel) else {
            let waited = Date().timeIntervalSince(opened) > 7
            return ("Members", [Row(id: "loading", header: waited ? "DISCORD DIDN'T SEND THE LIST" : "LOADING MEMBERS...")])
        }
        var rows: [Row] = []
        var shown = 0
        var seen = Set<String>()
        for case let item? in ml.items {
            if let grp = item.obj("group") {
                let gid = grp.str("id") ?? ""
                let name = gid == "online" ? "Online" : gid == "offline" ? "Offline" : g?.roles[gid]?.name ?? "Members"
                rows.append(Row(id: "g" + gid + "\(rows.count)", header: name.uppercased() + " — \(grp.long("count"))"))
                continue
            }
            guard let m = item.obj("member"), let u = m.obj("user"), let uid = u.str("id"), !seen.contains(uid) else { continue }
            seen.insert(uid)
            var r = row(uid, m.str("nick"), g?.colorFor(m.strs("roles")) ?? 0)
            if let pres = m.obj("presence"), uid != session.me?.id {
                let p = Presence.from(pres)
                r.status = p.status
                r.activity = p.summary
            }
            rows.append(r)
            shown += 1
        }
        if ml.onlineCount > shown && shown >= 90 { rows.append(Row(id: "more", header: "AND \(ml.onlineCount - shown) MORE ONLINE")) }
        let title = ml.onlineCount > 0 ? "\(ml.onlineCount) online" + (ml.memberCount > 0 ? " of \(ml.memberCount)" : "") : "Members"
        return (title, rows)
    }
}
