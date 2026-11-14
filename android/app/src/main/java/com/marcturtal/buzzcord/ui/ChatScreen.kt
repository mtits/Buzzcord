@file:OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)

package com.marcturtal.buzzcord.ui

import android.text.format.DateUtils
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil.compose.AsyncImage
import com.marcturtal.buzzcord.Notifier
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.data.ChatEvent
import com.marcturtal.buzzcord.data.Format
import com.marcturtal.buzzcord.data.Session
import com.marcturtal.buzzcord.net.Api
import com.marcturtal.buzzcord.net.Snowflake
import com.marcturtal.buzzcord.net.jarr
import com.marcturtal.buzzcord.net.jobj
import com.marcturtal.buzzcord.net.long
import com.marcturtal.buzzcord.net.obj
import com.marcturtal.buzzcord.net.objs
import com.marcturtal.buzzcord.net.str
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.text.DateFormat
import kotlin.math.min

@Composable
fun ChatScreen(s: Session, channelId: String) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val rev by s.revision.collectAsState()
    val c = remember(rev, channelId) { s.findChannel(channelId) }
    val messages = remember(channelId) { mutableStateListOf<JSONObject>() }   // newest first
    var loading by remember(channelId) { mutableStateOf(true) }
    var reachedTop by remember(channelId) { mutableStateOf(false) }
    var loadingOlder by remember(channelId) { mutableStateOf(false) }
    val typing = remember(channelId) { mutableStateMapOf<String, Long>() }
    var text by rememberSaveable(channelId) { mutableStateOf("") }
    var replyTo by remember(channelId) { mutableStateOf<JSONObject?>(null) }
    var editing by remember(channelId) { mutableStateOf<JSONObject?>(null) }
    var uploading by remember { mutableStateOf(false) }
    var lastTypingSent by remember { mutableStateOf(0L) }
    val list = rememberLazyListState()
    val meId = s.me?.id

    fun toast(msg: String?) = Toast.makeText(ctx, msg ?: "Something went wrong", Toast.LENGTH_LONG).show()

    // First page, then live updates from the gateway.
    LaunchedEffect(channelId) {
        try {
            val page = s.api.get("/channels/$channelId/messages?limit=50") as? JSONArray
            messages.clear()
            page?.let { a -> for (i in 0 until a.length()) a.optJSONObject(i)?.let { messages.add(it) } }
            if ((page?.length() ?: 0) < 50) reachedTop = true
        } catch (e: Exception) {
            toast(e.message)
        }
        loading = false
        s.markRead(s.findChannel(channelId))
    }
    LaunchedEffect(channelId) {
        s.events.collect { ev ->
            if (ev.channelId != channelId) return@collect
            when (ev) {
                is ChatEvent.Created -> {
                    val nonce = ev.m.str("nonce")
                    val i = if (nonce != null) messages.indexOfFirst { it.optBoolean("_pending") && it.str("nonce") == nonce } else -1
                    if (i >= 0) messages[i] = ev.m
                    else if (messages.none { it.str("id") == ev.m.str("id") }) messages.add(0, ev.m)
                    ev.m.obj("author")?.str("id")?.let { typing.remove(it) }
                    if (Accounts.appVisible) s.markRead(s.findChannel(channelId))
                    if (list.firstVisibleItemIndex <= 2) list.animateScrollToItem(0)
                    if (ev.m.str("content") == Format.BUZZ && ev.m.obj("author")?.str("id") != meId) Notifier.buzz(ctx)
                }
                is ChatEvent.Updated -> {
                    val i = messages.indexOfFirst { it.str("id") == ev.m.str("id") }
                    if (i >= 0) {
                        val merged = JSONObject(messages[i].toString())
                        for (k in ev.m.keys()) merged.put(k, ev.m.get(k))
                        messages[i] = merged
                    }
                }
                is ChatEvent.Deleted -> messages.removeAll { it.str("id") == ev.messageId }
                is ChatEvent.Typing -> typing[ev.userId] = System.currentTimeMillis()
            }
        }
    }
    // Typing indicators fade after 10 s.
    LaunchedEffect(channelId) {
        while (true) {
            delay(2000)
            val now = System.currentTimeMillis()
            typing.keys.filter { now - (typing[it] ?: 0) > 10_000 }.forEach { typing.remove(it) }
        }
    }
    // Older messages when scrolled near the top.
    LaunchedEffect(channelId) {
        snapshotFlow { list.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0 }.collect { last ->
            if (!loading && !reachedTop && !loadingOlder && messages.isNotEmpty() && last >= messages.size - 8) {
                loadingOlder = true
                try {
                    val oldest = messages.last().str("id")
                    val page = s.api.get("/channels/$channelId/messages?limit=50&before=$oldest") as? JSONArray
                    page?.let { a -> for (i in 0 until a.length()) a.optJSONObject(i)?.let { messages.add(it) } }
                    if ((page?.length() ?: 0) < 50) reachedTop = true
                } catch (e: Exception) {
                    reachedTop = true
                }
                loadingOlder = false
            }
        }
    }
    DisposableEffect(channelId) {
        Accounts.visibleChat = s.id to channelId
        Notifier.cancel(ctx, s.id, channelId)
        onDispose { if (Accounts.visibleChat == (s.id to channelId)) Accounts.visibleChat = null }
    }

    fun send(content: String, attachments: JSONArray? = null) {
        val body = content.trim()
        if (body.isEmpty() && attachments == null) return
        val ed = editing
        if (ed != null) {
            editing = null
            scope.launch {
                try { s.api.patch("/channels/$channelId/messages/${ed.str("id")}", jobj("content" to body)) } catch (e: Exception) { toast(e.message) }
            }
            return
        }
        val nonce = Api.nonce()
        val reply = replyTo
        replyTo = null
        if (attachments == null) {
            // Shown right away, swapped for the real message when Discord echoes it.
            val me = s.me
            messages.add(0, jobj(
                "id" to nonce, "nonce" to nonce, "content" to body, "_pending" to true, "channel_id" to channelId,
                "author" to jobj("id" to me?.id, "username" to me?.username, "global_name" to me?.globalName, "avatar" to me?.avatar),
            ))
            scope.launch { list.scrollToItem(0) }
        }
        scope.launch {
            try {
                s.api.sendMessage(channelId, body, reply?.str("id"), attachments, nonce)
            } catch (e: Exception) {
                messages.removeAll { it.str("nonce") == nonce && it.optBoolean("_pending") }
                if (text.isEmpty()) text = body
                toast(e.message)
            }
        }
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        uploading = true
        val caption = text
        text = ""
        scope.launch {
            try {
                val att = s.api.uploadAttachment(ctx, channelId, uri, 0)
                send(caption, jarr(att))
            } catch (e: Exception) {
                text = caption
                toast(e.message)
            }
            uploading = false
        }
    }

    val dm = c?.isDm == true
    val otherId = if (dm) c?.recipientIds?.firstOrNull() else null
    Scaffold(
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(containerColor = BrandPurple, titleContentColor = Color.White, navigationIconContentColor = Color.White),
                navigationIcon = { IconButton(onClick = { Nav.pop() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } },
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Avatar(s.chatAvatar(c), 34.dp, status = otherId?.let { s.presence(it).status }, fallback = s.title(c))
                        Column(Modifier.padding(start = 10.dp)) {
                            Text(s.title(c), maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = 17.sp)
                            // Always visible: which account you're talking as.
                            Text(
                                "as ${s.displayName}" + (s.guildOf(c)?.let { " · ${it.name}" } ?: ""),
                                fontSize = 12.sp, color = BrandYellow, maxLines = 1, overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }
                },
            )
        },
        bottomBar = {
            Surface(tonalElevation = 3.dp) {
                Column(Modifier.windowInsetsPadding(WindowInsets.navigationBars.union(WindowInsets.ime))) {
                    val names = typing.keys.map { s.name(it) }
                    if (names.isNotEmpty()) {
                        Text(
                            names.joinToString(", ") + if (names.size == 1) " is typing..." else " are typing...",
                            fontSize = 12.sp, fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(start = 16.dp, top = 4.dp),
                        )
                    }
                    val bar = replyTo?.let { "Replying to " + s.name(it.obj("author")?.str("id")) } ?: editing?.let { "Editing message" }
                    if (bar != null) {
                        Row(Modifier.fillMaxWidth().padding(start = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(bar, fontSize = 12.sp, modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.primary)
                            IconButton(onClick = { replyTo = null; if (editing != null) { editing = null; text = "" } }) { Icon(Icons.Default.Close, "Cancel", Modifier.size(18.dp)) }
                        }
                    }
                    if (c != null && !s.canSend(c)) {
                        Text("You can't send messages here.", Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    } else {
                        Row(Modifier.fillMaxWidth().padding(6.dp), verticalAlignment = Alignment.CenterVertically) {
                            IconButton(onClick = { picker.launch("*/*") }, enabled = !uploading && editing == null) {
                                if (uploading) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Default.Add, "Send a file")
                            }
                            OutlinedTextField(
                                value = text,
                                onValueChange = {
                                    text = it
                                    val now = System.currentTimeMillis()
                                    if (it.isNotEmpty() && editing == null && now - lastTypingSent > 8000) {
                                        lastTypingSent = now
                                        s.api.fire("POST", "/channels/$channelId/typing", null)
                                    }
                                },
                                placeholder = { Text("Message as ${s.displayName}") },
                                maxLines = 5,
                                shape = RoundedCornerShape(22.dp),
                                modifier = Modifier.weight(1f),
                            )
                            if (c?.isPrivate == true && text.isEmpty() && editing == null) {
                                TextButton(onClick = { send(Format.BUZZ) }) {
                                    Text("BUZZ!", fontWeight = FontWeight.Black, fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.secondary)
                                }
                            } else {
                                IconButton(onClick = { val t = text; text = ""; send(t) }, enabled = text.isNotBlank()) {
                                    Icon(Icons.AutoMirrored.Filled.Send, "Send", tint = MaterialTheme.colorScheme.primary)
                                }
                            }
                        }
                    }
                }
            }
        },
    ) { pad ->
        Box(Modifier.fillMaxSize().padding(pad)) {
            if (loading) CircularProgressIndicator(Modifier.align(Alignment.Center))
            LazyColumn(state = list, reverseLayout = true, modifier = Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 8.dp)) {
                itemsIndexed(messages, key = { _, m -> m.str("id") ?: m.hashCode() }) { i, m ->
                    val older = messages.getOrNull(i + 1)
                    MessageItem(
                        s, m, older,
                        onReply = { replyTo = m; editing = null },
                        onEdit = { editing = m; replyTo = null; text = m.str("content") ?: "" },
                        onDelete = { scope.launch { try { s.api.delete("/channels/$channelId/messages/${m.str("id")}") } catch (e: Exception) { toast(e.message) } } },
                    )
                }
                if (loadingOlder) item { Box(Modifier.fillMaxWidth().padding(12.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp)) } }
            }
        }
    }
}

@Composable
private fun MessageItem(s: Session, m: JSONObject, older: JSONObject?, onReply: () -> Unit, onEdit: () -> Unit, onDelete: () -> Unit) {
    val clipboard = LocalClipboardManager.current
    val uri = LocalUriHandler.current
    var menu by remember { mutableStateOf(false) }
    val authorJ = m.obj("author")
    val authorId = authorJ?.str("id")
    val mine = authorId != null && authorId == s.me?.id
    val time = Snowflake.time(m.str("id")).takeIf { it > 0 && !m.optBoolean("_pending") } ?: System.currentTimeMillis()
    val ref = m.obj("referenced_message")
    val type = m.long("type").toInt()
    val compact = older != null && ref == null && older.obj("author")?.str("id") == authorId &&
        time - Snowflake.time(older.str("id")) < 7 * 60_000
    val linkColor = MaterialTheme.colorScheme.primary
    val mentionColor = MaterialTheme.colorScheme.secondary

    // System messages (joins, pins, calls) get one quiet line.
    if (type != 0 && type != 19) {
        Text(
            "${s.name(authorId)} " + when (type) {
                1 -> "added someone to the group"; 2 -> "removed someone from the group"; 3 -> "started a call"
                4 -> "changed the group name"; 5 -> "changed the group icon"; 6 -> "pinned a message"; 7 -> "joined the server"
                else -> "did something"
            },
            fontSize = 12.sp, fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
        )
        return
    }

    Box {
        Row(
            Modifier.fillMaxWidth()
                .combinedClickable(onClick = {}, onLongClick = { menu = true })
                .padding(start = 12.dp, end = 12.dp, top = if (compact) 1.dp else 8.dp, bottom = 1.dp),
        ) {
            if (compact) Spacer(Modifier.width(44.dp))
            else Avatar(s.user(authorId).let { u -> if (u.username == "unknown" && authorJ != null) com.marcturtal.buzzcord.net.User.from(authorJ) else u }.avatarUrl(64), 36.dp, modifier = Modifier.padding(end = 8.dp))
            Column(Modifier.weight(1f)) {
                if (ref != null) {
                    Text(
                        "↪ " + s.name(ref.obj("author")?.str("id")) + ": " + Format.plain(s, ref),
                        fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                if (!compact) {
                    Row(verticalAlignment = Alignment.Bottom) {
                        Text(
                            s.name(authorId).let { if (it == "unknown") authorJ?.str("global_name") ?: authorJ?.str("username") ?: it else it },
                            fontWeight = FontWeight.Bold, fontSize = 14.sp,
                            color = if (mine) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                        )
                        Text(
                            "  " + DateUtils.formatSameDayTime(time, System.currentTimeMillis(), DateFormat.SHORT, DateFormat.SHORT),
                            fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                val content = m.str("content") ?: ""
                if (content == Format.BUZZ) {
                    Text("BUZZ!", fontWeight = FontWeight.Black, fontStyle = FontStyle.Italic, color = Color(0xFFE53935), fontSize = 18.sp)
                } else if (content.isNotEmpty()) {
                    Text(
                        Format.rich(s, m, linkColor, mentionColor),
                        fontSize = 15.sp,
                        color = if (m.optBoolean("_pending")) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                    )
                }
                if (m.has("edited_timestamp") && !m.isNull("edited_timestamp")) {
                    Text("(edited)", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                for (a in m.objs("attachments")) Attachment(a) { uri.openUri(a.str("url") ?: "") }
                for (e in m.objs("embeds")) Embed(e) { url -> uri.openUri(url) }
                for (st in m.objs("sticker_items")) Text("[sticker: ${st.str("name")}]", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                val reactions = m.objs("reactions")
                if (reactions.isNotEmpty()) {
                    Text(
                        reactions.joinToString("  ") { r -> (r.obj("emoji")?.let { e -> if (e.str("id") == null) e.str("name") else ":${e.str("name")}:" } ?: "?") + " " + r.long("count") },
                        fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp),
                    )
                }
            }
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text("Reply") }, onClick = { menu = false; onReply() })
            DropdownMenuItem(text = { Text("Copy text") }, onClick = { menu = false; clipboard.setText(AnnotatedString(m.str("content") ?: "")) })
            if (mine && !m.optBoolean("_pending")) {
                DropdownMenuItem(text = { Text("Edit") }, onClick = { menu = false; onEdit() })
                DropdownMenuItem(text = { Text("Delete") }, onClick = { menu = false; onDelete() })
            }
        }
    }
}

// Pictures come through Discord's media proxy at screen size, not full resolution: far less data.
private fun sized(url: String, w: Long, h: Long, max: Int = 640): String {
    if (w <= 0 || h <= 0) return url
    val scale = min(1.0, max.toDouble() / maxOf(w, h))
    val tw = (w * scale).toInt().coerceAtLeast(1)
    val th = (h * scale).toInt().coerceAtLeast(1)
    return url + (if ('?' in url) "&" else "?") + "width=$tw&height=$th"
}

@Composable
private fun Attachment(a: JSONObject, open: () -> Unit) {
    val type = a.str("content_type") ?: ""
    val w = a.long("width")
    val h = a.long("height")
    if (type.startsWith("image/") && w > 0 && h > 0) {
        val src = a.str("proxy_url")?.let { sized(it, w, h) } ?: a.str("url")
        AsyncImage(
            model = src, contentDescription = a.str("filename"), contentScale = ContentScale.Fit,
            modifier = Modifier.padding(top = 4.dp).fillMaxWidth(0.8f).aspectRatio((w.toFloat() / h).coerceIn(0.4f, 3f))
                .heightIn(max = 320.dp).clip(RoundedCornerShape(8.dp)).clickable(onClick = open),
        )
    } else {
        Text(
            "📎 " + (a.str("filename") ?: "file") + "  " + humanSize(a.long("size")),
            color = MaterialTheme.colorScheme.primary, fontSize = 14.sp,
            modifier = Modifier.padding(top = 4.dp).clickable(onClick = open),
        )
    }
}

private fun humanSize(b: Long): String = when {
    b <= 0 -> ""
    b < 1024 -> "$b B"
    b < 1024 * 1024 -> "${b / 1024} KB"
    else -> String.format("%.1f MB", b / 1048576.0)
}

@Composable
private fun Embed(e: JSONObject, open: (String) -> Unit) {
    val type = e.str("type") ?: ""
    val thumb = e.obj("thumbnail") ?: e.obj("image")
    if ((type == "gifv" || type == "image") && thumb != null) {
        val w = thumb.long("width")
        val h = thumb.long("height")
        val src = thumb.str("proxy_url")?.let { sized(it, w, h) } ?: thumb.str("url")
        AsyncImage(
            model = src, contentDescription = null, contentScale = ContentScale.Fit,
            modifier = Modifier.padding(top = 4.dp).fillMaxWidth(0.7f)
                .aspectRatio(if (w > 0 && h > 0) (w.toFloat() / h).coerceIn(0.4f, 3f) else 1.5f)
                .clip(RoundedCornerShape(8.dp)).clickable { e.str("url")?.let(open) },
        )
        return
    }
    val title = e.str("title")
    val desc = e.str("description")
    if (title == null && desc == null) return
    Row(
        Modifier.padding(top = 4.dp).clip(RoundedCornerShape(6.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .clickable { e.str("url")?.let(open) }.padding(8.dp),
    ) {
        Column(Modifier.weight(1f, fill = false)) {
            e.obj("provider")?.str("name")?.let { Text(it, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            title?.let { Text(it, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, color = MaterialTheme.colorScheme.primary, maxLines = 2, overflow = TextOverflow.Ellipsis) }
            desc?.let { Text(it, fontSize = 13.sp, maxLines = 4, overflow = TextOverflow.Ellipsis) }
        }
        thumb?.str("proxy_url")?.let { u ->
            AsyncImage(model = sized(u, thumb.long("width"), thumb.long("height"), 160), contentDescription = null, contentScale = ContentScale.Crop,
                modifier = Modifier.padding(start = 8.dp).size(64.dp).clip(RoundedCornerShape(6.dp)))
        }
    }
}
