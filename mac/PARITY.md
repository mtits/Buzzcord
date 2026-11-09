# Mac and Windows: what each has

For comparing the Mac port (`mac/`, Swift) with the Windows version (`src/`, C#). The Mac port
was written from a copy of `src/` dated 2026-11-02, so anything added on Windows after that is
missing here. Fill in the "Windows since 2026-11-02" list and the Mac side gets ported.

## Ported from Windows (should match)

- Sign-in: QR code and token, several accounts, tokens in the keychain (Windows: DPAPI).
- Buddy list: Favorites, Recent, Groups, Friends, Offline, Servers, Requests; search, Add a Friend, status and status message. No group reordering yet (Windows: Options > Arrange groups).
- IM windows: history, pictures, GIFs, embeds, replies, reactions, edit, delete, typing, formatting, emoji, Send File, drag and drop, paste screenshots, BUZZ.
- Notifications: DMs, mentions, followed channels, friend requests, sign-ins, someone unfriending or blocking you (not when you removed them); mutes respected; Busy silences; pop-up IM windows; Dock badge (Windows: tray and taskbar badge).
- Auto-save of files and the chat log, member sidebar, message search, Mentions & Notifications history.
- Voice calls with DAVE (MLS checked against the IETF vectors in `testdata/mls`), voice activity and push-to-talk, per-user volume, ringing window.
- Profiles with the game Board (Favorite, Rotation, Games I Like, Want to Play), game picker.
- Rich presence over `discord-ipc-N` (Mac: a Unix socket in `$TMPDIR`), game detection, per-account sharing. Windows shares by default (Status > Share my game activity, per account; Options > Detect running games); the Mac starts with Options > Show What I'm Playing off.
- Full-size profile pictures and banners (Windows auto-saves them to Profile pictures; Mac opens a viewer with Download instead).
- Tray icon (Mac: menu bar icon: double-click shows every buddy list, click for a menu).
- Help menu: Rich Presence Status, Open Log Folder, Donate (PayPal), About.
- Mic and speaker picker for calls.

## Ported from the Mac (should match)

- **Auto-Away:** Away after N idle minutes (Options > Auto-Away: Off, 5, 10, 15, 30; default 10) or when the screen locks; back on input. The presence says `status: "idle"`, `afk: true`, `since` = when you went away; the saved status isn't changed, and Busy and Invisible keep their status (`afk` is still sent, as on the Mac). "Away (idle)" under your name. Mac: `AutoAway.swift`, `Session.shownStatus`. Windows: `AutoAway.cs` (GetLastInputInfo, SystemEvents.SessionSwitch for lock/unlock), `Session.ShownStatus`, `Session.SetAutoAway`.
- **Reply from a message alert**, and **Mark as Read**; in a server channel the reply references the alerted message, in DMs and groups it's a plain message; a failed reply shows an alert with the text you typed. Mac: the notification banner (`Support.swift` Notifier, `AccountController.replyFromBanner`). Windows: tray balloons can't hold a text box or buttons, so message alerts (DMs, mentions, followed channels) use a small pop-up by the clock with a reply box, Reply, Mark as Read and Open (`QuickReplyForm.cs`, `Session.ReplyTargetFor`). Other alerts keep the balloon, and so do message alerts while a game runs.
- **Global shortcut** that brings every buddy list forward from any app, changeable or off. Mac: Option+Command+B (`Shortcut.swift`). Windows: Ctrl+Alt+B, Options > Buddy list shortcut, RegisterHotKey; if another app has the combination, a tray balloon says so (`GlobalHotkey.cs`).
- **Unread Messages window** with message previews, Open, Mark as Read and Mark All as Read; it lists exactly what the unread count counts. Mac: Shift+Command+U or the menu bar icon (`Unread.swift`). Windows: Ctrl+Shift+U (Messenger menu) or the tray menu (`UnreadForm.cs`, `Session.UnreadEntries`, which the tray and taskbar numbers now add up).
- **The red (1) fix:** message requests (and DMs Discord filtered as spam) never count as unread, and a DM with no read marker from Discord (never opened anywhere) only counts while its last message is under 14 days old. Mac: `Session.isUnread`, `neverOpenedCounts`. Windows: `Session.IsUnread`, `Session.NeverOpenedCounts`, `Channel.IsRequest`.

- **Launch safety (2026-11-08):** the one-time notice before the first sign-in (Windows `NoticeForm.cs`, Mac `Notice.swift`, same wording), the token box warning, and the About box (not affiliated with Discord Inc. or Yahoo, MIT License, site and source links). Difference: Windows ticks Start with Windows in the notice by default; the Mac's Open at Login box starts unticked.

- **Updates from GitHub Releases (1.3, 2026-11-08):** checks 20 s after start and every 6 hours, downloads the platform's zip, checks its SHA-256, unpacks it refusing paths outside the staging folder, offers "Restart to update" (banner/balloon and a bold menu item), installs on the next start if ignored, rolls back on failure, and the restarted copy removes the old files. Options > Update Buzzcord Automatically, Help > Check for Updates. Windows: `Updater.cs` (renames each file to `.old`). Mac: `Updater.swift` (swaps the whole .app bundle, also checks the code signature, and refuses to run from Downloads, a disk image or App Translocation).
- **Block, Blocked Users and Remove from Recent (1.4.0):** Block... in the buddy list's right-click menu (friends, Recent chats, friend requests) and on profiles, with the same confirmation text; Messenger > Blocked users with picture, name, username and Unblock (Unblock also on the profile, which hides Message while they're blocked); blocked users' messages folded to "(time) Blocked message" (click to show), a reply quoting one says "(blocked message)", and they don't count as unread or raise alerts; Remove from Recent closes a one-to-one DM (`DELETE /channels/{id}`), never a group, not for a chat pinned to Favorites. API: block = `PUT /users/@me/relationships/{id}` with `{"type": 2}` after marking the user as removed by you, unblock = `DELETE` the same. Mac: `Blocking.swift`, `Session.block/unblock/isBlocked/closeDm`, `MessageHtml.render`. Windows: `Blocking.cs` (`Blocking`, `BlockedForm`), `Session.Block/Unblock/IsBlocked/BlockedUsers/CloseDm`, `MessageHtml.Render`. Differences: IE11 has no `<details>`, so Windows folds with a link and a little script; Windows takes the chat off Recent chats before Discord answers and puts it back if Discord refuses (the Mac waits for the answer); a blocked user's message in a chat that was read moves Buzzcord's own read marker past it, without telling Discord.
- **Donate window (1.3):** PayPal (International), GCash and InstaPay bank QR codes (Philippines) from `assets/donate`, from Help, the tray/menu bar menu and About. Windows: `DonateForm.cs`. Mac: `Donate.swift`.

## Mac only (candidates to bring to Windows)

- **Menu Bar Only:** hide the Dock icon. Not needed on Windows: Buzzcord already lives in the tray, and closing a buddy list only hides it.
- **"Don't Share" for one game** and Share Hidden Games Again. Windows has no per-game hiding, only the per-account switch and Detect running games.
- **Echo cancellation fallback:** tries voice processing on the chosen pair, checks that sound really moves, else runs mic and speakers separately, else falls back to the system default; switches over when devices are plugged in or out mid-call. `VoiceAudio.swift`. Not ported: Windows calls have no echo cancellation at all yet (the mic and speakers always run separately, and the README says to use headphones), so the problem this works around (voice processing failing when the mic and speakers are separate USB devices) can't happen there.

## Not on the Mac

- Arrange groups (custom buddy list group order).
- Quiet while gaming.
- **"Friends can't see your game" warning:** Windows reads Discord's Activity Privacy setting (`show_current_game` in READY `user_settings` and USER_SETTINGS_UPDATE). When it's off, Discord strips your game from what friends see even though Buzzcord sends it, so the header's "Playing X" line turns red with "(friends can't see it)", and clicking it PATCHes `/users/@me/settings` `show_current_game: true`. It also reads SESSIONS_REPLACE to log what Discord actually broadcasts (`presence <id>: sent [...] / Discord shows [...]` in buzzcord.log). `Session.DiscordSharesGames`, `Session.Broadcast`, BuddyListForm header.
- **Theme choice:** Options > Theme (Match Windows, Light, Dark). The Mac follows the system appearance only.
- Neither version has video calls or screen share. (This list used to say only the Mac lacked them; Windows doesn't have them either.)

## Windows since 2026-11-02

Nothing. The newest change in `src/` is from 2026-11-02 09:11 (Philippine time), the profile game Board, and the Mac port already has the Board, so the copy it was written from was current. The repo's `src/` (added in the `windows-source` PR) is the same code.

Windows features from before the copy that the Mac doesn't have, or does differently, are listed under "Not on the Mac" above. They were missing from this file before.

Later, Windows ported the Mac-only features (see "Ported from the Mac"), so those need nothing on the Mac side. One thing found while porting that the Mac should fix (**fixed 2026-11-08**: the Mac now spaces them 1.1 s apart per account too, `UnreadModel.markAllRead`): **Mark All as Read** in the Unread window calls `Session.markRead(_:)` in a loop, but `markRead` skips the ack to Discord when the previous one was under a second ago, so on each account only the first chat's read marker reaches Discord (threads aside, which use the unthrottled `markRead(channelId:upTo:)`; the others are read in Buzzcord only and come back as unread after a restart or on the phone). Windows spaces them 1.1 seconds apart (`UnreadForm.MarkAll`).

## Building and checking the Mac side

`cd mac && ./build.sh --install` builds a universal app. `swift test` runs 45 tests (MLS vectors, a full call against a fake voice server, game lists, rich presence socket, Auto-Away, devices, shortcut, unread, unfriend detection). Hidden checks are listed at the end of `mac/README.md`.
