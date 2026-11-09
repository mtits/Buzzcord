import Combine
import Foundation

enum Prefs {
    static let d = UserDefaults.standard
    static func bool(_ k: String, _ def: Bool) -> Bool { d.object(forKey: k) == nil ? def : d.bool(forKey: k) }
    static func set(_ k: String, _ v: Any?) { d.set(v, forKey: k) }
    static func string(_ k: String) -> String? { d.string(forKey: k) }
    static func strings(_ k: String) -> [String] { d.stringArray(forKey: k) ?? [] }
}

// Everything Buzzcord knows about one signed-in account. Gateway events arrive on the main
// actor and all state below is only touched there.
@MainActor
final class Session: ObservableObject {
    let token: String
    let api: Api
    private(set) var gateway: Gateway!
    var me: User?
    var isReady = false
    @Published var connectionState = "Connecting..."
    var status = "online"      // online, idle, dnd, invisible
    private(set) var autoAway = false   // idle or screen locked (see AutoAway)
    private var awaySince: Int64 = 0
    var customStatusText: String?

    var users: [String: User] = [:]
    var presences: [String: Presence] = [:]
    var relationships: [String: Int] = [:]    // 1 friend, 2 blocked, 3 incoming, 4 outgoing
    var nicknames: [String: String] = [:]
    var privateChannels: [String: Channel] = [:]
    var guilds: [String: Guild] = [:]
    var guildOrder: [String] = []
    var readStates: [String: String] = [:]
    var mentions: [String: Int] = [:]
    var voiceStates: [String: VoiceState] = [:]
    var ringing: [String: [String]] = [:]     // call channel -> user ids being rung

    // Bumped (at most a few times a second) whenever the buddy list should redraw.
    @Published private(set) var revision = 0
    private var changePending = false

    var onReady: (() -> Void)?
    var onMessageCreate: ((JObj) -> Void)?
    var onMessageUpdate: ((JObj) -> Void)?
    var onMessageDelete: ((String, String) -> Void)?   // channel id, message id
    var onTyping: ((String, String) -> Void)?          // channel id, user id
    var onReaction: ((String, JObj) -> Void)?          // event name, data
    var onPresence: ((String) -> Void)?                // user id
    var onSignedOut: (() -> Void)?
    var onFriendRequest: ((String) -> Void)?
    var onFriendAccepted: ((String) -> Void)?
    var onFriendSignedIn: ((String) -> Void)?
    var onUnfriended: ((String) -> Void)?
    var onIncomingCall: ((String) -> Void)?            // channel id
    var onCallChanged: ((String) -> Void)?             // channel id: ringing changed or the call ended

    static let signInGrace: TimeInterval = 60
    private var readyAt = Date.distantFuture
    private var removedByMe: Set<String> = []
    private var lastAck = Date.distantPast

    init(token: String) {
        self.token = token
        api = Api(token: token)
        status = Prefs.string("Status." + tokenKey) ?? "online"
        gateway = Gateway(token: token) { [weak self] in self?.buildPresence() ?? ["status": "online", "since": 0, "activities": [Any](), "afk": false] }
        gateway.onDispatch = { [weak self] t, d in self?.handle(t, d) }
        gateway.onState = { [weak self] s in self?.connectionState = s }
        gateway.onAuthFailed = { [weak self] in self?.onSignedOut?() }
        // 4001 "unknown opcode" right after a member-list request: this gateway wants the older
        // request format. Switch for good; the reconnect re-sends the subscriptions.
        gateway.onClosed = { [weak self] code in
            guard let self else { return }
            if code == 4001 && !self.legacyMemberRequests && Date().timeIntervalSince(self.lastMemberRequest) < 15 {
                self.legacyMemberRequests = true
                Log.write("member list: switching to the older request format")
            }
        }
    }

    // Settings key for this account that doesn't reveal the token.
    var tokenKey: String {
        if let dot = token.firstIndex(of: ".") { return String(token[..<dot]) }
        return String(token.hashValue, radix: 16)
    }

    func start() { gateway.start() }
    func stop() { gateway.stop() }

    func changed() {
        if changePending { return }
        changePending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            guard let self else { return }
            self.changePending = false
            self.revision &+= 1
        }
    }

    // MARK: - Discord's notification settings (mutes)

    private struct MuteInfo {
        var muted = false
        var mutedUntil = Date.distantFuture
        var level = -1                 // message_notifications: 0 all, 1 mentions, 2 nothing
        var suppressEveryone = false, suppressRoles = false
        var channels: [String: Bool] = [:]
        var active: Bool { muted && Date() < mutedUntil }
    }

    private var muteSettings: [String: MuteInfo] = [:]   // guild id ("" = DMs)

    private func applyGuildSettings(_ g: JObj) {
        var info = MuteInfo(muted: g.bool("muted"), level: g.has("message_notifications") ? Int(g.long("message_notifications")) : -1,
                            suppressEveryone: g.bool("suppress_everyone"), suppressRoles: g.bool("suppress_roles"))
        info.mutedUntil = Session.muteEnd(g.obj("mute_config"))
        for o in g.objs("channel_overrides") {
            if let cid = o.str("channel_id") { info.channels[cid] = o.bool("muted") && Date() < Session.muteEnd(o.obj("mute_config")) }
        }
        muteSettings[g.str("guild_id") ?? ""] = info
    }

    private static func muteEnd(_ cfg: JObj?) -> Date {
        guard let s = cfg?.str("end_time") else { return .distantFuture }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: s) ?? ISO8601DateFormatter().date(from: s) ?? .distantFuture
    }

    func isGuildMuted(_ guildId: String?) -> Bool {
        guard let guildId else { return false }
        return muteSettings[guildId]?.active == true
    }

    // Muted channel, a channel in a muted category, a channel in a muted server, or a muted DM.
    func isMuted(_ c: Channel?) -> Bool {
        guard let c, let m = muteSettings[c.guildId ?? ""] else { return false }
        if m.channels[c.id] == true { return true }
        if let p = c.parentId, m.channels[p] == true { return true }
        return c.guildId != nil && m.active
    }

    // Discord's rules: a direct @you always counts; @everyone and role pings follow the
    // server's suppress settings, and nothing else gets through a muted channel or server.
    func shouldNotifyMention(_ m: JObj) -> Bool {
        guard let me else { return false }
        if m.objs("mentions").contains(where: { $0.str("id") == me.id }) { return true }
        if !mentionsMe(m) { return false }
        let c = findChannel(m.str("channel_id"))
        if isMuted(c) { return false }
        if let c, let info = muteSettings[c.guildId ?? ""] {
            if info.level == 2 { return false }
            if m.bool("mention_everyone") && info.suppressEveryone { return false }
            if !m.bool("mention_everyone") && info.suppressRoles { return false }
        }
        return true
    }

    func mentionsMe(_ m: JObj) -> Bool {
        guard let me else { return false }
        if m.bool("mention_everyone") { return true }
        if m.objs("mentions").contains(where: { $0.str("id") == me.id }) { return true }
        if let gid = m.str("guild_id"), let g = guilds[gid] {
            for r in m.strs("mention_roles") where g.myRoles.contains(r) { return true }
        }
        return false
    }

    // MARK: - lookups

    func user(_ id: String?) -> User {
        if let id, let u = users[id] { return u }
        let u = User()
        u.id = id ?? ""
        u.username = "unknown"
        return u
    }

    func name(_ userId: String?) -> String {
        guard let userId else { return "?" }
        if let n = nicknames[userId], !n.isEmpty { return n }
        return user(userId).displayName
    }

    func presence(_ userId: String?) -> Presence {
        guard let userId else { return Presence() }
        if let me, userId == me.id {
            return Presence(status: status == "invisible" ? "offline" : shownStatus, activities: myActivities)
        }
        return presences[userId] ?? Presence()
    }

    func findChannel(_ id: String?) -> Channel? {
        guard let id else { return nil }
        if let c = privateChannels[id] { return c }
        for g in guilds.values { if let c = g.channels[id] { return c } }
        return nil
    }

    func guild(of c: Channel?) -> Guild? {
        guard let gid = c?.guildId else { return nil }
        return guilds[gid]
    }

    func title(_ c: Channel?) -> String {
        guard let c else { return "?" }
        if c.isDm { return c.recipientIds.first.map { name($0) } ?? "Direct message" }
        if c.isGroup {
            if let n = c.name, !n.isEmpty { return n }
            let names = c.recipientIds.map { name($0) }
            return names.isEmpty ? "Group chat" : names.joined(separator: ", ")
        }
        let g = guild(of: c)
        return "#" + (c.name ?? "") + (g.map { " (\($0.name))" } ?? "")
    }

    func isUnread(_ c: Channel?) -> Bool {
        guard let c, let last = c.lastMessageId, !isMuted(c), !c.isRequest else { return false }
        guard let read = readStates[c.id] else { return Session.neverOpenedCounts(c, last: last) }
        return Snowflake.compare(last, read) > 0
    }

    // Discord keeps no read marker for a chat that was never opened anywhere. Such a DM only
    // counts as unread while its last message is recent: old ones nobody opened stay quiet.
    static let neverOpenedWindow: TimeInterval = 14 * 86400

    static func neverOpenedCounts(_ c: Channel, last: String, now: Date = Date()) -> Bool {
        c.isPrivate && now.timeIntervalSince(Snowflake.time(last)) < neverOpenedWindow
    }

    // Everything in the unread count, one entry per chat: unread DMs and groups (their
    // mentions, or 1), and mentions in server channels and threads.
    var unreadEntries: [(channelId: String, count: Int)] {
        var out: [(String, Int)] = []
        for c in privateChannels.values where isUnread(c) { out.append((c.id, max(1, mentionCount(c.id)))) }
        for (cid, count) in mentions where privateChannels[cid] == nil && count > 0 { out.append((cid, count)) }
        return out.sorted { Snowflake.compare(findChannel($0.0)?.lastMessageId ?? $0.0, findChannel($1.0)?.lastMessageId ?? $1.0) > 0 }
    }

    // Read up to a message in a chat Buzzcord may not have loaded (a thread, say).
    func markRead(channelId: String, upTo last: String) {
        if let c = findChannel(channelId) { c.lastMessageId = c.lastMessageId ?? last; markRead(c); return }
        readStates[channelId] = last
        mentions.removeValue(forKey: channelId)
        changed()
        api.fire("POST", "/channels/\(channelId)/messages/\(last)/ack", ["token": JNull])
    }

    func mentionCount(_ channelId: String) -> Int { mentions[channelId] ?? 0 }

    func canSend(_ c: Channel) -> Bool {
        guard let g = guild(of: c), let me else { return true }
        return g.permissions(in: c, me: me.id) & Guild.sendMessages != 0
    }

    // Unread DMs plus mentions, for the Dock badge.
    var badgeCount: Int { unreadEntries.reduce(0) { $0 + $1.count } }

    // MARK: - actions

    func markRead(_ c: Channel?) {
        guard let c, let last = c.lastMessageId else { return }
        let changedRead = readStates[c.id].map { Snowflake.compare(last, $0) > 0 } ?? true
        readStates[c.id] = last
        let hadMentions = mentions.removeValue(forKey: c.id) != nil
        if !changedRead && !hadMentions { return }
        changed()
        // Tell Discord too, so the phone app agrees. Spaced out like the official client does.
        if Date().timeIntervalSince(lastAck) < 1 { return }
        lastAck = Date()
        api.fire("POST", "/channels/\(c.id)/messages/\(last)/ack", ["token": JNull])
    }

    // What friends see: Away while Auto-Away is on, unless you picked Busy or Invisible.
    var shownStatus: String { autoAway && status == "online" ? "idle" : status }

    func setAutoAway(_ on: Bool) {
        guard on != autoAway else { return }
        autoAway = on
        awaySince = on ? Int64(Date().timeIntervalSince1970 * 1000) : 0
        if status == "online" { pushPresence() }
    }

    func setStatus(_ s: String) {
        status = s
        Prefs.set("Status." + tokenKey, s)
        pushPresence()
        api.fire("PATCH", "/users/@me/settings", ["status": s])
    }

    func setCustomStatus(_ text: String?) {
        customStatusText = (text?.isEmpty ?? true) ? nil : text
        pushPresence()
        api.fire("PATCH", "/users/@me/settings", ["custom_status": customStatusText.map { ["text": $0] as Any } ?? JNull])
    }

    func pushPresence() {
        if gateway.connected { gateway.updatePresence(buildPresence()) }
        if let me { onPresence?(me.id) }
        changed()
    }

    private func buildPresence() -> JObj {
        // afk tells Discord to send notifications to your phone while you're away from the Mac.
        ["status": shownStatus, "since": autoAway ? awaySince : 0, "activities": myActivities, "afk": autoAway]
    }

    // Rich presence and games running on this Mac (when that's on and this account shares them), then the status message.
    var sharesGames: Bool {
        get { Prefs.bool("ShareActivity." + tokenKey, true) }
        set { Prefs.set("ShareActivity." + tokenKey, newValue); pushPresence() }
    }

    private var myActivities: [JObj] {
        var acts = sharesGames ? Playing.activities : []
        if let t = customStatusText, !t.isEmpty { acts.append(["name": "Custom Status", "type": 4, "state": t]) }
        return acts
    }

    // MARK: - friends

    func relationships(ofType type: Int) -> [String] { relationships.filter { $0.value == type }.map(\.key) }

    // By username, like Discord's Add Friend box.
    func sendFriendRequest(_ username: String) async throws {
        var name = username.trimmingCharacters(in: .whitespaces)
        if name.hasPrefix("@") { name.removeFirst() }
        try await api.send("POST", "/users/@me/relationships", ["username": name, "discriminator": JNull], location: "Add Friend")
    }

    // Accepts their request if they sent one, otherwise sends one to them.
    func addOrAcceptFriend(_ userId: String, location: String) async throws {
        try await api.send("PUT", "/users/@me/relationships/\(userId)", JObj(), location: location)
    }

    // Ignores a request, cancels one you sent, or unfriends.
    func removeRelationship(_ userId: String, location: String) async throws {
        noteRemovedByMe(userId)
        try await api.send("DELETE", "/users/@me/relationships/\(userId)", nil, location: location)
    }

    func isBlocked(_ userId: String?) -> Bool { userId.map { relationships[$0] == 2 } ?? false }

    // Blocking a friend also unfriends them. Discord stops their DMs and calls; their messages in
    // servers and group chats still arrive and Buzzcord hides them.
    func block(_ userId: String, location: String) async throws {
        noteRemovedByMe(userId)
        try await api.send("PUT", "/users/@me/relationships/\(userId)", ["type": 2], location: location)
    }

    func unblock(_ userId: String, location: String) async throws {
        try await api.send("DELETE", "/users/@me/relationships/\(userId)", nil, location: location)
    }

    // Takes a one-to-one chat off Recent chats, like the X in Discord's DM list. The messages stay,
    // and it comes back with the next message. Never for a group: closing one means leaving it.
    func closeDm(_ c: Channel) async throws {
        guard c.isDm, privateChannels[c.id] != nil else { return }
        try await api.send("DELETE", "/channels/\(c.id)", nil)
        privateChannels.removeValue(forKey: c.id)
        changed()
    }

    // So the RELATIONSHIP_REMOVE that follows isn't reported as them unfriending you.
    func noteRemovedByMe(_ userId: String) { removedByMe.insert(userId) }

    func openDm(_ userId: String) async throws -> Channel {
        if let c = privateChannels.values.first(where: { $0.isDm && $0.recipientIds == [userId] }) { return c }
        guard let j = try await api.post("/users/@me/channels", ["recipients": [userId]]) as? JObj else {
            throw ApiError(status: 0, body: nil, raw: "Discord didn't open the chat")
        }
        for r in j.objs("recipients") { rememberUser(r) }
        let ch = Channel.from(j, nil)
        privateChannels[ch.id] = ch
        changed()
        return ch
    }

    func voiceMembers(_ channelId: String) -> [String] {
        voiceStates.filter { $0.value.channelId == channelId }.map(\.key)
    }

    // Feeds a gateway event in directly; used by the demo mode.
    func inject(_ t: String, _ d: JObj) { handle(t, d) }

    // MARK: - gateway events

    private func handle(_ t: String, _ d: JObj) {
        switch t {
        case "READY": onReadyEvent(d); resubscribeMembers()
        case "RESUMED": if !memberSubs.isEmpty { resubscribeMembers() }
        case "GUILD_MEMBER_LIST_UPDATE": onMemberListUpdate(d)
        case "READY_SUPPLEMENTAL":
            applyPresences(d)
            applyMembers(d.arr("merged_members"), d.objs("guilds"))
            changed()
        case "MESSAGE_CREATE": onMessage(d)
        case "MESSAGE_UPDATE":
            if let a = d.obj("author") { rememberUser(a) }
            onMessageUpdate?(d)
        case "MESSAGE_DELETE":
            if let c = d.str("channel_id"), let id = d.str("id") { onMessageDelete?(c, id) }
        case "MESSAGE_ACK":
            if let cid = d.str("channel_id") {
                readStates[cid] = d.str("message_id")
                mentions.removeValue(forKey: cid)
                changed()
            }
        case "PRESENCE_UPDATE":
            guard let u = d.obj("user"), let id = u.str("id") else { break }
            if u.has("username") { rememberUser(u) }
            if let me, id == me.id { break } // our own state comes from this app
            let before = presences[id]?.status ?? "offline"
            let p = Presence.from(d)
            presences[id] = p
            onPresence?(id)
            if relationships[id] == 1 || privateChannels.values.contains(where: { $0.isDm && $0.recipientIds.first == id }) { changed() }
            // A friend coming online. Presences flood in right after connecting, so the first minute doesn't count.
            if d.str("guild_id") == nil && before == "offline" && p.status != "offline" && relationships[id] == 1
                && Date().timeIntervalSince(readyAt) >= Session.signInGrace {
                onFriendSignedIn?(id)
            }
        case "MESSAGE_REACTION_ADD", "MESSAGE_REACTION_REMOVE", "MESSAGE_REACTION_REMOVE_ALL", "MESSAGE_REACTION_REMOVE_EMOJI":
            onReaction?(t, d)
        case "USER_GUILD_SETTINGS_UPDATE":
            applyGuildSettings(d)
            changed()
        case "TYPING_START":
            if let cid = d.str("channel_id"), let uid = d.str("user_id"), uid != me?.id {
                if let m = d.obj("member")?.obj("user") { rememberUser(m) }
                onTyping?(cid, uid)
            }
        case "CHANNEL_CREATE", "CHANNEL_UPDATE":
            guard let id = d.str("id") else { break }
            if let gid = d.str("guild_id") {
                if let g = guilds[gid] {
                    if let c = g.channels[id] { c.update(d) } else { g.channels[id] = Channel.from(d, gid) }
                }
            } else {
                for r in d.objs("recipients") { rememberUser(r) }
                if let c = privateChannels[id] { c.update(d) } else { privateChannels[id] = Channel.from(d, nil) }
            }
            changed()
        case "CHANNEL_DELETE":
            if let gid = d.str("guild_id"), let g = guilds[gid] { g.channels.removeValue(forKey: d.str("id") ?? "") }
            else { privateChannels.removeValue(forKey: d.str("id") ?? "") }
            changed()
        case "CHANNEL_RECIPIENT_ADD", "CHANNEL_RECIPIENT_REMOVE":
            guard let u = d.obj("user"), let uid = u.str("id"), let c = privateChannels[d.str("channel_id") ?? ""] else { break }
            rememberUser(u)
            if t == "CHANNEL_RECIPIENT_ADD" { if !c.recipientIds.contains(uid) { c.recipientIds.append(uid) } }
            else { c.recipientIds.removeAll { $0 == uid } }
            changed()
        case "RELATIONSHIP_ADD", "RELATIONSHIP_UPDATE":
            if let u = d.obj("user") { rememberUser(u) }
            guard let id = d.str("id") ?? d.str("user_id") else { break }
            let before = relationships[id] ?? 0
            let type = Int(d.long("type"))
            relationships[id] = type
            if d.has("nickname") { nicknames[id] = d.str("nickname") }
            if t == "RELATIONSHIP_ADD" && type == 3 { onFriendRequest?(id) }
            if before == 4 && type == 1 { onFriendAccepted?(id) }
            changed()
        case "RELATIONSHIP_REMOVE":
            guard let id = d.str("id") else { break }
            let wasFriend = relationships[id] == 1
            relationships.removeValue(forKey: id)
            // A friendship that ended without you doing it: they unfriended (or blocked) you.
            let byMe = removedByMe.remove(id) != nil
            if wasFriend && !byMe { onUnfriended?(id) }
            changed()
        case "VOICE_STATE_UPDATE": onVoiceState(d, d.str("guild_id"))
        case "CALL_CREATE", "CALL_UPDATE":
            guard let cid = d.str("channel_id") else { break }
            let wasRinging = me.map { ringing[cid]?.contains($0.id) == true } ?? false
            if d.has("ringing") { ringing[cid] = d.strs("ringing") }
            for vs in d.objs("voice_states") { onVoiceState(vs, nil) }
            if let me, !wasRinging, ringing[cid]?.contains(me.id) == true { onIncomingCall?(cid) }
            onCallChanged?(cid)
        case "CALL_DELETE":
            if let cid = d.str("channel_id") { ringing.removeValue(forKey: cid); onCallChanged?(cid) }
        case "VOICE_SERVER_UPDATE":
            voiceToken = d.str("token")
            voiceEndpoint = d.str("endpoint")   // nil while Discord moves the call to another server
            voiceServerId = d.str("guild_id") ?? d.str("channel_id")
            completeVoice()
        case "GUILD_CREATE", "GUILD_UPDATE":
            guard let gid = d.str("id") else { break }
            let g = guilds[gid] ?? Guild()
            if guilds[gid] == nil {
                guilds[gid] = g
                if !guildOrder.contains(gid) { guildOrder.append(gid) }
            }
            g.update(d)
            changed()
        case "GUILD_DELETE":
            if !d.bool("unavailable"), let gid = d.str("id") {
                guilds.removeValue(forKey: gid)
                guildOrder.removeAll { $0 == gid }
                changed()
            }
        case "GUILD_ROLE_CREATE", "GUILD_ROLE_UPDATE":
            if let g = guilds[d.str("guild_id") ?? ""], let r = d.obj("role") { g.setRole(r); changed() }
        case "GUILD_MEMBER_UPDATE":
            if let me, d.obj("user")?.str("id") == me.id, let g = guilds[d.str("guild_id") ?? ""] {
                g.myRoles = Set(d.strs("roles"))
                changed()
            }
        case "USER_UPDATE":
            if let me, d.str("id") == me.id { me.update(d); rememberUser(d); changed() }
        default:
            break
        }
    }

    // MARK: - server member lists (the sidebar in channel windows)

    // Discord's member list for a channel: a flat list of group headers ({"group":{id,count}})
    // and members ({"member":{user,roles,nick,presence}}), kept in sync by
    // GUILD_MEMBER_LIST_UPDATE ops. The first 100 entries are requested.
    final class MemberList {
        let id: String, guildId: String
        var memberCount = 0, onlineCount = 0
        var items: [JObj?] = []
        init(id: String, guildId: String) { self.id = id; self.guildId = guildId }
    }

    @Published private(set) var memberRevision = 0
    private var memberChangePending = false
    private var memberLists: [String: MemberList] = [:]        // "guild/list"
    private var channelLists: [String: String] = [:]           // channel -> "guild/list"
    private var memberSubs: [String: [String]] = [:]           // guild -> channels shown
    private var pendingLists: [String: [String]] = [:]
    private var lastSynced: [String: String] = [:]
    private var legacyMemberRequests = false   // fall back to the older opcode if Discord rejects the newer one
    private var lastMemberRequest = Date.distantPast

    private func membersChanged() {
        if memberChangePending { return }
        memberChangePending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self else { return }
            self.memberChangePending = false
            self.memberRevision &+= 1
        }
    }

    func members(for c: Channel?) -> MemberList? {
        guard let c, let key = channelLists[c.id] else { return nil }
        return memberLists[key]
    }

    func subscribeMembers(_ c: Channel) {
        guard let gid = c.guildId else { return }
        var chans = memberSubs[gid] ?? []
        if !chans.contains(c.id) { chans.append(c.id) }
        memberSubs[gid] = chans
        queueBinding(gid, c.id)
        sendMemberRequest(gid)
    }

    func unsubscribeMembers(_ c: Channel) {
        guard let gid = c.guildId, var chans = memberSubs[gid] else { return }
        chans.removeAll { $0 == c.id }
        memberSubs[gid] = chans
        channelLists.removeValue(forKey: c.id)
        sendMemberRequest(gid)
    }

    // Which list belongs to which channel isn't in Discord's payload, so a new channel takes
    // the next list Discord syncs for that server (or, if none comes, the last one synced).
    private func queueBinding(_ gid: String, _ cid: String) {
        var q = pendingLists[gid] ?? []
        if !q.contains(cid) { q.append(cid) }
        pendingLists[gid] = q
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self, let pq = self.pendingLists[gid], pq.contains(cid), let last = self.lastSynced[gid] else { return }
            self.pendingLists[gid] = pq.filter { $0 != cid }
            self.channelLists[cid] = last
            self.membersChanged()
        }
    }

    private func sendMemberRequest(_ gid: String) {
        var channels = JObj()
        for cid in memberSubs[gid] ?? [] { channels[cid] = [[0, 99]] }
        lastMemberRequest = Date()
        let body: JObj = ["typing": true, "activities": true, "threads": true, "channels": channels]
        if legacyMemberRequests {
            var d = body
            d["guild_id"] = gid
            gateway.send(["op": 14, "d": d])
        } else {
            gateway.send(["op": 37, "d": ["subscriptions": [gid: body]] as JObj])
        }
    }

    private func resubscribeMembers() {
        memberLists.removeAll()
        channelLists.removeAll()
        for (gid, chans) in memberSubs where !chans.isEmpty {
            for cid in chans { queueBinding(gid, cid) }
            sendMemberRequest(gid)
        }
    }

    private func onMemberListUpdate(_ d: JObj) {
        guard let gid = d.str("guild_id") else { return }
        let key = gid + "/" + (d.str("id") ?? "")
        let list = memberLists[key] ?? MemberList(id: d.str("id") ?? "", guildId: gid)
        memberLists[key] = list
        list.memberCount = Int(d.long("member_count", Int64(list.memberCount)))
        list.onlineCount = Int(d.long("online_count", Int64(list.onlineCount)))
        var synced = false
        for op in d.objs("ops") {
            switch op.str("op") {
            case "SYNC":
                synced = true
                let range = op.arr("range") ?? []
                let start = (range.first as? NSNumber)?.intValue ?? 0
                let items = op.arr("items") ?? []
                while list.items.count < start + items.count { list.items.append(nil) }
                for (i, it) in items.enumerated() { list.items[start + i] = remember(it as? JObj) }
                if list.items.count > start + items.count && items.count < 100 {
                    list.items.removeSubrange((start + items.count)...)
                }
            case "INSERT":
                let i = Int(op.long("index"))
                if i >= 0 && i <= list.items.count { list.items.insert(remember(op.obj("item")), at: i) }
            case "UPDATE":
                let i = Int(op.long("index"))
                if i >= 0 && i < list.items.count { list.items[i] = remember(op.obj("item")) }
            case "DELETE":
                let i = Int(op.long("index"))
                if i >= 0 && i < list.items.count { list.items.remove(at: i) }
            case "INVALIDATE":
                let range = op.arr("range") ?? []
                guard range.count >= 2, let a = (range[0] as? NSNumber)?.intValue, let b = (range[1] as? NSNumber)?.intValue else { break }
                var i = a
                while i <= b && i < list.items.count { list.items[i] = nil; i += 1 }
            default:
                break
            }
        }
        if synced {
            lastSynced[gid] = key
            let bound = channelLists.values.contains(key)
            if var q = pendingLists[gid], !q.isEmpty {
                if !bound { channelLists[q.removeFirst()] = key }
                // Same list as a channel already showing (common: "everyone can see"): share it.
                else if memberSubs[gid]?.contains(q[0]) == true { channelLists[q.removeFirst()] = key }
                pendingLists[gid] = q
            }
        }
        membersChanged()
    }

    private func remember(_ item: JObj?) -> JObj? {
        if let m = item?.obj("member"), let u = m.obj("user") {
            rememberUser(u)
            // Friends' presence comes from their own updates; for everyone else the list is the source.
            if let p = m.obj("presence"), let id = u.str("id"), id != me?.id, relationships[id] != 1 { presences[id] = Presence.from(p) }
        }
        return item
    }

    private func onVoiceState(_ d: JObj, _ guildId: String?) {
        guard let uid = d.str("user_id") else { return }
        let s = VoiceState.from(d, guildId)
        if s.channelId == nil { voiceStates.removeValue(forKey: uid) } else { voiceStates[uid] = s }
        if let me, uid == me.id, s.channelId != nil, s.channelId == wantedVoiceChannel {
            myVoiceSession = s.sessionId
            completeVoice()
        }
        onVoiceStateChanged?()
        changed()
    }

    // MARK: - voice

    var onVoiceStateChanged: (() -> Void)?
    private var myVoiceSession: String?, voiceToken: String?, voiceEndpoint: String?, voiceServerId: String?, wantedVoiceChannel: String?
    private var pendingVoice: CheckedContinuation<VoiceServer, Error>?
    private var voiceAttempt = 0

    // Asks the main gateway to put us in a voice channel (or a DM/group call when guildId is
    // nil); returns the voice server to connect to.
    func joinVoice(guildId: String?, channelId: String, selfMute: Bool, selfDeaf: Bool) async throws -> VoiceServer {
        // Still in this call as far as Discord knows (the voice connection dropped): asking to
        // join again changes nothing, so no new voice state or server comes. Leave first.
        if let me, voiceStates[me.id]?.channelId == channelId {
            setVoice(guildId: guildId, channelId: nil, selfMute: false, selfDeaf: false)
            try await Task.sleep(nanoseconds: 700_000_000)
        }
        pendingVoice?.resume(throwing: CancellationError())
        pendingVoice = nil
        wantedVoiceChannel = channelId
        myVoiceSession = nil; voiceToken = nil; voiceEndpoint = nil
        voiceAttempt += 1
        let attempt = voiceAttempt
        return try await withCheckedThrowingContinuation { k in
            pendingVoice = k
            gateway.send(["op": 4, "d": ["guild_id": guildId ?? JNull, "channel_id": channelId, "self_mute": selfMute, "self_deaf": selfDeaf, "self_video": false] as JObj])
            DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self] in
                guard let self, self.voiceAttempt == attempt, let p = self.pendingVoice else { return }
                self.pendingVoice = nil
                p.resume(throwing: ApiError(status: 0, body: nil, raw: "Discord didn't answer the voice join"))
            }
        }
    }

    // Mute/deafen as others see it, or leave (channelId nil).
    func setVoice(guildId: String?, channelId: String?, selfMute: Bool, selfDeaf: Bool) {
        if channelId == nil { wantedVoiceChannel = nil }
        gateway.send(["op": 4, "d": ["guild_id": guildId ?? JNull, "channel_id": channelId ?? JNull, "self_mute": selfMute, "self_deaf": selfDeaf, "self_video": false] as JObj])
    }

    private func completeVoice() {
        guard let k = pendingVoice, let sid = myVoiceSession, let token = voiceToken, let endpoint = voiceEndpoint, let cid = wantedVoiceChannel else { return }
        pendingVoice = nil
        k.resume(returning: VoiceServer(endpoint: endpoint, token: token, serverId: voiceServerId ?? cid, sessionId: sid, channelId: cid))
    }

    func isRingingMe(_ channelId: String) -> Bool { me.map { ringing[channelId]?.contains($0.id) == true } ?? false }

    // Rings everyone else in the DM or group (after we've joined its call).
    func ringCall(_ channelId: String) async throws { try await api.post("/channels/\(channelId)/call/ring", ["recipients": JNull]) }

    // Declines a call ringing us.
    func stopRinging(_ channelId: String) {
        guard let me else { return }
        api.fire("POST", "/channels/\(channelId)/call/stop-ringing", ["recipients": [me.id]])
    }

    private func rememberUser(_ u: JObj) {
        guard let id = u.str("id") else { return }
        if let existing = users[id] { existing.update(u) } else { users[id] = User.from(u) }
    }

    private func onReadyEvent(_ d: JObj) {
        guard let meJ = d.obj("user") else { return }
        let meU = User.from(meJ)
        me = meU
        users[meU.id] = meU
        for u in d.objs("users") { rememberUser(u) }

        relationships.removeAll()
        for r in d.objs("relationships") {
            if let u = r.obj("user") { rememberUser(u) }
            guard let id = r.str("id") ?? r.str("user_id") else { continue }
            relationships[id] = Int(r.long("type"))
            if let n = r.str("nickname") { nicknames[id] = n }
        }

        privateChannels.removeAll()
        for c in d.objs("private_channels") {
            for r in c.objs("recipients") { rememberUser(r) }
            let ch = Channel.from(c, nil)
            privateChannels[ch.id] = ch
        }

        guilds.removeAll()
        guildOrder.removeAll()
        voiceStates.removeAll()
        let guildList = d.objs("guilds")
        for gj in guildList {
            let g = Guild()
            g.update(gj)
            guilds[g.id] = g
            guildOrder.append(g.id)
            for vs in gj.objs("voice_states") { onVoiceState(vs, g.id) }
        }
        // Discord's own server order (folders flattened), if it sent one.
        if let folders = d.obj("user_settings")?.objs("guild_folders"), !folders.isEmpty {
            var order: [String] = []
            for f in folders { for gid in f.strs("guild_ids") where guilds[gid] != nil && !order.contains(gid) { order.append(gid) } }
            for gid in guildOrder where !order.contains(gid) { order.append(gid) }
            guildOrder = order
        }
        applyMembers(d.arr("merged_members"), guildList)

        readStates.removeAll()
        mentions.removeAll()
        let rs = d["read_state"]
        let entries = (rs as? JObj)?.arr("entries") ?? (rs as? [Any])
        for case let e as JObj in entries ?? [] {
            guard let id = e.str("id"), let last = e.str("last_message_id") else { continue }
            if e.has("read_state_type") && e.long("read_state_type") != 0 { continue }
            readStates[id] = last
            if e.long("mention_count") > 0 { mentions[id] = Int(e.long("mention_count")) }
        }
        let noMarker = privateChannels.values.filter { readStates[$0.id] == nil && $0.lastMessageId != nil }
        Log.write("read states: \(readStates.count); \(noMarker.count) DMs/groups never opened, \(noMarker.filter { isUnread($0) }.count) of them recent enough to count")

        if let settings = d.obj("user_settings") {
            if Prefs.string("Status." + tokenKey) == nil, let s = settings.str("status") { status = s }
            if let cs = settings.obj("custom_status") { customStatusText = cs.str("text") }
        }

        applyPresences(d)
        muteSettings.removeAll()
        let ugs = d["user_guild_settings"]
        let ugsList = (ugs as? JObj)?.arr("entries") ?? (ugs as? [Any])
        for case let o as JObj in ugsList ?? [] { applyGuildSettings(o) }
        readyAt = Date()
        isReady = true
        gateway.updatePresence(buildPresence())
        onReady?()
        changed()
    }

    private func applyMembers(_ merged: [Any]?, _ guildList: [JObj]) {
        guard let merged, let me else { return }
        for (i, item) in merged.enumerated() where i < guildList.count {
            guard let g = guilds[guildList[i].str("id") ?? ""], let members = item as? [Any] else { continue }
            for case let mj as JObj in members {
                let uid = mj.str("user_id") ?? mj.obj("user")?.str("id")
                if uid == me.id { g.myRoles = Set(mj.strs("roles")) }
            }
        }
    }

    private func applyPresences(_ d: JObj) {
        for p in d.objs("presences") {
            if let id = p.obj("user")?.str("id") ?? p.str("user_id") { presences[id] = Presence.from(p) }
        }
        for p in d.obj("merged_presences")?.objs("friends") ?? [] {
            if let id = p.str("user_id") ?? p.obj("user")?.str("id") { presences[id] = Presence.from(p) }
        }
    }

    private func onMessage(_ m: JObj) {
        let author = m.obj("author")
        if let author { rememberUser(author) }
        guard let cid = m.str("channel_id") else { return }
        if let c = findChannel(cid) {
            c.lastMessageId = m.str("id")
            if let me, author?.str("id") == me.id { readStates[cid] = c.lastMessageId }
            else if me != nil && !isBlocked(author?.str("id")) && (c.isPrivate ? !isMuted(c) : shouldNotifyMention(m)) { mentions[cid] = mentionCount(cid) + 1 }
        } else if m.str("guild_id") == nil {
            // A DM channel we haven't seen yet.
            let ch = Channel()
            ch.id = cid
            ch.type = 1
            ch.lastMessageId = m.str("id")
            if let aid = author?.str("id") { ch.recipientIds = [aid] }
            privateChannels[cid] = ch
            mentions[cid] = 1
        }
        onMessageCreate?(m)
        changed()
    }

    // Updates a stored message's "reactions" list from a MESSAGE_REACTION_* event.
    static func applyReaction(_ message: inout JObj, _ t: String, _ d: JObj, myId: String) {
        if t == "MESSAGE_REACTION_REMOVE_ALL" { message.removeValue(forKey: "reactions"); return }
        var list = message.objs("reactions")
        let key = MessageHtml.emojiKey(d.obj("emoji"))
        let idx = list.firstIndex { MessageHtml.emojiKey($0.obj("emoji")) == key }
        let byMe = d.str("user_id") == myId
        if t == "MESSAGE_REACTION_ADD" {
            var entry: JObj = idx.map { list[$0] } ?? ["emoji": d.obj("emoji") ?? JObj(), "count": 0, "me": false]
            if byMe && entry.bool("me") { return } // our own optimistic update already counted it
            entry["count"] = entry.long("count") + 1
            if byMe { entry["me"] = true }
            if let idx { list[idx] = entry } else { list.append(entry) }
        } else if let idx, t == "MESSAGE_REACTION_REMOVE" {
            var entry = list[idx]
            if byMe && !entry.bool("me") { return }
            entry["count"] = entry.long("count") - 1
            if byMe { entry["me"] = false }
            if entry.long("count") <= 0 { list.remove(at: idx) } else { list[idx] = entry }
        } else if let idx, t == "MESSAGE_REACTION_REMOVE_EMOJI" {
            list.remove(at: idx)
        }
        if list.isEmpty { message.removeValue(forKey: "reactions") } else { message["reactions"] = list }
    }
}
