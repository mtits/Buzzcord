package com.marcturtal.buzzcord

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.Person
import androidx.core.app.RemoteInput
import androidx.core.content.ContextCompat
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.data.Format
import com.marcturtal.buzzcord.data.Session
import com.marcturtal.buzzcord.net.Snowflake
import com.marcturtal.buzzcord.net.obj
import com.marcturtal.buzzcord.net.str
import org.json.JSONObject

// Per-account notifications: each chat is one conversation notification, tagged with the account
// it arrived on, with Reply (sends as that account) and Mark as Read right in the shade.
object Notifier {
    const val CH_MESSAGES = "messages"
    const val CH_FRIENDS = "friends"
    const val CH_SERVICE = "service"
    const val SERVICE_ID = 1
    const val KEY_REPLY = "reply"
    private const val ME = "\u0000me"
    private const val BRAND = 0xFF5B2A9E.toInt()

    private class Line(val text: String, val time: Long, val sender: String?, val senderKey: String?)

    private val history = HashMap<String, ArrayDeque<Line>>()
    private val lastMessage = HashMap<String, String>()

    fun createChannels(ctx: Context) {
        if (Build.VERSION.SDK_INT < 26) return
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH_MESSAGES, "Messages", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "Direct messages, group chats and mentions on every account"
        })
        nm.createNotificationChannel(NotificationChannel(CH_FRIENDS, "Friends and accounts", NotificationManager.IMPORTANCE_DEFAULT).apply {
            description = "Friend requests and sign-in problems"
        })
        nm.createNotificationChannel(NotificationChannel(CH_SERVICE, "Staying connected", NotificationManager.IMPORTANCE_MIN).apply {
            description = "The quiet notice Android needs while Buzzcord stays connected in the background"
            setShowBadge(false)
        })
    }

    private fun allowed(ctx: Context): Boolean =
        Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun key(accountId: String, channelId: String) = "$accountId/$channelId"
    private fun idFor(key: String) = key.hashCode() and 0x7FFFFFFF or 0x100

    fun message(ctx: Context, s: Session, m: JSONObject) {
        val cid = m.str("channel_id") ?: return
        if (Accounts.appVisible && Accounts.visibleChat == (s.id to cid)) return
        val authorId = m.obj("author")?.str("id")
        var text = Format.plain(s, m)
        if (text.isEmpty()) return
        if (text == Format.BUZZ) {
            text = "BUZZ!"
            buzz(ctx)
        }
        val k = key(s.id, cid)
        val lines = history.getOrPut(k) { ArrayDeque() }
        lines.addLast(Line(text, Snowflake.time(m.str("id")).takeIf { it > 0 } ?: System.currentTimeMillis(), s.name(authorId), authorId))
        while (lines.size > 8) lines.removeFirst()
        m.str("id")?.let { lastMessage[k] = it }
        post(ctx, s, cid, silent = false)
    }

    // After replying from the shade: show what was sent (and stop the reply spinner).
    fun replied(ctx: Context, s: Session, channelId: String, text: String) {
        val k = key(s.id, channelId)
        val lines = history.getOrPut(k) { ArrayDeque() }
        lines.addLast(Line(text, System.currentTimeMillis(), null, ME))
        while (lines.size > 8) lines.removeFirst()
        post(ctx, s, channelId, silent = true)
    }

    fun replyFailed(ctx: Context, s: Session, channelId: String, text: String, reason: String?) {
        val k = key(s.id, channelId)
        val lines = history.getOrPut(k) { ArrayDeque() }
        lines.addLast(Line("Not sent: \"$text\" (${reason ?: "error"})", System.currentTimeMillis(), "Buzzcord", "buzzcord"))
        post(ctx, s, channelId, silent = false)
    }

    private fun post(ctx: Context, s: Session, cid: String, silent: Boolean) {
        if (!allowed(ctx)) return
        val k = key(s.id, cid)
        val lines = history[k] ?: return
        val c = s.findChannel(cid)
        val mePerson = Person.Builder().setName("You (${s.displayName})").setKey(s.id).build()
        val style = NotificationCompat.MessagingStyle(mePerson)
        val isGroup = c == null || !c.isDm
        style.setGroupConversation(isGroup)
        if (isGroup) style.setConversationTitle(s.title(c) + (s.guildOf(c)?.let { " · ${it.name}" } ?: ""))
        for (l in lines) {
            val sender = if (l.senderKey == ME) null else Person.Builder().setName(l.sender ?: "?").setKey(l.senderKey).build()
            style.addMessage(l.text, l.time, sender)
        }

        val open = Intent(ctx, MainActivity::class.java)
            .setAction("open/$k")
            .putExtra(Accounts.EXTRA_ACCOUNT, s.id)
            .putExtra(Accounts.EXTRA_CHANNEL, cid)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        val openPi = PendingIntent.getActivity(ctx, idFor(k), open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)

        fun action(name: String) = Intent(ctx, ActionReceiver::class.java)
            .setAction(name)
            .setData(android.net.Uri.parse("buzzcord://$name/$k"))
            .putExtra(Accounts.EXTRA_ACCOUNT, s.id)
            .putExtra(Accounts.EXTRA_CHANNEL, cid)
            .putExtra(Accounts.EXTRA_LAST, lastMessage[k])

        val mutable = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
        val replyPi = PendingIntent.getBroadcast(ctx, idFor(k), action(ActionReceiver.REPLY), PendingIntent.FLAG_UPDATE_CURRENT or mutable)
        val readPi = PendingIntent.getBroadcast(ctx, idFor(k), action(ActionReceiver.READ), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val remote = RemoteInput.Builder(KEY_REPLY).setLabel("Reply as ${s.displayName}").build()
        val reply = NotificationCompat.Action.Builder(R.drawable.ic_stat_buzz, "Reply", replyPi)
            .addRemoteInput(remote)
            .setSemanticAction(NotificationCompat.Action.SEMANTIC_ACTION_REPLY)
            .setShowsUserInterface(false)
            .build()
        val read = NotificationCompat.Action.Builder(R.drawable.ic_stat_buzz, "Mark as read", readPi)
            .setSemanticAction(NotificationCompat.Action.SEMANTIC_ACTION_MARK_AS_READ)
            .setShowsUserInterface(false)
            .build()

        val n = NotificationCompat.Builder(ctx, CH_MESSAGES)
            .setSmallIcon(R.drawable.ic_stat_buzz)
            .setColor(BRAND)
            .setStyle(style)
            .setSubText(s.displayName)   // which account this arrived on
            .setContentIntent(openPi)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setGroup("acct_${s.id}")
            .setOnlyAlertOnce(silent)
            .setSilent(silent)
            .addAction(reply)
            .addAction(read)
            .build()
        val nm = NotificationManagerCompat.from(ctx)
        try {
            nm.notify(idFor(k), n)
            // Summary so each account's chats bundle together.
            val summary = NotificationCompat.Builder(ctx, CH_MESSAGES)
                .setSmallIcon(R.drawable.ic_stat_buzz)
                .setColor(BRAND)
                .setSubText(s.displayName)
                .setGroup("acct_${s.id}")
                .setGroupSummary(true)
                .setAutoCancel(true)
                .setSilent(true)
                .build()
            nm.notify(idFor("summary/${s.id}"), summary)
        } catch (e: SecurityException) {
            // permission revoked meanwhile
        }
    }

    fun cancel(ctx: Context, accountId: String, channelId: String) {
        val k = key(accountId, channelId)
        if (history.remove(k) == null) return
        lastMessage.remove(k)
        NotificationManagerCompat.from(ctx).cancel(idFor(k))
        if (history.keys.none { it.startsWith("$accountId/") }) NotificationManagerCompat.from(ctx).cancel(idFor("summary/$accountId"))
    }

    fun cancelAccount(ctx: Context, accountId: String) {
        for (k in history.keys.filter { it.startsWith("$accountId/") }) {
            history.remove(k)
            NotificationManagerCompat.from(ctx).cancel(idFor(k))
        }
        NotificationManagerCompat.from(ctx).cancel(idFor("summary/$accountId"))
    }

    fun friendRequest(ctx: Context, s: Session, userId: String) {
        simple(ctx, "friend/${s.id}/$userId", "Friend request", "${s.name(userId)} wants to be friends with ${s.displayName}", s)
    }

    fun signedOut(ctx: Context, s: Session) {
        simple(ctx, "signedout/${s.id}", "${s.displayName} was signed out", "Discord no longer accepts this sign-in. Open Buzzcord to sign in again.", s)
    }

    private fun simple(ctx: Context, key: String, title: String, text: String, s: Session) {
        if (!allowed(ctx)) return
        val open = Intent(ctx, MainActivity::class.java).setAction("open/$key").putExtra(Accounts.EXTRA_ACCOUNT, s.id)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        val n = NotificationCompat.Builder(ctx, CH_FRIENDS)
            .setSmallIcon(R.drawable.ic_stat_buzz)
            .setColor(BRAND)
            .setContentTitle(title)
            .setContentText(text)
            .setSubText(s.displayName)
            .setAutoCancel(true)
            .setContentIntent(PendingIntent.getActivity(ctx, idFor(key), open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .build()
        try { NotificationManagerCompat.from(ctx).notify(idFor(key), n) } catch (e: SecurityException) { }
    }

    fun buzz(ctx: Context) {
        val v = ctx.getSystemService(Vibrator::class.java) ?: return
        if (Build.VERSION.SDK_INT >= 26) v.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 120, 60, 120, 60, 120, 60, 200), -1))
    }

    fun serviceNotification(ctx: Context, online: Int, total: Int): android.app.Notification {
        val open = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val text = if (online == total) "$total account${if (total == 1) "" else "s"} connected" else "$online of $total accounts connected"
        return NotificationCompat.Builder(ctx, CH_SERVICE)
            .setSmallIcon(R.drawable.ic_stat_buzz)
            .setColor(BRAND)
            .setContentTitle(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()
    }
}
