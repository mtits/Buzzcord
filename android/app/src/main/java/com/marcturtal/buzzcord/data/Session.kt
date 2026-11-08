package com.marcturtal.buzzcord.data

import android.content.SharedPreferences
import android.os.SystemClock
import android.util.Base64
import com.marcturtal.buzzcord.net.Api
import com.marcturtal.buzzcord.net.Channel
import com.marcturtal.buzzcord.net.Gateway
import com.marcturtal.buzzcord.net.Guild
import com.marcturtal.buzzcord.net.Presence
import com.marcturtal.buzzcord.net.Snowflake
import com.marcturtal.buzzcord.net.User
import com.marcturtal.buzzcord.net.arr
import com.marcturtal.buzzcord.net.bool
import com.marcturtal.buzzcord.net.jarr
import com.marcturtal.buzzcord.net.jobj
import com.marcturtal.buzzcord.net.long
import com.marcturtal.buzzcord.net.obj
import com.marcturtal.buzzcord.net.objs
import com.marcturtal.buzzcord.net.str
import com.marcturtal.buzzcord.net.strs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import kotlin.math.max

sealed interface ChatEvent {
    val channelId: String
    class Created(override val channelId: String, val m: JSONObject) : ChatEvent
    class Updated(override val channelId: String, val m: JSONObject) : ChatEvent
    class Deleted(override val channelId: String, val messageId: String) : ChatEvent
    class Typing(override val channelId: String, val userId: String) : ChatEvent
}

// Everything Buzzcord knows about one signed-in account. All state is touched on the main thread only.
class Session(val token: String, private val scope: CoroutineScope, private val prefs: SharedPreferences) {
    val id: String = idFromToken(token)
    val api = Api(token)
    val gateway = Gateway(token, scope) { buildPresence() }

    var me: User? = null
        private set
    var isReady = false
        private set
    var paused = false
    var signedOut = false
        private set
    var cachedName: String? = null
    var cachedAvatar: String? = null
    val connectionState = MutableStateFlow("Connecting...")
    var status: String = prefs.getString("Status.$id", null) ?: "online"
        private set
    var customStatusText: String? = null
        private set

    val users = HashMap<String, User>()
    val presences = HashMap<String, Presence>()
    val relationships = HashMap<String, Int>()   // 1 friend, 2 blocked, 3 incoming, 4 outgoing
    val nicknames = HashMap<String, String>()
    val privateChannels = HashMap<String, Channel>()
    val guilds = HashMap<String, Guild>()
    val guildOrder = ArrayList<String>()
    val readStates = HashMap<String, String>()
    val mentions = HashMap<String, Int>()

    // Bumped (at most five times a second) whenever lists should redraw.
    val revision = MutableStateFlow(0)
    private var changePending = false
    val events = MutableSharedFlow<ChatEvent>(extraBufferCapacity = 256)

    var onNotify: ((Session, JSONObject) -> Unit)? = null
    var onFriendRequest: ((Session, String) -> Unit)? = null
    var onReadElsewhere: ((Session, String) -> Unit)? = null
    var onReady: ((Session) -> Unit)? = null
    var onSignedOut: ((Session) -> Unit)? = null

    private var readyAt = Long.MAX_VALUE
    private var nextAckAt = 0L

    val displayName: String get() = me?.displayName ?: cachedName ?: "Account"
    val avatarUrl: String? get() = me?.avatarUrl(128) ?: cachedAvatar

    init {
        gateway.onDispatch = { t, d -> handle(t, d) }
        gateway.onState = { s -> connectionState.value = s }
        gateway.onAuthFailed = {
            signedOut = true
            onSignedOut?.invoke(this)
            changed()
        }
    }

    fun start() {
        paused = false
        gateway.start()
    }

    fun stop() {
        paused = true
        gateway.stop()
        changed()
    }

    fun changed() {
        if (changePending) return
        changePending = true
        scope.launch {
            delay(200)
            changePending = false
            revision.value = revision.value + 1
        }
    }

    // MARK: Discord's notification settings (mutes)

    private class MuteInfo(
        val muted: Boolean, val mutedUntil: Long, val level: Int,
        val suppressEveryone: Boolean, val suppressRoles: Boolean,
        val channels: Map<String, Boolean>,
    ) {
        val active: Boolean get() = muted && System.currentTimeMillis() < mutedUntil
    }

    private val muteSettings = HashMap<String, MuteInfo>()   // guild id ("" = DMs)

    private fun applyGuildSettings(g: JSONObject) {
        val chans = HashMap<String, Boolean>()
        for (o in g.objs("channel_overrides")) {
            val cid = o.str("channel_id") ?: continue
            chans[cid] = o.bool("muted") && System.currentTimeMillis() < muteEnd(o.obj("mute_config"))
        }
        muteSettings[g.str("guild_id") ?: ""] = MuteInfo(
            g.bool("muted"), muteEnd(g.obj("mute_config")),
            if (g.has("message_notifications")) g.long("message_notifications").toInt() else -1,
            g.bool("suppress_everyone"), g.bool("suppress_roles"), chans,
        )
    }

    private fun muteEnd(cfg: JSONObject?): Long {
        val s = cfg?.str("end_time") ?: return Long.MAX_VALUE
        return try { Instant.parse(s).toEpochMilli() } catch (e: Exception) { Long.MAX_VALUE }
    }

    fun isMuted(c: Channel?): Boolean {
        c ?: return false
        val m = muteSettings[c.guildId ?: ""] ?: return false
        if (m.channels[c.id] == true) return true
        val p = c.parentId
        if (p != null && m.channels[p] == true) return true
        return c.guildId != null && m.active
    }

    // Discord's rules: a direct @you always counts; @everyone and role pings follow the server's
    // suppress settings, and nothing else gets through a muted channel or server.
    fun shouldNotifyMention(m: JSONObject): Boolean {
        val me = me ?: return false
        if (m.objs("mentions").any { it.str("id") == me.id }) return true
        if (!mentionsMe(m)) return false
        val c = findChannel(m.str("channel_id"))
        if (isMuted(c)) return false
        val info = muteSettings[c?.guildId ?: ""]
        if (c != null && info != null) {
            if (info.level == 2) return false
            if (m.bool("mention_everyone") && info.suppressEveryone) return false
            if (!m.bool("mention_everyone") && info.suppressRoles) return false
        }
        return true
    }

    fun mentionsMe(m: JSONObject): Boolean {
        val me = me ?: return false
        if (m.bool("mention_everyone")) return true
        if (m.objs("mentions").any { it.str("id") == me.id }) return true
        val g = guilds[m.str("guild_id") ?: ""] ?: return false
        return m.strs("mention_roles").any { it in g.myRoles }
    }

    // MARK: lookups

    fun user(id: String?): User = id?.let { users[it] } ?: User().also { it.id = id ?: ""; it.username = "unknown" }

    fun name(userId: String?): String {
        userId ?: return "?"
        nicknames[userId]?.takeIf { it.isNotEmpty() }?.let { return it }
        return user(userId).displayName
    }

    fun presence(userId: String?): Presence {
        userId ?: return Presence()
        if (userId == me?.id) return Presence(if (status == "invisible") "offline" else status, myActivities().let { a -> List(a.length()) { a.getJSONObject(it) } })
        return presences[userId] ?: Presence()
    }

    fun findChannel(id: String?): Channel? {
        id ?: return null
        privateChannels[id]?.let { return it }
        for (g in guilds.values) g.channels[id]?.let { return it }
        return null
    }

    fun guildOf(c: Channel?): Guild? = c?.guildId?.let { guilds[it] }

    fun title(c: Channel?): String {
        c ?: return "?"
        if (c.isDm) return c.recipientIds.firstOrNull()?.let { name(it) } ?: "Direct message"
        if (c.isGroup) {
            c.name?.takeIf { it.isNotEmpty() }?.let { return it }
            val names = c.recipientIds.map { name(it) }
            return if (names.isEmpty()) "Group chat" else names.joinToString(", ")
        }
        return "#" + (c.name ?: "")
    }

    fun chatAvatar(c: Channel?): String? = when {
        c == null -> null
        c.isDm -> user(c.recipientIds.firstOrNull()).avatarUrl(64)
        c.isGroup -> c.iconUrl(64)
        else -> guildOf(c)?.iconUrl(64)
    }

    fun dmWith(userId: String): Channel? = privateChannels.values.firstOrNull { it.isDm && it.recipientIds.firstOrNull() == userId }

    fun isUnread(c: Channel?): Boolean {
        c ?: return false
        val last = c.lastMessageId ?: return false
        if (isMuted(c) || c.isRequest) return false
        val read = readStates[c.id] ?: return neverOpenedCounts(c, last)
        return Snowflake.compare(last, read) > 0
    }

    // Discord keeps no read marker for a chat never opened anywhere. Such a DM only counts as
    // unread while its last message is recent, so old ones nobody opened stay quiet.
    private fun neverOpenedCounts(c: Channel, last: String): Boolean =
        c.isPrivate && System.currentTimeMillis() - Snowflake.time(last) < 14 * 86_400_000L

    fun mentionCount(channelId: String) = mentions[channelId] ?: 0

    // Everything in the unread count, one entry per chat: unread DMs and groups (their mentions,
    // or 1), and mentions in server channels.
    fun unreadEntries(): List<Pair<String, Int>> {
        if (paused || !isReady) return emptyList()
        val out = ArrayList<Pair<String, Int>>()
        for (c in privateChannels.values) if (isUnread(c)) out.add(c.id to max(1, mentionCount(c.id)))
        for ((cid, count) in mentions) if (privateChannels[cid] == null && count > 0) out.add(cid to count)
        return out.sortedWith { a, b -> Snowflake.compare(findChannel(b.first)?.lastMessageId ?: b.first, findChannel(a.first)?.lastMessageId ?: a.first) }
    }

    val badgeCount: Int get() = unreadEntries().sumOf { it.second }

    fun canSend(c: Channel): Boolean {
        val g = guildOf(c) ?: return true
        val me = me ?: return true
        return g.permissions(c, me.id) and Guild.SEND_MESSAGES != 0L
    }

    // Text channels you can see, in Discord's order: by category, then position.
    fun visibleChannels(g: Guild): List<Channel> {
        val meId = me?.id ?: return emptyList()
        val cats = g.channels.values.filter { it.isCategory }.associateBy { it.id }
        return g.channels.values.filter { it.isText && g.canView(it, meId) }.sortedWith(
            compareBy<Channel>({ c -> c.parentId?.let { cats[it]?.position } ?: -1 }, { it.position }, { it.id.length }, { it.id }),
        )
    }

    // DMs and groups, newest first.
    fun recentChats(): List<Channel> = privateChannels.values.filter { !it.isRequest }
        .sortedWith { a, b -> Snowflake.compare(b.lastMessageId ?: b.id, a.lastMessageId ?: a.id) }

    fun relationshipsOf(type: Int): List<String> = relationships.filter { it.value == type }.keys.toList()

    // MARK: actions

    fun markRead(c: Channel?) {
        c ?: return
        val last = c.lastMessageId ?: return
        val changedRead = readStates[c.id]?.let { Snowflake.compare(last, it) > 0 } ?: true
        readStates[c.id] = last
        val hadMentions = mentions.remove(c.id) != null
        if (!changedRead && !hadMentions) return
        changed()
        ack(c.id, last)
    }

    // Read up to a message in a chat that may not be loaded yet (e.g. replying from a notification after a restart).
    fun markRead(channelId: String, upTo: String) {
        findChannel(channelId)?.let { c ->
            if (c.lastMessageId == null || Snowflake.compare(upTo, c.lastMessageId) > 0) c.lastMessageId = upTo
            markRead(c)
            return
        }
        readStates[channelId] = upTo
        mentions.remove(channelId)
        changed()
        ack(channelId, upTo)
    }

    // Acks are spaced 1.1 s apart like the official client, so a burst (Mark All as Read) all reaches Discord.
    private fun ack(channelId: String, last: String) {
        val now = SystemClock.elapsedRealtime()
        nextAckAt = max(now, nextAckAt + 1100)
        val wait = nextAckAt - now
        scope.launch {
            if (wait > 0) delay(wait)
            api.fire("POST", "/channels/$channelId/messages/$last/ack", jobj("token" to null))
        }
    }

    fun setStatus(s: String) {
        status = s
        prefs.edit().putString("Status.$id", s).apply()
        gateway.updatePresence(buildPresence())
        api.fire("PATCH", "/users/@me/settings", jobj("status" to s))
        changed()
    }

    fun setCustomStatus(text: String?) {
        customStatusText = text?.trim()?.takeIf { it.isNotEmpty() }
        gateway.updatePresence(buildPresence())
        api.fire("PATCH", "/users/@me/settings", jobj("custom_status" to customStatusText?.let { jobj("text" to it) }))
        changed()
    }

    private fun myActivities(): JSONArray {
        val acts = JSONArray()
        customStatusText?.let { acts.put(jobj("name" to "Custom Status", "type" to 4, "state" to it)) }
        return acts
    }

    private fun buildPresence(): JSONObject = jobj("status" to status, "since" to 0, "activities" to myActivities(), "afk" to false)

    suspend fun sendFriendRequest(username: String) {
        val name = username.trim().removePrefix("@")
        api.send("POST", "/users/@me/relationships", jobj("username" to name, "discriminator" to null), "Add Friend")
    }

    suspend fun acceptFriend(userId: String) {
        api.send("PUT", "/users/@me/relationships/$userId", JSONObject(), "Friends")
    }

    suspend fun removeRelationship(userId: String) {
        api.send("DELETE", "/users/@me/relationships/$userId", null, "Friends")
    }

    suspend fun openDm(userId: String): Channel {
        dmWith(userId)?.let { return it }
        val j = api.post("/users/@me/channels", jobj("recipients" to jarr(userId))) as? JSONObject
            ?: throw IllegalStateException("Discord didn't open the chat.")
        for (r in j.objs("recipients")) rememberUser(r)
        val c = Channel.from(j, null)
        privateChannels[c.id] = c
        changed()
        return c
    }

    // MARK: gateway events

    private fun handle(t: String, d: JSONObject) {
        when (t) {
            "READY" -> onReadyEvent(d)
            "READY_SUPPLEMENTAL" -> {
                applyPresences(d)
                applyMembers(d.arr("merged_members"), d.objs("guilds"))
                changed()
            }
            "MESSAGE_CREATE" -> onMessage(d)
            "MESSAGE_UPDATE" -> {
                d.obj("author")?.let { rememberUser(it) }
                val cid = d.str("channel_id") ?: return
                events.tryEmit(ChatEvent.Updated(cid, d))
            }
            "MESSAGE_DELETE" -> {
                val cid = d.str("channel_id") ?: return
                val mid = d.str("id") ?: return
                events.tryEmit(ChatEvent.Deleted(cid, mid))
            }
            "MESSAGE_ACK" -> {
                val cid = d.str("channel_id") ?: return
                d.str("message_id")?.let { readStates[cid] = it }
                mentions.remove(cid)
                onReadElsewhere?.invoke(this, cid)
                changed()
            }
            "PRESENCE_UPDATE" -> {
                val u = d.obj("user") ?: return
                val uid = u.str("id") ?: return
                if (u.has("username")) rememberUser(u)
                if (uid == me?.id) return   // our own state comes from this app
                presences[uid] = Presence.from(d)
                if (relationships[uid] == 1 || dmWith(uid) != null) changed()
            }
            "USER_GUILD_SETTINGS_UPDATE" -> { applyGuildSettings(d); changed() }
            "TYPING_START" -> {
                val cid = d.str("channel_id") ?: return
                val uid = d.str("user_id") ?: return
                if (uid == me?.id) return
                d.obj("member")?.obj("user")?.let { rememberUser(it) }
                events.tryEmit(ChatEvent.Typing(cid, uid))
            }
            "CHANNEL_CREATE", "CHANNEL_UPDATE" -> {
                val cid = d.str("id") ?: return
                val gid = d.str("guild_id")
                if (gid != null) {
                    val g = guilds[gid] ?: return
                    g.channels[cid]?.update(d) ?: run { g.channels[cid] = Channel.from(d, gid) }
                } else {
                    for (r in d.objs("recipients")) rememberUser(r)
                    privateChannels[cid]?.update(d) ?: run { privateChannels[cid] = Channel.from(d, null) }
                }
                changed()
            }
            "CHANNEL_DELETE" -> {
                val cid = d.str("id") ?: return
                val gid = d.str("guild_id")
                if (gid != null) guilds[gid]?.channels?.remove(cid) else privateChannels.remove(cid)
                changed()
            }
            "CHANNEL_RECIPIENT_ADD", "CHANNEL_RECIPIENT_REMOVE" -> {
                val u = d.obj("user") ?: return
                val uid = u.str("id") ?: return
                val c = privateChannels[d.str("channel_id") ?: ""] ?: return
                rememberUser(u)
                c.recipientIds = if (t == "CHANNEL_RECIPIENT_ADD") (c.recipientIds + uid).distinct() else c.recipientIds - uid
                changed()
            }
            "RELATIONSHIP_ADD", "RELATIONSHIP_UPDATE" -> {
                d.obj("user")?.let { rememberUser(it) }
                val uid = d.str("id") ?: d.str("user_id") ?: return
                val type = d.long("type").toInt()
                relationships[uid] = type
                d.str("nickname")?.let { nicknames[uid] = it }
                if (t == "RELATIONSHIP_ADD" && type == 3) onFriendRequest?.invoke(this, uid)
                changed()
            }
            "RELATIONSHIP_REMOVE" -> {
                d.str("id")?.let { relationships.remove(it) }
                changed()
            }
            "GUILD_CREATE", "GUILD_UPDATE" -> {
                val gid = d.str("id") ?: return
                val g = guilds.getOrPut(gid) { Guild() }
                if (gid !in guildOrder) guildOrder.add(gid)
                g.update(d)
                changed()
            }
            "GUILD_DELETE" -> {
                if (d.bool("unavailable")) return
                val gid = d.str("id") ?: return
                guilds.remove(gid)
                guildOrder.remove(gid)
                changed()
            }
            "GUILD_ROLE_CREATE", "GUILD_ROLE_UPDATE" -> {
                val g = guilds[d.str("guild_id") ?: ""] ?: return
                d.obj("role")?.let { g.setRole(it); changed() }
            }
            "GUILD_MEMBER_UPDATE" -> {
                val me = me ?: return
                if (d.obj("user")?.str("id") != me.id) return
                val g = guilds[d.str("guild_id") ?: ""] ?: return
                g.myRoles = d.strs("roles").toSet()
                changed()
            }
            "USER_UPDATE" -> {
                val me = me ?: return
                if (d.str("id") == me.id) { me.update(d); changed() }
            }
        }
    }

    private fun rememberUser(u: JSONObject) {
        val uid = u.str("id") ?: return
        users[uid]?.update(u) ?: run { users[uid] = User.from(u) }
    }

    private fun onReadyEvent(d: JSONObject) {
        val meJ = d.obj("user") ?: return
        val meU = User.from(meJ)
        me = meU
        users[meU.id] = meU
        for (u in d.objs("users")) rememberUser(u)

        relationships.clear()
        for (r in d.objs("relationships")) {
            r.obj("user")?.let { rememberUser(it) }
            val uid = r.str("id") ?: r.str("user_id") ?: continue
            relationships[uid] = r.long("type").toInt()
            r.str("nickname")?.let { nicknames[uid] = it }
        }

        privateChannels.clear()
        for (c in d.objs("private_channels")) {
            for (r in c.objs("recipients")) rememberUser(r)
            val ch = Channel.from(c, null)
            privateChannels[ch.id] = ch
        }

        guilds.clear()
        guildOrder.clear()
        val guildList = d.objs("guilds")
        for (gj in guildList) {
            val g = Guild()
            g.update(gj)
            guilds[g.id] = g
            guildOrder.add(g.id)
        }
        // Discord's own server order (folders flattened), if it sent one.
        val folders = d.obj("user_settings")?.objs("guild_folders").orEmpty()
        if (folders.isNotEmpty()) {
            val order = ArrayList<String>()
            for (f in folders) for (gid in f.strs("guild_ids")) if (guilds.containsKey(gid) && gid !in order) order.add(gid)
            for (gid in guildOrder) if (gid !in order) order.add(gid)
            guildOrder.clear()
            guildOrder.addAll(order)
        }
        applyMembers(d.arr("merged_members"), guildList)

        readStates.clear()
        mentions.clear()
        val rs = d.opt("read_state")
        val entries = (rs as? JSONObject)?.optJSONArray("entries") ?: (rs as? JSONArray)
        if (entries != null) for (i in 0 until entries.length()) {
            val e = entries.optJSONObject(i) ?: continue
            val cid = e.str("id") ?: continue
            val last = e.str("last_message_id") ?: continue
            if (e.has("read_state_type") && e.long("read_state_type") != 0L) continue
            readStates[cid] = last
            if (e.long("mention_count") > 0) mentions[cid] = e.long("mention_count").toInt()
        }

        d.obj("user_settings")?.let { s ->
            if (prefs.getString("Status.$id", null) == null) s.str("status")?.let { status = it }
            s.obj("custom_status")?.let { customStatusText = it.str("text") }
        }

        applyPresences(d)
        muteSettings.clear()
        val ugs = d.opt("user_guild_settings")
        val ugsList = (ugs as? JSONObject)?.optJSONArray("entries") ?: (ugs as? JSONArray)
        if (ugsList != null) {
            for (i in 0 until ugsList.length()) ugsList.optJSONObject(i)?.let { applyGuildSettings(it) }
        }

        readyAt = SystemClock.elapsedRealtime()
        isReady = true
        signedOut = false
        gateway.updatePresence(buildPresence())
        onReady?.invoke(this)
        changed()
    }

    private fun applyMembers(merged: JSONArray?, guildList: List<JSONObject>) {
        merged ?: return
        val me = me ?: return
        for (i in 0 until minOf(merged.length(), guildList.size)) {
            val g = guilds[guildList[i].str("id") ?: ""] ?: continue
            val members = merged.optJSONArray(i) ?: continue
            for (j in 0 until members.length()) {
                val mj = members.optJSONObject(j) ?: continue
                val uid = mj.str("user_id") ?: mj.obj("user")?.str("id")
                if (uid == me.id) g.myRoles = mj.strs("roles").toSet()
            }
        }
    }

    private fun applyPresences(d: JSONObject) {
        for (p in d.objs("presences")) {
            val uid = p.obj("user")?.str("id") ?: p.str("user_id") ?: continue
            presences[uid] = Presence.from(p)
        }
        for (p in d.obj("merged_presences")?.objs("friends").orEmpty()) {
            val uid = p.str("user_id") ?: p.obj("user")?.str("id") ?: continue
            presences[uid] = Presence.from(p)
        }
    }

    private fun onMessage(m: JSONObject) {
        val author = m.obj("author")
        author?.let { rememberUser(it) }
        val cid = m.str("channel_id") ?: return
        val authorId = author?.str("id")
        val mine = authorId != null && authorId == me?.id
        var notify = false
        val c = findChannel(cid)
        if (c != null) {
            c.lastMessageId = m.str("id")
            if (mine) {
                m.str("id")?.let { readStates[cid] = it }
            } else if (me != null) {
                val counts = if (c.isPrivate) !isMuted(c) && !c.isRequest else shouldNotifyMention(m)
                if (counts) {
                    mentions[cid] = mentionCount(cid) + 1
                    notify = true
                }
            }
        } else if (m.str("guild_id") == null) {
            // A DM channel we haven't seen yet.
            val ch = Channel()
            ch.id = cid
            ch.type = 1
            ch.lastMessageId = m.str("id")
            author?.str("id")?.let { ch.recipientIds = listOf(it) }
            privateChannels[cid] = ch
            if (!mine) {
                mentions[cid] = 1
                notify = true
            }
        }
        events.tryEmit(ChatEvent.Created(cid, m))
        if (notify && status != "dnd" && isReady && SystemClock.elapsedRealtime() > readyAt) onNotify?.invoke(this, m)
        changed()
    }

    companion object {
        // The first part of a token is the account's user id in base64: a stable key that doesn't reveal the token.
        fun idFromToken(token: String): String {
            val first = token.substringBefore('.')
            for (flags in intArrayOf(Base64.DEFAULT, Base64.URL_SAFE)) {
                try {
                    val s = String(Base64.decode(first, flags))
                    if (s.isNotEmpty() && s.all { it.isDigit() }) return s
                } catch (e: IllegalArgumentException) {
                    // try the next alphabet
                }
            }
            return Integer.toHexString(token.hashCode())
        }
    }
}
