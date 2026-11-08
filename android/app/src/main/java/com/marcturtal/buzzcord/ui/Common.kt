package com.marcturtal.buzzcord.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil.compose.AsyncImage

// The classic messenger colours: purple bar, yellow highlights.
val BrandPurple = Color(0xFF5B2A9E)
val BrandPurpleDark = Color(0xFF3E1A73)
val BrandYellow = Color(0xFFFFD43B)

@Composable
fun BuzzTheme(content: @Composable () -> Unit) {
    val scheme = if (isSystemInDarkTheme()) darkColorScheme(
        primary = Color(0xFFCDB4FF),
        onPrimary = Color(0xFF2B0B5C),
        primaryContainer = BrandPurpleDark,
        onPrimaryContainer = Color(0xFFEBDDFF),
        secondary = BrandYellow,
        onSecondary = Color(0xFF3A2F00),
        background = Color(0xFF16131C),
        surface = Color(0xFF16131C),
        surfaceVariant = Color(0xFF2A2533),
    ) else lightColorScheme(
        primary = BrandPurple,
        onPrimary = Color.White,
        primaryContainer = Color(0xFFEBDDFF),
        onPrimaryContainer = Color(0xFF2B0B5C),
        secondary = Color(0xFFC79A00),
        onSecondary = Color.White,
        surfaceVariant = Color(0xFFEFE9F5),
    )
    MaterialTheme(colorScheme = scheme, content = content)
}

fun statusColor(status: String): Color = when (status) {
    "online" -> Color(0xFF3BA55D)
    "idle" -> Color(0xFFFAA81A)
    "dnd" -> Color(0xFFED4245)
    else -> Color(0xFF80848E)
}

fun statusLabel(status: String): String = when (status) {
    "online" -> "Available"
    "idle" -> "Away"
    "dnd" -> "Busy"
    "invisible" -> "Invisible"
    else -> "Offline"
}

@Composable
fun Avatar(url: String?, size: Dp, modifier: Modifier = Modifier, status: String? = null, fallback: String = "") {
    Box(modifier.size(size)) {
        Box(
            Modifier.fillMaxSize().clip(CircleShape).background(MaterialTheme.colorScheme.surfaceVariant),
            contentAlignment = Alignment.Center,
        ) {
            if (url != null) {
                AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            } else {
                Text(fallback.take(1).uppercase(), fontWeight = FontWeight.Bold, fontSize = (size.value * 0.4f).sp)
            }
        }
        if (status != null) {
            Box(
                Modifier.align(Alignment.BottomEnd).size(size * 0.32f)
                    .border(2.dp, MaterialTheme.colorScheme.surface, CircleShape)
                    .clip(CircleShape).background(statusColor(status)),
            )
        }
    }
}

// A tiny back stack; screens are cheap and there are only a few of them.
sealed interface Screen {
    data object Home : Screen
    data class Chat(val account: String, val channel: String) : Screen
    data object SignIn : Screen
    data object Unread : Screen
}

object Nav {
    val stack = mutableStateListOf<Screen>(Screen.Home)
    fun push(s: Screen) { stack.add(s) }
    fun pop(): Boolean = if (stack.size > 1) { stack.removeAt(stack.lastIndex); true } else false
    fun home() { while (stack.size > 1) stack.removeAt(stack.lastIndex) }
}
