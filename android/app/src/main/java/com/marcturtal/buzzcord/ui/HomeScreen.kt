@file:OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)

package com.marcturtal.buzzcord.ui

import android.content.Intent
import android.provider.Settings
import android.widget.Toast
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.marcturtal.buzzcord.data.Accounts
import com.marcturtal.buzzcord.data.Session
import com.marcturtal.buzzcord.net.Channel
import com.marcturtal.buzzcord.net.Guild
import kotlinx.coroutines.launch

@Composable
fun HomeScreen() {
    val sessions by Accounts.sessions.collectAsState()
    val activeId by Accounts.activeId.collectAsState()
    if (sessions.isEmpty()) return
    val activeIndex = sessions.indexOfFirst { it.id == activeId }.coerceAtLeast(0)
    val pager = rememberPagerState(initialPage = activeIndex) { sessions.size }
    val latest by rememberUpdatedState(sessions)

    // Tapping an account jumps there instantly; swiping the list switches accounts too.
    LaunchedEffect(activeIndex, sessions.size) {
        if (pager.currentPage != activeIndex && activeIndex < sessions.size) pager.scrollToPage(activeIndex)
    }
    LaunchedEffect(pager) {
        snapshotFlow { pager.settledPage }.collect { i -> latest.getOrNull(i)?.let { Accounts.switchTo(it.id) } }
    }

    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        AccountBar(sessions, activeId)
        HorizontalPager(state = pager, key = { i -> sessions.getOrNull(i)?.id ?: i }, modifier = Modifier.fillMaxSize()) { page ->
            sessions.getOrNull(page)?.let { BuddyList(it) }
        }
    }
}

@Composable
private fun AccountBar(sessions: List<Session>, activeId: String?) {
    var menu by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    var showAddFriend by remember { mutableStateOf(false) }
    // Redraw unread badges when any account changes.
    val revs = sessions.map { it.revision.collectAsState().value }
    val total = remember(revs) { sessions.sumOf { it.badgeCount } }

    Surface(color = BrandPurple, contentColor = Color.White) {
        Row(Modifier.fillMaxWidth().statusBarsPadding().padding(start = 8.dp, top = 6.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            LazyRow(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(6.dp), contentPadding = PaddingValues(horizontal = 2.dp)) {
                items(sessions, key = { it.id }) { s -> AccountChip(s, s.id == activeId) }
                item {
                    IconButton(onClick = { Nav.push(Screen.SignIn) }) {
                        Icon(Icons.Default.Add, contentDescription = "Add account", tint = BrandYellow)
                    }
                }
            }
            IconButton(onClick = { Nav.push(Screen.Unread) }) {
                BadgedBox(badge = { if (total > 0) Badge { Text(if (total > 99) "99+" else total.toString()) } }) {
                    Icon(Icons.Default.Email, contentDescription = "Unread on every account")
                }
            }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "Menu") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Add a friend") }, onClick = { menu = false; showAddFriend = true })
                    DropdownMenuItem(text = { Text("Add an account") }, onClick = { menu = false; Nav.push(Screen.SignIn) })
                    DropdownMenuItem(text = { Text("Settings") }, onClick = { menu = false; showSettings = true })
                }
            }
        }
    }
    if (showSettings) SettingsDialog { showSettings = false }
    if (showAddFriend) Accounts.active?.let { AddFriendDialog(it) { showAddFriend = false } }
}

@Composable
private fun AccountChip(s: Session, active: Boolean) {
    val rev by s.revision.collectAsState()
    val state by s.connectionState.collectAsState()
    var menu by remember { mutableStateOf(false) }
    var confirmRemove by remember { mutableStateOf(false) }
    val count = remember(rev) { s.badgeCount }
    val shownStatus = when {
        s.paused || s.signedOut -> "offline"
        state != "Online" -> "idle"
        else -> if (s.status == "invisible") "offline" else s.status
    }
    Box {
        BadgedBox(badge = { if (count > 0) Badge { Text(if (count > 99) "99+" else count.toString()) } }) {
            Avatar(
                s.avatarUrl, 40.dp, status = shownStatus, fallback = s.displayName,
                modifier = Modifier
                    .alpha(if (s.paused) 0.45f else 1f)
                    .border(2.5.dp, if (active) BrandYellow else Color.Transparent, CircleShape)
                    .padding(3.dp)
                    .combinedClickable(onClick = { Accounts.switchTo(s.id) }, onLongClick = { menu = true }),
            )
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            Text(s.displayName, fontWeight = FontWeight.Bold, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
            Text(if (s.paused) "Paused" else state, fontSize = 12.sp, modifier = Modifier.padding(horizontal = 16.dp))
            HorizontalDivider(Modifier.padding(vertical = 6.dp))
            DropdownMenuItem(
                text = { Text(if (s.paused) "Resume (reconnect)" else "Pause (disconnect, saves battery)") },
                onClick = { menu = false; Accounts.setPaused(s, !s.paused) },
            )
            DropdownMenuItem(text = { Text("Move left") }, onClick = { menu = false; Accounts.move(s, -1) })
            DropdownMenuItem(text = { Text("Move right") }, onClick = { menu = false; Accounts.move(s, 1) })
            DropdownMenuItem(text = { Text("Sign out of this account") }, onClick = { menu = false; confirmRemove = true })
        }
    }
    if (confirmRemove) {
        AlertDialog(
            onDismissRequest = { confirmRemove = false },
            title = { Text("Sign out of ${s.displayName}?") },
            text = { Text("Buzzcord forgets this account on this phone. To end the sign-in on Discord's side too, remove it under Discord's Settings > Devices.") },
            confirmButton = { TextButton(onClick = { confirmRemove = false; Accounts.remove(s) }) { Text("Sign out") } },
            dismissButton = { TextButton(onClick = { confirmRemove = false }) { Text("Cancel") } },
        )
    }
}

private class BuddyModel(
    val requests: List<String>,
    val recent: List<Channel>,
    val online: List<String>,
    val offline: List<String>,
    val guilds: List<Guild>,
)

private fun buildModel(s: Session, query: String): BuddyModel {
    val q = query.trim().lowercase()
    fun match(text: String) = q.isEmpty() || text.lowercase().contains(q)
    val friends = s.relationshipsOf(1).filter { match(s.name(it)) }.sortedBy { s.name(it).lowercase() }
    return BuddyModel(
        requests = s.relationshipsOf(3).filter { match(s.name(it)) },
        recent = s.recentChats().filter { match(s.title(it)) }.take(if (q.isEmpty()) 40 else 200),
        online = friends.filter { s.presence(it).status != "offline" },
        offline = friends.filter { s.presence(it).status == "offline" },
        guilds = s.guildOrder.mapNotNull { s.guilds[it] }.filter { g -> q.isEmpty() || match(g.name) || s.visibleChannels(g).any { match(it.name ?: "") } },
    )
}

@Composable
private fun BuddyList(s: Session) {
    val rev by s.revision.collectAsState()
    val state by s.connectionState.collectAsState()
    var query by rememberSaveable(s.id) { mutableStateOf("") }
    val collapsed = remember(s.id) { mutableStateMapOf("offline" to true, "servers" to false) }
    val openGuilds = remember(s.id) { mutableStateMapOf<String, Boolean>() }
    val model = remember(rev, query) { buildModel(s, query) }
    val scope = rememberCoroutineScope()
    val ctx = LocalContext.current

    fun openFriend(uid: String) {
        s.dmWith(uid)?.let { Nav.push(Screen.Chat(s.id, it.id)); return }
        scope.launch {
            try { Nav.push(Screen.Chat(s.id, s.openDm(uid).id)) } catch (e: Exception) { Toast.makeText(ctx, e.message, Toast.LENGTH_LONG).show() }
        }
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item(key = "header") { MyHeader(s, state) }
        item(key = "search") {
            OutlinedTextField(
                value = query, onValueChange = { query = it }, singleLine = true,
                placeholder = { Text("Search friends, chats, servers") },
                leadingIcon = { Icon(Icons.Default.Search, null) },
                shape = RoundedCornerShape(24.dp),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            )
        }
        if (s.paused) {
            item { Hint("This account is paused. Long-press its picture at the top to resume.") }
            return@LazyColumn
        }
        if (!s.isReady) {
            item { Hint(if (s.signedOut) "Signed out. Long-press the picture to sign out, then add it again." else state) }
            return@LazyColumn
        }

        if (model.requests.isNotEmpty()) {
            item(key = "h-req") { SectionHeader("Friend requests", model.requests.size, collapsed["req"] == true) { collapsed["req"] = collapsed["req"] != true } }
            if (collapsed["req"] != true) items(model.requests, key = { "req$it" }) { uid -> RequestRow(s, uid) }
        }

        item(key = "h-recent") { SectionHeader("Recent chats", model.recent.size, collapsed["recent"] == true) { collapsed["recent"] = collapsed["recent"] != true } }
        if (collapsed["recent"] != true) items(model.recent, key = { "c" + it.id }) { c -> ChatRow(s, c) }

        item(key = "h-online") { SectionHeader("Friends", model.online.size, collapsed["online"] == true) { collapsed["online"] = collapsed["online"] != true } }
        if (collapsed["online"] != true) items(model.online, key = { "f$it" }) { uid -> FriendRow(s, uid) { openFriend(uid) } }

        item(key = "h-offline") { SectionHeader("Offline", model.offline.size, collapsed["offline"] == true) { collapsed["offline"] = collapsed["offline"] != true } }
        if (collapsed["offline"] != true) items(model.offline, key = { "o$it" }) { uid -> FriendRow(s, uid) { openFriend(uid) } }

        item(key = "h-servers") { SectionHeader("Servers", model.guilds.size, collapsed["servers"] == true) { collapsed["servers"] = collapsed["servers"] != true } }
        if (collapsed["servers"] != true) {
            for (g in model.guilds) {
                item(key = "g" + g.id) { GuildRow(s, g, openGuilds[g.id] == true) { openGuilds[g.id] = openGuilds[g.id] != true } }
                if (openGuilds[g.id] == true || query.isNotBlank()) {
                    val chans = s.visibleChannels(g).filter { query.isBlank() || (it.name ?: "").contains(query.trim(), ignoreCase = true) || g.name.contains(query.trim(), ignoreCase = true) }
                    items(chans, key = { "gc" + it.id }) { c -> ChannelRow(s, c) }
                }
            }
        }
        item { Spacer(Modifier.height(48.dp)) }
    }
}

@Composable
private fun Hint(text: String) {
    Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(24.dp))
}

@Composable
private fun MyHeader(s: Session, state: String) {
    var menu by remember { mutableStateOf(false) }
    var editMessage by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Avatar(s.avatarUrl, 48.dp, status = if (s.status == "invisible") "offline" else s.status, fallback = s.displayName)
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text(s.displayName, fontWeight = FontWeight.Bold, fontSize = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Box {
                Row(Modifier.clickable { menu = true }, verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        if (state == "Online" || s.paused) statusLabel(s.status) + (s.customStatusText?.let { " · $it" } ?: "") else state,
                        fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                    Icon(Icons.Default.KeyboardArrowDown, null, Modifier.size(16.dp))
                }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    for (st in listOf("online", "idle", "dnd", "invisible")) {
                        DropdownMenuItem(
                            text = { Text(statusLabel(st)) },
                            leadingIcon = { Box(Modifier.size(12.dp).clip(CircleShape).background(statusColor(st))) },
                            onClick = { menu = false; s.setStatus(st) },
                        )
                    }
                    HorizontalDivider()
                    DropdownMenuItem(text = { Text("Set status message...") }, onClick = { menu = false; editMessage = true })
                }
            }
        }
    }
    if (editMessage) {
        var text by remember { mutableStateOf(s.customStatusText ?: "") }
        AlertDialog(
            onDismissRequest = { editMessage = false },
            title = { Text("Status message") },
            text = { OutlinedTextField(text, { text = it }, singleLine = true, placeholder = { Text("What's up?") }) },
            confirmButton = { TextButton(onClick = { editMessage = false; s.setCustomStatus(text) }) { Text("Set") } },
            dismissButton = { TextButton(onClick = { editMessage = false; s.setCustomStatus(null) }) { Text("Clear") } },
        )
    }
}

@Composable
private fun SectionHeader(title: String, count: Int, collapsed: Boolean, onToggle: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onToggle).padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(if (collapsed) Icons.Default.KeyboardArrowRight else Icons.Default.KeyboardArrowDown, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
        Text("$title ($count)", fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary, fontSize = 14.sp)
    }
}

@Composable
private fun Row2(
    avatar: @Composable () -> Unit,
    title: String,
    subtitle: String?,
    bold: Boolean,
    badge: Int,
    onClick: () -> Unit,
    trailing: (@Composable () -> Unit)? = null,
) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        avatar()
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text(title, fontWeight = if (bold) FontWeight.Bold else FontWeight.Normal, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (!subtitle.isNullOrEmpty()) {
                Text(subtitle, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        if (badge > 0) Badge(containerColor = MaterialTheme.colorScheme.error) { Text(badge.toString()) }
        trailing?.invoke()
    }
}

@Composable
private fun ChatRow(s: Session, c: Channel) {
    val uid = if (c.isDm) c.recipientIds.firstOrNull() else null
    val p = s.presence(uid)
    val unread = s.isUnread(c)
    Row2(
        avatar = { Avatar(s.chatAvatar(c), 40.dp, status = if (uid != null) p.status else null, fallback = s.title(c)) },
        title = s.title(c),
        subtitle = if (uid != null) p.summary else "${c.recipientIds.size + 1} people",
        bold = unread,
        badge = if (unread) s.mentionCount(c.id) else 0,
        onClick = { Nav.push(Screen.Chat(s.id, c.id)) },
    )
}

@Composable
private fun FriendRow(s: Session, uid: String, onClick: () -> Unit) {
    val p = s.presence(uid)
    Row2(
        avatar = { Avatar(s.user(uid).avatarUrl(64), 40.dp, status = p.status) },
        title = s.name(uid),
        subtitle = p.summary,
        bold = false,
        badge = 0,
        onClick = onClick,
    )
}

@Composable
private fun RequestRow(s: Session, uid: String) {
    val scope = rememberCoroutineScope()
    val ctx = LocalContext.current
    Row2(
        avatar = { Avatar(s.user(uid).avatarUrl(64), 40.dp) },
        title = s.name(uid),
        subtitle = "wants to be your friend",
        bold = true,
        badge = 0,
        onClick = {},
        trailing = {
            Row {
                TextButton(onClick = { scope.launch { try { s.acceptFriend(uid) } catch (e: Exception) { Toast.makeText(ctx, e.message, Toast.LENGTH_LONG).show() } } }) { Text("Accept") }
                TextButton(onClick = { scope.launch { try { s.removeRelationship(uid) } catch (e: Exception) { Toast.makeText(ctx, e.message, Toast.LENGTH_LONG).show() } } }) { Text("Ignore") }
            }
        },
    )
}

@Composable
private fun GuildRow(s: Session, g: Guild, open: Boolean, onClick: () -> Unit) {
    val mentions = s.visibleChannels(g).sumOf { s.mentionCount(it.id) }
    Row2(
        avatar = { Avatar(g.iconUrl(64), 36.dp, fallback = g.name) },
        title = g.name,
        subtitle = null,
        bold = mentions > 0,
        badge = mentions,
        onClick = onClick,
        trailing = { Icon(if (open) Icons.Default.KeyboardArrowDown else Icons.Default.KeyboardArrowRight, null) },
    )
}

@Composable
private fun ChannelRow(s: Session, c: Channel) {
    val unread = s.isUnread(c)
    Row(
        Modifier.fillMaxWidth().clickable { Nav.push(Screen.Chat(s.id, c.id)) }.padding(start = 64.dp, end = 16.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            "# " + (c.name ?: ""), modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis,
            fontWeight = if (unread) FontWeight.Bold else FontWeight.Normal,
            color = if (unread || s.isMuted(c).not()) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant,
        )
        val m = s.mentionCount(c.id)
        if (m > 0) Badge(containerColor = MaterialTheme.colorScheme.error) { Text(m.toString()) }
    }
}

@Composable
fun SettingsDialog(onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    var background by remember { mutableStateOf(Accounts.backgroundEnabled) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Settings") },
        text = {
            Column {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Stay connected in the background")
                        Text("Needed for notifications. One quiet notice covers every account.", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(background, { background = it; Accounts.backgroundEnabled = it })
                }
                Spacer(Modifier.height(12.dp))
                TextButton(onClick = {
                    try { ctx.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) } catch (e: Exception) { }
                }) { Text("Battery optimisation settings") }
                Text(
                    "If notifications arrive late, set Buzzcord to Unrestricted there. Paused accounts use no data or battery.",
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    "Buzzcord is unofficial and not made or endorsed by Discord Inc. Discord's Terms of Service don't allow third-party clients; use at your own risk. MIT License, github.com/mtits/Buzzcord",
                    fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

@Composable
fun AddFriendDialog(s: Session, onDismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Add a friend") },
        text = {
            Column {
                Text("As ${s.displayName}", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(4.dp))
                OutlinedTextField(name, { name = it; error = null }, singleLine = true, placeholder = { Text("username") })
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 12.sp) }
            }
        },
        confirmButton = {
            TextButton(enabled = name.isNotBlank() && !busy, onClick = {
                busy = true
                scope.launch {
                    try {
                        s.sendFriendRequest(name)
                        onDismiss()
                    } catch (e: Exception) {
                        error = e.message
                    } finally {
                        busy = false
                    }
                }
            }) { Text("Send request") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
