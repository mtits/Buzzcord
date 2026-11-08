package com.marcturtal.buzzcord

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.net.Api
import com.marcturtal.buzzcord.ui.App
import com.marcturtal.buzzcord.ui.BuzzTheme
import com.marcturtal.buzzcord.ui.CaptchaHost
import com.marcturtal.buzzcord.ui.Nav
import com.marcturtal.buzzcord.ui.Screen

class MainActivity : ComponentActivity() {
    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        Api.solveCaptcha = { CaptchaHost.solve(it) }
        if (savedInstanceState == null) handle(intent)
        setContent { BuzzTheme { App() } }
        // Ask for notifications once the first account is signed in.
        lifecycleScope.launch { Accounts.sessions.collect { if (it.isNotEmpty()) maybeAskNotifications() } }
    }

    private fun maybeAskNotifications() {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED &&
            !Accounts.prefs.getBoolean("AskedNotifications", false)
        ) {
            Accounts.prefs.edit().putBoolean("AskedNotifications", true).apply()
            askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    override fun onStart() {
        super.onStart()
        Accounts.appVisible = true
        CaptchaHost.uiAttached++
        Accounts.ensureService(this)
    }

    override fun onStop() {
        Accounts.appVisible = false
        CaptchaHost.uiAttached--
        super.onStop()
    }

    // From a notification or a launcher shortcut: switch to that account, and open the chat if one is named.
    private fun handle(intent: Intent?) {
        val acc = intent?.getStringExtra(Accounts.EXTRA_ACCOUNT) ?: return
        if (Accounts.session(acc) == null) return
        Accounts.switchTo(acc)
        Nav.home()
        intent.getStringExtra(Accounts.EXTRA_CHANNEL)?.let { Nav.push(Screen.Chat(acc, it)) }
    }
}
