@file:OptIn(ExperimentalMaterial3Api::class)

package com.marcturtal.buzzcord.ui

import android.annotation.SuppressLint
import android.webkit.JavascriptInterface
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.data.Session
import com.marcturtal.buzzcord.net.str
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import org.json.JSONObject
import kotlin.coroutines.resume

@Composable
fun App() {
    val sessions by Accounts.sessions.collectAsState()
    val activeId by Accounts.activeId.collectAsState()
    val top = Nav.stack.last()
    BackHandler(enabled = Nav.stack.size > 1) { Nav.pop() }

    if (sessions.isEmpty()) {
        SignInScreen(canGoBack = false)
    } else {
        when (top) {
            is Screen.Home -> HomeScreen()
            is Screen.SignIn -> SignInScreen(canGoBack = true)
            is Screen.Unread -> UnreadScreen()
            is Screen.Chat -> {
                val s = Accounts.session(top.account)
                if (s == null) LaunchedEffect(top) { Nav.pop() } else ChatScreen(s, top.channel)
            }
        }
    }
    // Keep the active account in step with the chat on screen (e.g. opened from a notification).
    LaunchedEffect(top, activeId) {
        if (top is Screen.Chat && activeId != top.account) Accounts.switchTo(top.account)
    }

    FirstRunNotice()
    CaptchaHost.Show()
}

@Composable
private fun FirstRunNotice() {
    var show by remember { mutableStateOf(!Accounts.prefs.getBoolean("NoticeShown", false)) }
    if (!show) return
    AlertDialog(
        onDismissRequest = {},
        title = { Text("Before you start") },
        text = {
            Text(
                "Buzzcord is an unofficial Discord client. It isn't made, endorsed or supported by Discord Inc.\n\n" +
                    "Discord's Terms of Service don't allow third-party clients on user accounts. Buzzcord behaves like Discord's own web app and never automates anything, but Discord could still limit or ban accounts that use it.\n\n" +
                    "Use it at your own risk.",
            )
        },
        confirmButton = {
            TextButton(onClick = {
                Accounts.prefs.edit().putBoolean("NoticeShown", true).apply()
                show = false
            }) { Text("I understand") }
        },
    )
}

@Composable
private fun UnreadScreen() {
    val sessions by Accounts.sessions.collectAsState()
    val revs = sessions.map { it.revision.collectAsState().value }
    val entries = remember(revs) { sessions.flatMap { s -> s.unreadEntries().map { (cid, n) -> Triple(s, cid, n) } } }
    Scaffold(
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(containerColor = BrandPurple, titleContentColor = Color.White, navigationIconContentColor = Color.White, actionIconContentColor = Color.White),
                navigationIcon = { IconButton(onClick = { Nav.pop() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } },
                title = { Text("Unread on every account") },
                actions = {
                    if (entries.isNotEmpty()) TextButton(onClick = {
                        for ((s, cid, _) in entries) s.findChannel(cid)?.let { s.markRead(it) }
                    }) { Text("Mark all read", color = BrandYellow) }
                },
            )
        },
    ) { pad ->
        if (entries.isEmpty()) {
            Text("Nothing unread anywhere.", Modifier.padding(pad).padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            return@Scaffold
        }
        LazyColumn(Modifier.fillMaxSize().padding(pad)) {
            items(entries, key = { it.first.id + "/" + it.second }) { (s, cid, n) -> UnreadRow(s, cid, n) }
        }
    }
}

@Composable
private fun UnreadRow(s: Session, cid: String, count: Int) {
    val c = s.findChannel(cid)
    Row(
        Modifier.fillMaxWidth().clickable { Accounts.switchTo(s.id); Nav.push(Screen.Chat(s.id, cid)) }.padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Avatar(s.chatAvatar(c), 40.dp, fallback = s.title(c))
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text(s.title(c), fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Avatar(s.avatarUrl, 16.dp, fallback = s.displayName)
                Text(
                    " " + s.displayName + (s.guildOf(c)?.let { " · ${it.name}" } ?: ""),
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Badge(containerColor = MaterialTheme.colorScheme.error) { Text(count.toString()) }
    }
}

class CaptchaBridge(private val onMessage: (String) -> Unit) {
    @JavascriptInterface
    fun message(msg: String) = onMessage(msg)
}

// Shows Discord's hCaptcha when an API call asks for one, then hands the answer back to Api.
object CaptchaHost {
    private class Pending(val challenge: JSONObject, val cont: CancellableContinuation<String?>)

    private val pending = MutableStateFlow<Pending?>(null)
    var uiAttached = 0

    suspend fun solve(challenge: JSONObject): String? {
        if (uiAttached <= 0) return null
        if ((challenge.str("captcha_service") ?: "hcaptcha") != "hcaptcha") return null
        return suspendCancellableCoroutine { cont ->
            pending.value?.cont?.let { if (it.isActive) it.resume(null) }
            pending.value = Pending(challenge, cont)
            cont.invokeOnCancellation { if (pending.value?.cont === cont) pending.value = null }
        }
    }

    private fun finish(answer: String?) {
        val p = pending.value ?: return
        pending.value = null
        if (p.cont.isActive) p.cont.resume(answer)
    }

    @SuppressLint("SetJavaScriptEnabled")
    @Composable
    fun Show() {
        val p by pending.collectAsState()
        val current = p ?: return
        val dark = isSystemInDarkTheme()
        var note by remember(current) { mutableStateOf("Discord wants to check you're human.") }
        Dialog(onDismissRequest = { finish(null) }) {
            androidx.compose.material3.Surface(shape = MaterialTheme.shapes.large) {
                Column(Modifier.padding(12.dp)) {
                    Text(note, fontSize = 13.sp, modifier = Modifier.padding(bottom = 8.dp))
                    AndroidView(
                        modifier = Modifier.fillMaxWidth().height(520.dp),
                        factory = { ctx ->
                            val wv = WebView(ctx)
                            wv.settings.javaScriptEnabled = true
                            wv.settings.domStorageEnabled = true
                            wv.webViewClient = WebViewClient()
                            wv.addJavascriptInterface(CaptchaBridge { msg ->
                                wv.post {
                                    if (msg.startsWith("error:")) note = "The captcha hit a problem (${msg.substring(6)}). Close this and try again."
                                    else finish(msg)
                                }
                            }, "Bridge")
                            // Served as discord.com so the site key matches, like the desktop versions do.
                            wv.loadDataWithBaseURL("https://discord.com/captcha", page(current.challenge, dark), "text/html", "utf-8", null)
                            wv
                        },
                        onRelease = { it.destroy() },
                    )
                    TextButton(onClick = { finish(null) }, modifier = Modifier.align(Alignment.End)) { Text("Cancel") }
                }
            }
        }
    }

    private fun page(ch: JSONObject, dark: Boolean): String {
        fun js(v: String?) = JSONObject.quote(v ?: "").replace("</", "<\\/")
        return """<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<style>html,body{margin:0;height:100%;background:${if (dark) "#17141f" else "#fff"};}body{display:flex;align-items:center;justify-content:center;}</style>
<script>
function send(m){Bridge.message(m);}
function go(){
  var id=hcaptcha.render('box',{sitekey:${js(ch.str("captcha_sitekey"))},
    callback:function(t){send(t);},
    'error-callback':function(e){send('error:'+e);},
    'expired-callback':function(){hcaptcha.reset(id);}, theme:${if (dark) "'dark'" else "'light'"}});
  var rq=${js(ch.str("captcha_rqdata"))};
  if(rq) hcaptcha.setData(id,{rqdata:rq});
}
</script>
<script src="https://js.hcaptcha.com/1/api.js?render=explicit&onload=go&recaptchacompat=off" async defer></script>
</head><body><div id="box"></div></body></html>"""
    }
}
