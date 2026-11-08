package com.marcturtal.buzzcord.net

import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSink
import okio.source
import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.TimeUnit

const val TAG = "Buzzcord"

// One HTTP client for the whole app: every account shares its connection pool and threads.
object Http {
    val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS)
        .readTimeout(100, TimeUnit.SECONDS)
        .writeTimeout(100, TimeUnit.SECONDS)
        .build()

    // Websockets: no read timeout, the gateway's own heartbeat finds dead connections.
    val sockets: OkHttpClient by lazy { client.newBuilder().readTimeout(0, TimeUnit.MILLISECONDS).build() }

    val io = CoroutineScope(SupervisorJob() + Dispatchers.IO)
}

class ApiException(val status: Int, val body: JSONObject?, raw: String?) : Exception(describe(status, body, raw)) {
    val isCaptcha: Boolean get() = body?.has("captcha_sitekey") == true

    companion object {
        fun describe(status: Int, body: JSONObject?, raw: String?): String {
            if (body?.has("captcha_sitekey") == true) return "Discord asked for a captcha and it wasn't solved, so nothing was sent. Try again to get a new one."
            if (status == 413) return "That file is too big for Discord (10 MB without Nitro)."
            var msg = body?.str("message") ?: ""
            if (msg.isEmpty()) msg = if ((raw?.length ?: 999) < 200) raw ?: "request failed" else "request failed"
            return "Discord said: $msg ($status)"
        }
    }
}

// Identity of this client. Same as the Windows build: the Discord web app in Chrome on Windows,
// the most common and best-tested fingerprint.
object ClientInfo {
    const val USER_AGENT =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"

    @Volatile var build = 657850

    fun properties(): JSONObject = jobj(
        "os" to "Windows",
        "browser" to "Chrome",
        "device" to "",
        "system_locale" to Locale.getDefault().toLanguageTag().ifEmpty { "en-US" },
        "browser_user_agent" to USER_AGENT,
        "browser_version" to "140.0.0.0",
        "os_version" to "10",
        "referrer" to "",
        "referring_domain" to "",
        "referrer_current" to "",
        "referring_domain_current" to "",
        "release_channel" to "stable",
        "client_build_number" to build,
        "client_event_source" to null,
    )

    fun superProperties(): String = Base64.encodeToString(properties().toString().toByteArray(), Base64.NO_WRAP)

    // The web app's build number changes every few days; read it from the web app's own files
    // at most once a day and keep the last known value.
    suspend fun loadBuildNumber(prefs: SharedPreferences) = withContext<Unit>(Dispatchers.IO) {
        val saved = prefs.getInt("BuildNumber", 0)
        if (saved > 0) build = saved
        if (System.currentTimeMillis() - prefs.getLong("BuildNumberChecked", 0) < 20 * 3600_000L) return@withContext
        try {
            val html = getString("https://discord.com/app")
            val scripts = Regex("src=\"(/assets/sentry\\.[^\"]+\\.js)\"").findAll(html).map { it.groupValues[1] }
            for (path in scripts) {
                val js = getString("https://discord.com$path")
                val n = Regex("\"buildNumber\",\"(\\d+)\"").find(js)?.groupValues?.get(1)?.toIntOrNull() ?: continue
                build = n
                prefs.edit().putInt("BuildNumber", n).apply()
                break
            }
            prefs.edit().putLong("BuildNumberChecked", System.currentTimeMillis()).apply()
        } catch (e: Exception) {
            Log.w(TAG, "build number: ${e.message}")
        }
    }

    fun getString(url: String): String {
        val req = Request.Builder().url(url).header("User-Agent", USER_AGENT).header("Accept-Language", "en-US,en;q=0.9").build()
        Http.client.newCall(req).execute().use { return it.body?.string() ?: "" }
    }
}

class Api(val token: String) {
    companion object {
        const val BASE = "https://discord.com/api/v9"
        private val JSON_TYPE: MediaType = "application/json".toMediaType()

        // Set by the UI: shows Discord's captcha and returns the solved key (null if closed or no UI).
        @Volatile var solveCaptcha: (suspend (JSONObject) -> String?)? = null

        fun nonce(): String = Snowflake.now()
    }

    suspend fun get(path: String): Any? = send("GET", path, null)
    suspend fun post(path: String, body: Any?): Any? = send("POST", path, body)
    suspend fun patch(path: String, body: Any?): Any? = send("PATCH", path, body)
    suspend fun put(path: String, body: Any?): Any? = send("PUT", path, body)
    suspend fun delete(path: String): Any? = send("DELETE", path, null)

    // Friend actions carry X-Context-Properties: where in the app they came from.
    suspend fun send(method: String, path: String, body: Any?, location: String): Any? {
        val ctx = Base64.encodeToString(jobj("location" to location).toString().toByteArray(), Base64.NO_WRAP)
        return send(method, path, body, mapOf("X-Context-Properties" to ctx))
    }

    fun fire(method: String, path: String, body: Any?) {
        Http.io.launch {
            try { send(method, path, body) } catch (e: Exception) { Log.w(TAG, "$method $path: ${e.message}") }
        }
    }

    suspend fun send(method: String, path: String, body: Any?, extra: Map<String, String> = emptyMap()): Any? = withContext<Any?>(Dispatchers.IO) {
        val headers = extra.toMutableMap()
        var captchaTried = false
        var attempt = 0
        while (true) {
            val rb = Request.Builder().url(if (path.startsWith("http")) path else BASE + path)
                .header("User-Agent", ClientInfo.USER_AGENT)
                .header("Accept-Language", "en-US,en;q=0.9")
                .header("Authorization", token)
                .header("X-Super-Properties", ClientInfo.superProperties())
                .header("X-Discord-Locale", "en-US")
                .header("X-Discord-Timezone", TimeZone.getDefault().id)
                .header("Origin", "https://discord.com")
                .header("Referer", "https://discord.com/channels/@me")
            for ((k, v) in headers) rb.header(k, v)
            val reqBody: RequestBody? = when {
                body != null -> body.toString().toRequestBody(JSON_TYPE)
                method == "POST" || method == "PUT" || method == "PATCH" -> "".toRequestBody(JSON_TYPE)
                else -> null
            }
            rb.method(method, reqBody)
            val (code, text) = Http.client.newCall(rb.build()).execute().use { it.code to (it.body?.string() ?: "") }
            if (code == 429 && attempt < 3) {
                attempt++
                val wait = (parseJson(text) as? JSONObject)?.optDouble("retry_after", 1.0) ?: 1.0
                delay((wait.coerceIn(0.5, 30.0) * 1000).toLong())
                continue
            }
            if (code in 200..299) return@withContext parseJson(text)
            val err = parseJson(text) as? JSONObject
            val solve = solveCaptcha
            if (code == 400 && err != null && err.has("captcha_sitekey") && !captchaTried && solve != null) {
                // Show Discord's captcha, then send the same request again with the answer, as the official app does.
                captchaTried = true
                val answer = withContext(Dispatchers.Main) { solve(err) }
                if (answer != null) {
                    headers["X-Captcha-Key"] = answer
                    err.str("captcha_rqtoken")?.let { headers["X-Captcha-Rqtoken"] = it }
                    err.str("captcha_session_id")?.let { headers["X-Captcha-Session-Id"] = it }
                    continue
                }
            }
            throw ApiException(code, err, text)
        }
        return@withContext null
    }

    suspend fun sendMessage(channelId: String, content: String, replyTo: String?, attachments: JSONArray?, nonce: String = nonce()): Any? {
        val body = jobj("content" to content, "nonce" to nonce, "tts" to false, "flags" to 0)
        if (replyTo != null) body.put("message_reference", jobj("channel_id" to channelId, "message_id" to replyTo))
        if (attachments != null && attachments.length() > 0) body.put("attachments", attachments)
        return post("/channels/$channelId/messages", body)
    }

    // Discord's upload flow: ask for an upload slot, PUT the bytes there, then send a message naming it.
    suspend fun uploadAttachment(ctx: Context, channelId: String, uri: Uri, index: Int): JSONObject {
        val (name, size) = withContext(Dispatchers.IO) { fileInfo(ctx, uri) }
        val slot = post("/channels/$channelId/attachments", jobj("files" to jarr(
            jobj("filename" to name, "file_size" to size, "id" to index.toString(), "is_clip" to false),
        ))) as? JSONObject
        val att = slot?.objs("attachments")?.firstOrNull() ?: throw ApiException(0, null, "Discord did not give an upload slot.")
        val url = att.str("upload_url") ?: throw ApiException(0, null, "Discord did not give an upload slot.")
        withContext(Dispatchers.IO) {
            val body = object : RequestBody() {
                override fun contentType(): MediaType = "application/octet-stream".toMediaType()
                override fun contentLength(): Long = if (size > 0) size else -1L
                override fun writeTo(sink: BufferedSink) {
                    ctx.contentResolver.openInputStream(uri)?.source()?.use { sink.writeAll(it) }
                }
            }
            val req = Request.Builder().url(url).put(body).header("User-Agent", ClientInfo.USER_AGENT).build()
            val code = Http.client.newBuilder().writeTimeout(30, TimeUnit.MINUTES).build().newCall(req).execute().use { it.code }
            if (code !in 200..299) throw ApiException(code, null, "Upload failed")
        }
        return jobj("id" to index.toString(), "filename" to name, "uploaded_filename" to (att.str("upload_filename") ?: ""))
    }

    private fun fileInfo(ctx: Context, uri: Uri): Pair<String, Long> {
        var name = "file"
        var size = -1L
        ctx.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                val n = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                val s = c.getColumnIndex(OpenableColumns.SIZE)
                if (n >= 0 && !c.isNull(n)) name = c.getString(n)
                if (s >= 0 && !c.isNull(s)) size = c.getLong(s)
            }
        }
        return name to size
    }
}
