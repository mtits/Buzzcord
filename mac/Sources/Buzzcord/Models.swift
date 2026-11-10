import Foundation

final class User {
    var id: String = ""
    var username: String?
    var globalName: String?
    var avatar: String?
    var discriminator: String?
    var bot = false
    var premiumType = 0   // 0 none, 1 Nitro Classic, 2 Nitro, 3 Nitro Basic (only sent for yourself)

    var displayName: String {
        if let g = globalName, !g.isEmpty { return g }
        return username ?? id
    }

    // Demo screenshots only (Demo --shots): user id -> picture key preloaded into ImageCache.
    nonisolated(unsafe) static var demoAvatars: [String: String] = [:]

    func avatarUrl(_ size: Int) -> String {
        if let d = User.demoAvatars[id] { return d }
        guard let a = avatar, !a.isEmpty else {
            let idx = ((Int64(id) ?? 0) >> 22) % 6
            return "https://cdn.discordapp.com/embed/avatars/\(idx).png"
        }
        return "https://cdn.discordapp.com/avatars/\(id)/\(a).png?size=\(size)"
    }

    static func from(_ j: JObj) -> User {
        let u = User()
        u.update(j)
        return u
    }

    func update(_ j: JObj) {
        id = j.str("id") ?? id
        if j.has("username") { username = j.str("username") }
        if j.has("global_name") { globalName = j.str("global_name") }
        if j.has("avatar") { avatar = j.str("avatar") }
        if j.has("discriminator") { discriminator = j.str("discriminator") }
        if j.has("bot") { bot = j.bool("bot") }
        if j.has("premium_type") { premiumType = Int(j.long("premium_type")) }
    }
}

struct Presence {
    var status = "offline"
    var activities: [JObj] = []

    static func from(_ j: JObj) -> Presence {
        Presence(status: j.str("status") ?? "offline", activities: j.objs("activities"))
    }

    var customStatus: String? {
        for a in activities where a.long("type") == 4 {
            var text = a.str("state")
            if let emoji = a.obj("emoji"), emoji.str("id") == nil, let e = emoji.str("name") {
                text = (text ?? "").isEmpty ? e : e + " " + text!
            }
            return text
        }
        return nil
    }

    // The first non-custom activity: a game, Spotify, a stream...
    var mainActivity: JObj? { activities.first { $0.long("type") != 4 } }

    static func describe(_ a: JObj?) -> String? {
        guard let a else { return nil }
        let name = a.str("name") ?? ""
        switch a.long("type") {
        case 0: return "Playing " + name
        case 1: return "Streaming " + (a.str("details") ?? name)
        case 2:
            if name == "Spotify", let d = a.str("details") {
                return "Listening to " + d + (a.str("state").map { " by " + $0 } ?? "")
            }
            return "Listening to " + name
        case 3: return "Watching " + name
        case 5: return "Competing in " + name
        default: return name
        }
    }

    // One line for the buddy list: what they're doing, else their custom status.
    var summary: String? {
        if let main = mainActivity {
            var s = Presence.describe(main) ?? ""
            if main.long("type") == 0, let extra = main.str("details"), !extra.isEmpty { s += " - " + extra }
            return s
        }
        return customStatus
    }
}

struct VoiceState {
    var userId: String
    var channelId: String?
    var guildId: String?
    var sessionId: String?
    var selfMute = false, selfDeaf = false, mute = false, deaf = false

    static func from(_ j: JObj, _ guildId: String?) -> VoiceState {
        VoiceState(userId: j.str("user_id") ?? "", channelId: j.str("channel_id"), guildId: j.str("guild_id") ?? guildId, sessionId: j.str("session_id"),
                   selfMute: j.bool("self_mute"), selfDeaf: j.bool("self_deaf"), mute: j.bool("mute"), deaf: j.bool("deaf"))
    }
}

// Where to connect for voice, from VOICE_STATE_UPDATE + VOICE_SERVER_UPDATE.
struct VoiceServer {
    var endpoint: String, token: String, serverId: String, sessionId: String, channelId: String
}

struct Overwrite {
    var id: String
    var type: Int   // 0 role, 1 member
    var allow: Int64
    var deny: Int64
}

final class Channel {
    var id = ""
    var guildId: String?
    var name: String?
    var parentId: String?
    var topic: String?
    var lastMessageId: String?
    var icon: String?
    var ownerId: String?
    var type = 0
    var position = 0
    var recipientIds: [String] = []
    var overwrites: [Overwrite] = []
    var isRequest = false   // a message request or a DM Discord filtered as spam

    var isPrivate: Bool { type == 1 || type == 3 }
    var isDm: Bool { type == 1 }
    var isGroup: Bool { type == 3 }
    var isText: Bool { type == 0 || type == 5 }
    var isCategory: Bool { type == 4 }
    var isVoice: Bool { type == 2 }

    static func from(_ j: JObj, _ guildId: String?) -> Channel {
        let c = Channel()
        c.update(j)
        if c.guildId == nil { c.guildId = guildId }
        return c
    }

    func update(_ j: JObj) {
        id = j.str("id") ?? id
        if j.has("type") { type = Int(j.long("type")) }
        if j.has("guild_id") { guildId = j.str("guild_id") }
        if j.has("name") { name = j.str("name") }
        if j.has("parent_id") { parentId = j.str("parent_id") }
        if j.has("topic") { topic = j.str("topic") }
        if j.has("last_message_id") { lastMessageId = j.str("last_message_id") }
        if j.has("icon") { icon = j.str("icon") }
        if j.has("owner_id") { ownerId = j.str("owner_id") }
        if j.has("is_message_request") || j.has("is_spam") { isRequest = j.bool("is_message_request") || j.bool("is_spam") }
        if j.has("position") { position = Int(j.long("position")) }
        if j.has("recipient_ids") { recipientIds = j.strs("recipient_ids") }
        else if j.has("recipients") { recipientIds = j.objs("recipients").compactMap { $0.str("id") } }
        if j.has("permission_overwrites") {
            overwrites = j.objs("permission_overwrites").map { o in
                Overwrite(id: o.str("id") ?? "", type: o.str("type") == "member" || o.long("type") == 1 ? 1 : 0,
                          allow: o.long("allow"), deny: o.long("deny"))
            }
        }
    }
}

struct Role {
    var id: String
    var name: String
    var permissions: Int64
    var position: Int
    var color: Int
}

final class Guild {
    var id = ""
    var name = ""
    var icon: String?
    var ownerId: String?
    var roles: [String: Role] = [:]
    var channels: [String: Channel] = [:]
    var myRoles: Set<String> = []
    var emojis: [JObj] = []
    var premiumTier = 0   // server boost level 0-3

    func iconUrl(_ size: Int) -> String? {
        guard let icon, !icon.isEmpty else { return nil }
        return "https://cdn.discordapp.com/icons/\(id)/\(icon).png?size=\(size)"
    }

    func update(_ j: JObj) {
        id = j.str("id") ?? id
        // Newer gateway payloads nest the guild's own fields under "properties".
        let props = j.obj("properties") ?? j
        if props.has("name") { name = props.str("name") ?? "" }
        if props.has("icon") { icon = props.str("icon") }
        if props.has("owner_id") { ownerId = props.str("owner_id") }
        if props.has("premium_tier") { premiumTier = Int(props.long("premium_tier")) }
        if j.has("roles") {
            roles.removeAll()
            for r in j.objs("roles") { setRole(r) }
        }
        if j.has("emojis") { emojis = j.objs("emojis").filter { $0.str("id") != nil && $0.str("name") != nil } }
        if j.has("channels") {
            channels.removeAll()
            for c in j.objs("channels") {
                let ch = Channel.from(c, id)
                channels[ch.id] = ch
            }
        }
    }

    func setRole(_ r: JObj) {
        guard let rid = r.str("id") else { return }
        let color = r.obj("colors")?.has("primary_color") == true ? Int(r.obj("colors")!.long("primary_color")) : Int(r.long("color"))
        roles[rid] = Role(id: rid, name: r.str("name") ?? "", permissions: r.long("permissions"), position: Int(r.long("position")), color: color)
    }

    // The colour of a member's highest coloured role (0 if none), as in Discord's member list.
    func colorFor(_ roleIds: [String]) -> Int {
        var best = -1, color = 0
        for rid in roleIds { if let r = roles[rid], r.color != 0, r.position > best { best = r.position; color = r.color } }
        return color
    }

    static let administrator: Int64 = 1 << 3, viewChannel: Int64 = 1 << 10, sendMessages: Int64 = 1 << 11,
               attachFiles: Int64 = 1 << 15, readHistory: Int64 = 1 << 16, manageMessages: Int64 = 1 << 13

    func permissions(in c: Channel?, me: String) -> Int64 {
        if ownerId == me { return -1 }
        var perms = roles[id]?.permissions ?? 0
        for rid in myRoles { perms |= roles[rid]?.permissions ?? 0 }
        if perms & Guild.administrator != 0 { return -1 }
        guard let c else { return perms }
        for o in c.overwrites where o.type == 0 && o.id == id { perms &= ~o.deny; perms |= o.allow }
        var allow: Int64 = 0, deny: Int64 = 0
        for o in c.overwrites where o.type == 0 && myRoles.contains(o.id) { allow |= o.allow; deny |= o.deny }
        perms &= ~deny
        perms |= allow
        for o in c.overwrites where o.type == 1 && o.id == me { perms &= ~o.deny; perms |= o.allow }
        return perms
    }

    func canView(_ c: Channel, me: String) -> Bool { permissions(in: c, me: me) & Guild.viewChannel != 0 }
}

enum Snowflake {
    static func compare(_ a: String?, _ b: String?) -> Int {
        if a == b { return 0 }
        guard let a else { return -1 }
        guard let b else { return 1 }
        if a.count != b.count { return a.count < b.count ? -1 : 1 }
        return a < b ? -1 : 1
    }

    static func time(_ id: String?) -> Date {
        guard let id, let v = Int64(id) else { return .distantPast }
        return Date(timeIntervalSince1970: Double((v >> 22) + 1_420_070_400_000) / 1000)
    }

    static func now() -> String {
        let ms = Int64(Date().timeIntervalSince1970 * 1000)
        return String((ms - 1_420_070_400_000) << 22)
    }
}
