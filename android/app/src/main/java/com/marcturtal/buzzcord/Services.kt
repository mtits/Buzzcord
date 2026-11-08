package com.marcturtal.buzzcord

import android.app.Application
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.RemoteInput
import coil.ImageLoader
import coil.ImageLoaderFactory
import coil.decode.GifDecoder
import coil.decode.ImageDecoderDecoder
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.net.Http
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class BuzzApp : Application(), ImageLoaderFactory {
    override fun onCreate() {
        super.onCreate()
        Notifier.createChannels(this)
        Accounts.init(this)
    }

    // Pictures share the app's HTTP connection pool.
    override fun newImageLoader(): ImageLoader = ImageLoader.Builder(this)
        .okHttpClient(Http.client)
        .components { if (Build.VERSION.SDK_INT >= 28) add(ImageDecoderDecoder.Factory()) else add(GifDecoder.Factory()) }
        .crossfade(true)
        .build()
}

// Keeps the process, and so every account's connection, alive while the app is in the background.
// One service for all accounts: the cost of a second account is one websocket, not a second app.
class GatewayService : Service() {
    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val sessions = Accounts.sessions.value
        val n = Notifier.serviceNotification(this, sessions.count { !it.paused }, sessions.size)
        try {
            if (Build.VERSION.SDK_INT >= 34) startForeground(Notifier.SERVICE_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            else startForeground(Notifier.SERVICE_ID, n)
        } catch (e: Exception) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (job == null) {
            job = Accounts.scope.launch {
                Accounts.sessions.collectLatest { list ->
                    if (list.none { !it.paused } || !Accounts.backgroundEnabled) {
                        stopSelf()
                        return@collectLatest
                    }
                    val nm = getSystemService(android.app.NotificationManager::class.java)
                    nm.notify(Notifier.SERVICE_ID, Notifier.serviceNotification(this@GatewayService, list.count { !it.paused }, list.size))
                }
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        job?.cancel()
        job = null
        super.onDestroy()
    }
}

// Reply and Mark as Read from a message notification.
class ActionReceiver : BroadcastReceiver() {
    companion object {
        const val REPLY = "com.marcturtal.buzzcord.REPLY"
        const val READ = "com.marcturtal.buzzcord.READ"
    }

    override fun onReceive(ctx: Context, intent: Intent) {
        val acc = intent.getStringExtra(Accounts.EXTRA_ACCOUNT) ?: return
        val cid = intent.getStringExtra(Accounts.EXTRA_CHANNEL) ?: return
        val last = intent.getStringExtra(Accounts.EXTRA_LAST)
        val s = Accounts.session(acc) ?: return
        when (intent.action) {
            REPLY -> {
                val text = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(Notifier.KEY_REPLY)?.toString()?.trim().orEmpty()
                if (text.isEmpty()) return
                val pending = goAsync()
                Accounts.scope.launch {
                    try {
                        s.api.sendMessage(cid, text, null, null)
                        if (last != null) s.markRead(cid, last)
                        Notifier.replied(ctx, s, cid, text)
                    } catch (e: Exception) {
                        Notifier.replyFailed(ctx, s, cid, text, e.message)
                    } finally {
                        pending.finish()
                    }
                }
            }
            READ -> {
                if (last != null) s.markRead(cid, last)
                Notifier.cancel(ctx, acc, cid)
            }
        }
    }
}
