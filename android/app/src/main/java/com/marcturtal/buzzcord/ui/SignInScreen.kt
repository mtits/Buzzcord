@file:OptIn(ExperimentalMaterial3Api::class)

package com.marcturtal.buzzcord.ui

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.webkit.CookieManager
import android.webkit.WebStorage
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.net.Api
import com.marcturtal.buzzcord.net.ClientInfo
import com.marcturtal.buzzcord.net.RemoteAuth
import com.marcturtal.buzzcord.net.parseJson
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun SignInScreen(canGoBack: Boolean) {
    var tab by remember { mutableIntStateOf(0) }
    fun done(token: String) {
        Accounts.addToken(token)
        Nav.home()
    }
    Scaffold(
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(containerColor = BrandPurple, titleContentColor = Color.White, navigationIconContentColor = Color.White),
                title = { Text(if (canGoBack) "Add an account" else "Buzzcord") },
                navigationIcon = { if (canGoBack) IconButton(onClick = { Nav.pop() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } },
            )
        },
    ) { pad ->
        Column(Modifier.fillMaxSize().padding(pad)) {
            TabRow(selectedTabIndex = tab) {
                Tab(tab == 0, { tab = 0 }, text = { Text("Log in") })
                Tab(tab == 1, { tab = 1 }, text = { Text("QR code") })
                Tab(tab == 2, { tab = 2 }, text = { Text("Token") })
            }
            when (tab) {
                0 -> WebLogin(::done)
                1 -> QrLogin(::done)
                else -> TokenLogin(::done)
            }
        }
    }
}

// Pulls the token out of the logged-in web app: from its storage (through a fresh iframe, since
// the page hides its own localStorage), falling back to the web app's token module.
private const val TOKEN_JS = """(function(){
try{var f=document.createElement('iframe');f.style.display='none';document.body.appendChild(f);
var t=f.contentWindow.localStorage.getItem('token');document.body.removeChild(f);if(t)return t;}catch(e){}
try{var m=[];var w=window.webpackChunkdiscord_app;if(w){w.push([[Symbol()],{},function(r){for(var k in r.c)m.push(r.c[k]);}]);w.pop();
for(var i=0;i<m.length;i++){var x=m[i]&&m[i].exports;if(!x)continue;var cs=[x.default,x.Z,x.ZP,x];
for(var j=0;j<cs.length;j++){var c=cs[j];if(c&&typeof c.getToken==='function'){var tk=c.getToken();if(tk)return tk;}}}}}catch(e){}
return '';})()"""

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun WebLogin(onToken: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var finished by remember { mutableStateOf(false) }
    var polling by remember { mutableStateOf<Job?>(null) }

    fun decode(raw: String?): String? {
        // evaluateJavascript hands back a JSON string; the stored token is itself JSON-quoted.
        val once = (parseJson(raw ?: return null) as? String) ?: return null
        val t = once.trim().removeSurrounding("\"")
        return t.takeIf { it.length > 30 && '.' in it }
    }

    fun poll(view: WebView) {
        if (finished || polling?.isActive == true) return
        polling = scope.launch {
            repeat(40) {
                if (finished) return@launch
                view.evaluateJavascript(TOKEN_JS) { raw ->
                    val token = decode(raw)
                    if (token != null && !finished) {
                        finished = true
                        // Leave nothing of the web session behind in the WebView.
                        CookieManager.getInstance().removeAllCookies(null)
                        WebStorage.getInstance().deleteAllData()
                        onToken(token)
                    }
                }
                delay(750)
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        Text(
            "Log in to Discord as usual. Buzzcord keeps only the sign-in token, encrypted on this phone.",
            fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(12.dp),
        )
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                // Start clean, so adding a second account doesn't land back in the first one.
                CookieManager.getInstance().removeAllCookies(null)
                WebStorage.getInstance().deleteAllData()
                WebView(ctx).apply {
                    settings.javaScriptEnabled = true
                    settings.domStorageEnabled = true
                    settings.userAgentString = ClientInfo.USER_AGENT
                    settings.useWideViewPort = true
                    settings.loadWithOverviewMode = true
                    settings.builtInZoomControls = true
                    settings.displayZoomControls = false
                    webViewClient = object : WebViewClient() {
                        override fun doUpdateVisitedHistory(view: WebView, url: String?, isReload: Boolean) {
                            if (url != null && ("/channels" in url || url.endsWith("/app"))) poll(view)
                        }

                        override fun onPageFinished(view: WebView, url: String?) {
                            if (url != null && ("/channels" in url || url.endsWith("/app"))) poll(view)
                        }
                    }
                    loadUrl("https://discord.com/login")
                }
            },
            onRelease = { it.destroy() },
        )
    }
}

@Composable
private fun QrLogin(onToken: (String) -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    key(attempt) {
        val scope = rememberCoroutineScope()
        var qr by remember { mutableStateOf<Bitmap?>(null) }
        var scanned by remember { mutableStateOf<String?>(null) }
        var error by remember { mutableStateOf<String?>(null) }
        DisposableEffect(Unit) {
            val ra = RemoteAuth(scope)
            ra.onQr = { url -> qr = qrBitmap(url) }
            ra.onScanned = { name, _ -> scanned = name }
            ra.onSuccess = onToken
            ra.onFailed = { error = it }
            ra.start()
            onDispose { ra.cancel() }
        }
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                "Scan this with the Discord app on another phone or tablet that's signed in to the account you want to add: profile picture > Scan QR Code.",
                textAlign = TextAlign.Center,
            )
            Box(Modifier.size(260.dp).background(Color.White), contentAlignment = Alignment.Center) {
                val bmp = qr
                when {
                    error != null -> Text(error ?: "", color = Color.Black, textAlign = TextAlign.Center, modifier = Modifier.padding(16.dp))
                    scanned != null -> Text("Scanned by ${scanned}.\nConfirm on that device.", color = Color.Black, textAlign = TextAlign.Center, fontWeight = FontWeight.Bold)
                    bmp != null -> Image(bmp.asImageBitmap(), "QR code", Modifier.size(240.dp), filterQuality = FilterQuality.None)
                    else -> CircularProgressIndicator()
                }
            }
            if (error != null) Button(onClick = { attempt++ }) { Text("New code") }
        }
    }
}

private fun qrBitmap(text: String): Bitmap {
    val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, mapOf(EncodeHintType.MARGIN to 1))
    val bmp = Bitmap.createBitmap(m.width, m.height, Bitmap.Config.ARGB_8888)
    for (x in 0 until m.width) for (y in 0 until m.height) bmp.setPixel(x, y, if (m[x, y]) android.graphics.Color.BLACK else android.graphics.Color.WHITE)
    return bmp
}

@Composable
private fun TokenLogin(onToken: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var token by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp)) {
        Text("Paste an account token.", fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        Text(
            "A token is a full key to the account. Never paste one someone else gave you, and never share yours: anyone with it can use the account.",
            fontSize = 13.sp, color = MaterialTheme.colorScheme.error,
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            token, { token = it.trim(); error = null }, singleLine = true, label = { Text("Token") },
            visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(),
        )
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 12.sp) }
        Spacer(Modifier.height(12.dp))
        Button(enabled = token.isNotEmpty() && !busy, onClick = {
            busy = true
            scope.launch {
                try {
                    Api(token).get("/users/@me")
                    onToken(token)
                } catch (e: Exception) {
                    error = "Discord didn't accept that token. (${e.message})"
                }
                busy = false
            }
        }) { Text(if (busy) "Checking..." else "Sign in") }
    }
}
