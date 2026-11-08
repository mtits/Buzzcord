package com.marcturtal.buzzcord.data

import android.app.Application
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.Network
import android.util.Log
import androidx.core.content.ContextCompat
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import com.marcturtal.buzzcord.GatewayService
import com.marcturtal.buzzcord.MainActivity
import com.marcturtal.buzzcord.Notifier
import com.marcturtal.buzzcord.R
import com.marcturtal.buzzcord.net.ClientInfo
import com.marcturtal.buzzcord.net.TAG
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

// Every signed-in account lives here, all connected at once in one lightweight process.
// Switching accounts is just changing which one the screen shows: no reconnect, no app clone.
object Accounts {
    const val EXTRA_ACCOUNT = "account"
    const val EXTRA_CHANNEL = "channel"
    const val EXTRA_LAST = "last"

    lateinit var app: Application
        private set
    lateinit var prefs: SharedPreferences
        private set
    private lateinit var store: AccountStore
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val _sessions = MutableStateFlow<List<Session>>(emptyList())
    val sessions: StateFlow<List<Session>> = _sessions
    val activeId = MutableStateFlow<String?>(null)

    // Which chat is on screen (account id to channel id), so it doesn't also pop a notification.
    var visibleChat: Pair<String, String>? = null
    var appVisible = false
    private var initialized = false

    var backgroundEnabled: Boolean
        get() = prefs.getBoolean("Background", true)
        set(v) { prefs.edit().putBoolean("Background", v).apply(); ensureService(app) }

    fun init(application: Application) {
        if (initialized) return
        initialized = true
        app = application
        prefs = application.getSharedPreferences("buzzcord", Context.MODE_PRIVATE)
        store = AccountStore(application)
        val saved = store.load()
        val list = saved.map { s ->
            makeSession(s.token).also {
                it.cachedName = s.name
                it.cachedAvatar = s.avatar
                it.paused = s.paused
                if (s.paused) it.connectionState.value = "Paused"
            }
        }
        _sessions.value = list
        activeId.value = prefs.getString("Active", null)?.takeIf { id -> list.any { it.id == id } } ?: list.firstOrNull()?.id
        scope.launch {
            // Refresh the client build number first (cached for a day), then connect every account.
            withTimeoutOrNull(5000) { ClientInfo.loadBuildNumber(prefs) }
            for (s in _sessions.value) if (!s.paused) s.start()
        }
        watchNetwork()
        updateShortcuts()
    }

    fun session(id: String?): Session? = _sessions.value.firstOrNull { it.id == id }

    val active: Session? get() = session(activeId.value)

    fun switchTo(id: String) {
        if (activeId.value == id || session(id) == null) return
        activeId.value = id
        prefs.edit().putString("Active", id).apply()
    }

    // Adds an account, or refreshes the token of one already signed in.
    fun addToken(token: String): Session {
        val id = Session.idFromToken(token)
        session(id)?.let { old ->
            if (old.token == token) {
                switchTo(id)
                if (old.paused) setPaused(old, false)
                return old
            }
            old.gateway.stop()
            _sessions.value = _sessions.value.filter { it.id != id }
        }
        val s = makeSession(token)
        _sessions.value = _sessions.value + s
        s.start()
        activeId.value = s.id
        prefs.edit().putString("Active", s.id).apply()
        persist()
        ensureService(app)
        return s
    }

    fun remove(s: Session) {
        s.gateway.stop()
        Notifier.cancelAccount(app, s.id)
        _sessions.value = _sessions.value.filter { it !== s }
        if (activeId.value == s.id) activeId.value = _sessions.value.firstOrNull()?.id
        prefs.edit().putString("Active", activeId.value).apply()
        persist()
        updateShortcuts()
        ensureService(app)
    }

    // A paused account stays signed in but holds no connection: zero data and battery until resumed.
    fun setPaused(s: Session, paused: Boolean) {
        if (paused) s.stop() else s.start()
        persist()
        ensureService(app)
        _sessions.value = _sessions.value.toList()
    }

    fun move(s: Session, delta: Int) {
        val list = _sessions.value.toMutableList()
        val i = list.indexOf(s)
        val j = i + delta
        if (i < 0 || j !in list.indices) return
        list.removeAt(i)
        list.add(j, s)
        _sessions.value = list
        persist()
        updateShortcuts()
    }

    val totalUnread: Int get() = _sessions.value.sumOf { it.badgeCount }

    private fun makeSession(token: String): Session {
        val s = Session(token, scope, prefs)
        s.onNotify = { sess, m -> Notifier.message(app, sess, m) }
        s.onFriendRequest = { sess, uid -> Notifier.friendRequest(app, sess, uid) }
        s.onReadElsewhere = { sess, cid -> Notifier.cancel(app, sess.id, cid) }
        s.onSignedOut = { sess -> Notifier.signedOut(app, sess) }
        s.onReady = { sess ->
            val changed = sess.cachedName != sess.displayName || sess.cachedAvatar != sess.avatarUrl
            sess.cachedName = sess.displayName
            sess.cachedAvatar = sess.avatarUrl
            if (changed) {
                persist()
                updateShortcuts()
            }
        }
        return s
    }

    private fun persist() {
        store.save(_sessions.value.map { AccountStore.Saved(it.id, it.token, it.cachedName, it.cachedAvatar, it.paused) })
    }

    // The foreground service keeps the process (and so every connection) alive in the background.
    fun ensureService(ctx: Context) {
        val want = backgroundEnabled && _sessions.value.any { !it.paused }
        val intent = Intent(ctx, GatewayService::class.java)
        try {
            if (want) ContextCompat.startForegroundService(ctx, intent) else ctx.stopService(intent)
        } catch (e: Exception) {
            // Starting from the background isn't allowed on newer Android; the activity retries on start.
            Log.w(TAG, "service: ${e.message}")
        }
    }

    // Long-press the launcher icon to jump straight into an account.
    private fun updateShortcuts() {
        try {
            val shortcuts = _sessions.value.take(4).map { s ->
                ShortcutInfoCompat.Builder(app, "acct_${s.id}")
                    .setShortLabel(s.displayName.take(24))
                    .setIcon(IconCompat.createWithResource(app, R.drawable.ic_shortcut))
                    .setIntent(Intent(app, MainActivity::class.java).setAction(Intent.ACTION_VIEW).putExtra(EXTRA_ACCOUNT, s.id))
                    .build()
            }
            ShortcutManagerCompat.setDynamicShortcuts(app, shortcuts)
        } catch (e: Exception) {
            Log.w(TAG, "shortcuts: ${e.message}")
        }
    }

    private fun watchNetwork() {
        val cm = app.getSystemService(ConnectivityManager::class.java) ?: return
        var current: Network? = null
        try {
            cm.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    val previous = current
                    current = network
                    if (previous != null && previous != network) {
                        scope.launch { for (s in _sessions.value) if (!s.paused) s.gateway.networkChanged() }
                    } else if (previous == null) {
                        // Coming back from no network: retry right away instead of waiting out the backoff.
                        scope.launch { for (s in _sessions.value) if (!s.paused) s.gateway.retryNow() }
                    }
                }

                override fun onLost(network: Network) {
                    if (current == network) current = null
                }
            })
        } catch (e: Exception) {
            Log.w(TAG, "network callback: ${e.message}")
        }
    }
}
