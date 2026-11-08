package com.marcturtal.buzzcord.net

import android.util.Base64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.spec.MGF1ParameterSpec
import javax.crypto.Cipher
import javax.crypto.spec.OAEPParameterSpec
import javax.crypto.spec.PSource

// "Log in with QR code", the same flow the Discord web app uses. Another device's Discord app
// scans a QR code holding a fingerprint of a key made here; Discord then sends the token back
// encrypted with that key, so it never travels in the clear.
class RemoteAuth(private val scope: CoroutineScope) {
    var onQr: ((String) -> Unit)? = null                 // URL to put in the QR code
    var onScanned: ((String, String) -> Unit)? = null    // username, avatar URL
    var onSuccess: ((String) -> Unit)? = null            // token
    var onFailed: ((String) -> Unit)? = null

    private val keyPair: KeyPair = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
    private var ws: WebSocket? = null
    private var heartbeat: Job? = null
    private var finished = false

    fun start() {
        val req = Request.Builder().url("wss://remote-auth-gateway.discord.gg/?v=2")
            .header("Origin", "https://discord.com")
            .header("User-Agent", ClientInfo.USER_AGENT)
            .build()
        ws = Http.sockets.newWebSocket(req, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                val p = parseJson(text) as? JSONObject ?: return
                scope.launch(Dispatchers.Main) {
                    try {
                        handle(p)
                    } catch (e: Exception) {
                        fail(e.message ?: "Something went wrong.")
                    }
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                scope.launch(Dispatchers.Main) { fail("The QR code expired. Tap New code to get another.") }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                scope.launch(Dispatchers.Main) { fail("Couldn't reach Discord: ${t.message}") }
            }
        })
    }

    fun cancel() {
        finished = true
        heartbeat?.cancel()
        ws?.close(1000, null)
    }

    private fun fail(msg: String) {
        if (finished) return
        finished = true
        heartbeat?.cancel()
        onFailed?.invoke(msg)
    }

    private fun succeed(token: String) {
        if (finished) return
        finished = true
        heartbeat?.cancel()
        ws?.close(1000, null)
        onSuccess?.invoke(token)
    }

    private suspend fun handle(p: JSONObject) {
        if (finished) return
        when (p.str("op")) {
            "hello" -> {
                val interval = p.long("heartbeat_interval", 41250)
                heartbeat = scope.launch {
                    while (isActive) {
                        delay(interval)
                        ws?.send(jobj("op" to "heartbeat").toString())
                    }
                }
                val pub = Base64.encodeToString(keyPair.public.encoded, Base64.NO_WRAP) // X.509 SubjectPublicKeyInfo
                ws?.send(jobj("op" to "init", "encoded_public_key" to pub).toString())
            }
            "nonce_proof" -> {
                val nonce = decrypt(p.str("encrypted_nonce") ?: "")
                val hash = MessageDigest.getInstance("SHA-256").digest(nonce)
                val proof = Base64.encodeToString(hash, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
                ws?.send(jobj("op" to "nonce_proof", "proof" to proof).toString())
            }
            "pending_remote_init" -> onQr?.invoke("https://discord.com/ra/" + (p.str("fingerprint") ?: ""))
            "pending_ticket" -> {
                // "id:discriminator:avatar:username"
                val parts = String(decrypt(p.str("encrypted_user_payload") ?: "")).split(":", limit = 4)
                if (parts.size == 4) {
                    val u = User()
                    u.id = parts[0]
                    u.discriminator = parts[1]
                    u.avatar = if (parts[2] == "0") null else parts[2]
                    u.username = parts[3]
                    onScanned?.invoke(u.username ?: "", u.avatarUrl(128))
                }
            }
            "pending_login" -> succeed(withContext(Dispatchers.IO) { exchangeTicket(p.str("ticket") ?: "") })
            "finish" -> succeed(String(decrypt(p.str("encrypted_token") ?: "")))   // older protocol variant
            "cancel" -> fail("Login was cancelled on the phone.")
        }
    }

    private fun exchangeTicket(ticket: String): String {
        val req = Request.Builder().url(Api.BASE + "/users/@me/remote-auth/login")
            .header("User-Agent", ClientInfo.USER_AGENT)
            .header("X-Super-Properties", ClientInfo.superProperties())
            .header("Origin", "https://discord.com")
            .header("Referer", "https://discord.com/login")
            .post(jobj("ticket" to ticket).toString().toRequestBody("application/json".toMediaType()))
            .build()
        val (code, text) = Http.client.newCall(req).execute().use { it.code to (it.body?.string() ?: "") }
        val j = parseJson(text) as? JSONObject
        val enc = j?.str("encrypted_token")
        if (code !in 200..299 || enc == null) throw ApiException(code, j, text)
        return String(decrypt(enc))
    }

    private fun decrypt(b64: String): ByteArray {
        val c = Cipher.getInstance("RSA/ECB/OAEPPadding")
        // Discord uses SHA-256 for both the OAEP digest and MGF1 (Android defaults MGF1 to SHA-1).
        c.init(Cipher.DECRYPT_MODE, keyPair.private, OAEPParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.SHA256, PSource.PSpecified.DEFAULT))
        return c.doFinal(Base64.decode(b64, Base64.DEFAULT))
    }
}
