package com.marcturtal.buzzcord.net

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel as KChannel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.zip.Inflater
import kotlin.math.min
import kotlin.random.Random

// One live connection to Discord's gateway for one account. Reconnects and resumes by itself.
// Uses zlib-stream compression: READY for a big account shrinks from megabytes to a fraction,
// which matters on mobile data with several accounts signed in.
class Gateway(
    private val token: String,
    private val scope: CoroutineScope,
    private val presence: () -> JSONObject,
) {
    companion object {
        const val DEFAULT_URL = "wss://gateway.discord.gg"
        const val CAPABILITIES = 16   // DEDUPE_USER_OBJECTS
    }

    var onDispatch: ((String, JSONObject) -> Unit)? = null   // on the main thread
    var onState: ((String) -> Unit)? = null
    var onAuthFailed: (() -> Unit)? = null

    @Volatile var connected = false
        private set

    private var job: Job? = null
    @Volatile private var ws: WebSocket? = null
    @Volatile private var sessionId: String? = null
    @Volatile private var resumeUrl: String? = null
    @Volatile private var seq = -1L
    @Volatile private var acked = true
    @Volatile private var stopped = false
    private val kick = KChannel<Unit>(KChannel.CONFLATED)

    private sealed class Ev {
        class Msg(val p: JSONObject) : Ev()
        class Closed(val code: Int) : Ev()
    }

    fun start() {
        if (job?.isActive == true) return
        stopped = false
        job = scope.launch(Dispatchers.IO) { runLoop() }
    }

    fun stop() {
        stopped = true
        connected = false
        ws?.close(1000, null)
        job?.cancel()
        job = null
        sessionId = null
        seq = -1
        state("Paused")
    }

    // The network changed: drop a connection that may be tied to the old network and resume now.
    fun networkChanged() {
        if (stopped) return
        ws?.cancel()
        kick.trySend(Unit)
    }

    // Skip the current backoff wait, if there is one.
    fun retryNow() {
        if (!stopped && !connected) kick.trySend(Unit)
    }

    private suspend fun runLoop() {
        var failures = 0
        while (!stopped) {
            val resume = sessionId != null && seq >= 0
            state(if (resume) "Reconnecting..." else "Connecting...")
            val (code, gotHello) = try {
                connectOnce(resume)
            } catch (e: Exception) {
                if (stopped) break
                Log.w(TAG, "gateway: ${e.message}")
                0 to false
            }
            connected = false
            if (stopped) break
            failures = if (gotHello) 0 else failures + 1
            if (code == 4004) {
                state("Signed out (token no longer valid)")
                withContext(Dispatchers.Main) { onAuthFailed?.invoke() }
                return
            }
            // These codes mean the session can't be resumed.
            if (code == 4003 || code == 4007 || code == 4009) { sessionId = null; seq = -1 }
            state("Disconnected, retrying...")
            val wait = min(30_000L, 1000L shl min(failures, 5)) + Random.nextLong(500)
            withTimeoutOrNull(wait) { kick.receive() }
        }
    }

    private suspend fun connectOnce(resume: Boolean): Pair<Int, Boolean> = coroutineScope {
        val events = KChannel<Ev>(KChannel.UNLIMITED)
        val inflater = Inflater()
        val pending = ByteArrayOutputStream()
        val base = (if (resume) resumeUrl else null) ?: DEFAULT_URL
        val req = Request.Builder().url("$base/?encoding=json&v=9&compress=zlib-stream")
            .header("Origin", "https://discord.com")
            .header("User-Agent", ClientInfo.USER_AGENT)
            .build()

        // Runs on OkHttp's reader thread, so inflating and parsing never touch the main thread.
        val socket = Http.sockets.newWebSocket(req, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                pending.write(bytes.toByteArray())
                val data = pending.toByteArray()
                val n = data.size
                // A complete zlib-stream message ends with the sync-flush marker 00 00 FF FF.
                if (n < 4 || data[n - 4] != 0.toByte() || data[n - 3] != 0.toByte() ||
                    data[n - 2] != 0xFF.toByte() || data[n - 1] != 0xFF.toByte()
                ) return
                pending.reset()
                try {
                    inflater.setInput(data)
                    val out = ByteArrayOutputStream(n * 4)
                    val chunk = ByteArray(64 * 1024)
                    while (true) {
                        val got = inflater.inflate(chunk)
                        if (got == 0) break
                        out.write(chunk, 0, got)
                    }
                    (parseJson(out.toString("UTF-8")) as? JSONObject)?.let { events.trySend(Ev.Msg(it)) }
                } catch (e: Exception) {
                    Log.w(TAG, "gateway inflate: ${e.message}")
                    webSocket.cancel()
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                (parseJson(text) as? JSONObject)?.let { events.trySend(Ev.Msg(it)) }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                events.trySend(Ev.Closed(code))
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                events.trySend(Ev.Closed(code))
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (!stopped) Log.w(TAG, "gateway: ${t.message}")
                events.trySend(Ev.Closed(0))
            }
        })
        ws = socket

        var gotHello = false
        var closeCode = 0
        var heartbeat: Job? = null
        try {
            loop@ for (ev in events) {
                if (stopped) break
                when (ev) {
                    is Ev.Closed -> { closeCode = ev.code; break@loop }
                    is Ev.Msg -> {
                        val p = ev.p
                        when (p.long("op", -1).toInt()) {
                            10 -> { // hello
                                gotHello = true
                                val interval = p.obj("d")?.long("heartbeat_interval", 41250) ?: 41250L
                                acked = true
                                heartbeat?.cancel()
                                heartbeat = launch {
                                    delay((interval * Random.nextDouble()).toLong())
                                    while (isActive) {
                                        if (!acked) {
                                            // No answer to the last beat: the connection is dead. Drop it and resume.
                                            Log.w(TAG, "gateway: heartbeat not acknowledged, reconnecting")
                                            socket.cancel()
                                            events.trySend(Ev.Closed(0))
                                            return@launch
                                        }
                                        acked = false
                                        sendBeat(socket)
                                        delay(interval)
                                    }
                                }
                                if (resume) sendResume(socket) else identify(socket)
                            }
                            11 -> acked = true
                            1 -> sendBeat(socket)
                            7 -> { // asked to reconnect and resume; a 4000-range close keeps the session
                                socket.close(4000, "reconnect")
                                break@loop
                            }
                            9 -> { // invalid session
                                val canResume = p.optBoolean("d", false)
                                if (!canResume) { sessionId = null; seq = -1 }
                                delay(1000L + Random.nextLong(4000))
                                if (canResume) sendResume(socket) else identify(socket)
                            }
                            0 -> {
                                if (p.has("s") && !p.isNull("s")) seq = p.getLong("s")
                                val t = p.str("t") ?: ""
                                var d = p.optJSONObject("d")
                                // A few events (SESSIONS_REPLACE) carry a bare array; hand it over as d.list.
                                if (d == null) p.optJSONArray("d")?.let { d = JSONObject().put("list", it) }
                                if (t == "READY") {
                                    sessionId = d?.str("session_id")
                                    resumeUrl = d?.str("resume_gateway_url")
                                    connected = true
                                    state("Online")
                                } else if (t == "RESUMED") {
                                    connected = true
                                    state("Online")
                                }
                                val payload = d
                                if (payload != null) withContext(Dispatchers.Main) { onDispatch?.invoke(t, payload) }
                            }
                        }
                    }
                }
            }
        } finally {
            heartbeat?.cancel()
            socket.cancel()
            events.close()
            inflater.end()
            if (ws === socket) ws = null
        }
        closeCode to gotHello
    }

    private fun sendBeat(socket: WebSocket) {
        socket.send(jobj("op" to 1, "d" to (if (seq >= 0) seq else null)).toString())
    }

    private fun sendResume(socket: WebSocket) {
        socket.send(jobj("op" to 6, "d" to jobj("token" to token, "session_id" to (sessionId ?: ""), "seq" to seq)).toString())
    }

    private suspend fun identify(socket: WebSocket) {
        val p = withContext(Dispatchers.Main) { presence() }
        socket.send(jobj("op" to 2, "d" to jobj(
            "token" to token,
            "capabilities" to CAPABILITIES,
            "properties" to ClientInfo.properties(),
            "presence" to p,
            "compress" to false,
            "client_state" to jobj("guild_versions" to JSONObject()),
        )).toString())
    }

    fun send(payload: JSONObject) {
        ws?.send(payload.toString())
    }

    fun updatePresence(p: JSONObject) {
        if (connected) send(jobj("op" to 3, "d" to p))
    }

    private fun state(s: String) {
        scope.launch(Dispatchers.Main) { onState?.invoke(s) }
    }
}
