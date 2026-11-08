package com.marcturtal.buzzcord.net

import org.json.JSONObject

class User {
    var id: String = ""
    var username: String? = null
    var globalName: String? = null
    var avatar: String? = null
    var discriminator: String? = null
    var bot = false

    val displayName: String
        get() = globalName?.takeIf { it.isNotEmpty() } ?: username ?: id

    fun avatarUrl(size: Int): String {
        val a = avatar
        if (a.isNullOrEmpty()) {
            val idx = ((id.toLongOrNull() ?: 0L) shr 22) % 6
            return "https://cdn.discordapp.com/embed/avatars/$idx.png"
        }
        return "https://cdn.discordapp.com/avatars/$id/$a.png?size=$size"
    }

    fun update(j: JSONObject) {
        id = j.str("id") ?: id
        if (j.has("username")) username = j.str("username")
        if (j.has("global_name")) globalName = j.str("global_name")
        if (j.has("avatar")) avatar = j.str("avatar")
        if (j.has("discriminator")) discriminator = j.str("discriminator")
        if (j.has("bot")) bot = j.bool("bot")
    }

    companion object {
        fun from(j: JSONObject) = User().also { it.update(j) }
    }
}

class Presence(val status: String = "offline", val activities: List<JSONObject> = emptyList()) {
    val customStatus: String?
        get() {
            for (a in activities) {
                if (a.long("type") != 4L) continue
                var text = a.str("state")
                val emoji = a.obj("emoji")
                if (emoji != null && emoji.str("id") == null) {
                    val e = emoji.str("name")
                    if (e != null) text = if (text.isNullOrEmpty()) e else "$e $text"
                }
                return text
            }
            return null
        }

    // The first non-custom activity: a game, Spotify, a stream...
    val mainActivity: JSONObject? get() = activities.firstOrNull { it.long("type") != 4L }

    // One line for the buddy list: what they're doing, else their custom status.
    val summary: String?
        get() {
            val main = mainActivity ?: return customStatus
            var s = describe(main) ?: ""
            if (main.long("type") == 0L) main.str("details")?.takeIf { it.isNotEmpty() }?.let { s += " - $it" }
            return s
        }

    companion object {
        fun from(j: JSONObject) = Presence(j.str("status") ?: "offline", j.objs("activities"))

        fun describe(a: JSONObject?): String? {
            a ?: return null
            val name = a.str("name") ?: ""
            return when (a.long("type").toInt()) {
                0 -> "Playing $name"
                1 -> "Streaming " + (a.str("details") ?: name)
                2 -> if (name == "Spotify" && a.str("details") != null)
                    "Listening to " + a.str("details") + (a.str("state")?.let { " by $it" } ?: "")
                else "Listening to $name"
                3 -> "Watching $name"
                5 -> "Competing in $name"
                else -> name
            }
        }
    }
}

class Overwrite(val id: String, val type: Int, val allow: Long, val deny: Long)

class Channel {
    var id = ""
    var guildId: String? = null
    var name: String? = null
    var parentId: String? = null
    var topic: String? = null
    var lastMessageId: String? = null
    var icon: String? = null
    var type = 0
    var position = 0
    var recipientIds: List<String> = emptyList()
    var overwrites: List<Overwrite> = emptyList()
    var isRequest = false   // a message request, or a DM Discord filtered as spam

    val isPrivate get() = type == 1 || type == 3
    val isDm get() = type == 1
    val isGroup get() = type == 3
    val isText get() = type == 0 || type == 5
    val isCategory get() = type == 4

    fun iconUrl(size: Int): String? = icon?.takeIf { it.isNotEmpty() }?.let { "https://cdn.discordapp.com/channel-icons/$id/$it.png?size=$size" }

    fun update(j: JSONObject) {
        id = j.str("id") ?: id
        if (j.has("type")) type = j.long("type").toInt()
        if (j.has("guild_id")) guildId = j.str("guild_id")
        if (j.has("name")) name = j.str("name")
        if (j.has("parent_id")) parentId = j.str("parent_id")
        if (j.has("topic")) topic = j.str("topic")
        if (j.has("last_message_id")) lastMessageId = j.str("last_message_id")
        if (j.has("icon")) icon = j.str("icon")
        if (j.has("is_message_request") || j.has("is_spam")) isRequest = j.bool("is_message_request") || j.bool("is_spam")
        if (j.has("position")) position = j.long("position").toInt()
        if (j.has("recipient_ids")) recipientIds = j.strs("recipient_ids")
        else if (j.has("recipients")) recipientIds = j.objs("recipients").mapNotNull { it.str("id") }
        if (j.has("permission_overwrites")) {
            overwrites = j.objs("permission_overwrites").map { o ->
                Overwrite(
                    o.str("id") ?: "",
                    if (o.str("type") == "member" || o.long("type") == 1L) 1 else 0,
                    o.long("allow"), o.long("deny"),
                )
            }
        }
    }

    companion object {
        fun from(j: JSONObject, guildId: String?) = Channel().also {
            it.update(j)
            if (it.guildId == null) it.guildId = guildId
        }
    }
}

class Role(val id: String, val name: String, val permissions: Long, val position: Int)

class Guild {
    var id = ""
    var name = ""
    var icon: String? = null
    var ownerId: String? = null
    val roles = HashMap<String, Role>()
    val channels = HashMap<String, Channel>()
    var myRoles: Set<String> = emptySet()

    fun iconUrl(size: Int): String? = icon?.takeIf { it.isNotEmpty() }?.let { "https://cdn.discordapp.com/icons/$id/$it.png?size=$size" }

    fun update(j: JSONObject) {
        id = j.str("id") ?: id
        // Newer gateway payloads nest the guild's own fields under "properties".
        val props = j.obj("properties") ?: j
        if (props.has("name")) name = props.str("name") ?: ""
        if (props.has("icon")) icon = props.str("icon")
        if (props.has("owner_id")) ownerId = props.str("owner_id")
        if (j.has("roles")) {
            roles.clear()
            for (r in j.objs("roles")) setRole(r)
        }
        if (j.has("channels")) {
            channels.clear()
            for (c in j.objs("channels")) Channel.from(c, id).let { channels[it.id] = it }
        }
    }

    fun setRole(r: JSONObject) {
        val rid = r.str("id") ?: return
        roles[rid] = Role(rid, r.str("name") ?: "", r.long("permissions"), r.long("position").toInt())
    }

    fun permissions(c: Channel?, me: String): Long {
        if (ownerId == me) return -1L
        var perms = roles[id]?.permissions ?: 0L
        for (rid in myRoles) perms = perms or (roles[rid]?.permissions ?: 0L)
        if (perms and ADMINISTRATOR != 0L) return -1L
        c ?: return perms
        for (o in c.overwrites) if (o.type == 0 && o.id == id) perms = (perms and o.deny.inv()) or o.allow
        var allow = 0L
        var deny = 0L
        for (o in c.overwrites) if (o.type == 0 && o.id in myRoles) { allow = allow or o.allow; deny = deny or o.deny }
        perms = (perms and deny.inv()) or allow
        for (o in c.overwrites) if (o.type == 1 && o.id == me) perms = (perms and o.deny.inv()) or o.allow
        return perms
    }

    fun canView(c: Channel, me: String) = permissions(c, me) and VIEW_CHANNEL != 0L

    companion object {
        const val ADMINISTRATOR = 1L shl 3
        const val VIEW_CHANNEL = 1L shl 10
        const val SEND_MESSAGES = 1L shl 11
    }
}

object Snowflake {
    private const val EPOCH = 1_420_070_400_000L

    fun compare(a: String?, b: String?): Int {
        if (a == b) return 0
        if (a == null) return -1
        if (b == null) return 1
        if (a.length != b.length) return if (a.length < b.length) -1 else 1
        return if (a < b) -1 else 1
    }

    fun time(id: String?): Long {
        val v = id?.toLongOrNull() ?: return 0L
        return (v shr 22) + EPOCH
    }

    fun now(): String = ((System.currentTimeMillis() - EPOCH) shl 22).toString()
}
