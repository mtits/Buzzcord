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
- **Pasting and attaching files within Discord's limits (for 1.5.0, built on the Mac first; Windows from the same spec):**
  - **Limits table** (Mac: `UploadLimits` in `Uploads.swift`; keep the two identical). Checked 2026-11-10 on Discord's own pages: support.discord.com "File Attachments FAQ" (article 25444343291031: "As of August 2026, the free upload limit is 20MB (up from 10MB)", Nitro Basic 50MB, Nitro up to 1GB), "What are Nitro & Nitro Basic?" (115000435108: Nitro Classic 50MB), "Server Boosting FAQ" (360028038352: "50MB/100MB Upload Limit for all members (server only)"), and docs.discord.com/developers/reference "Uploading Files" (the limit "applies to each file in a request", given in MiB). Some older help pages ("Discord Account Caps", "How do I upload images and GIFs?") still say 10 MB / 500 MB; they predate the August 2026 change.

    | Who / where | Limit per file | API field |
    |---|---|---|
    | No Nitro | 20 MB | READY `user.premium_type` 0 |
    | Nitro Classic | 50 MB | `premium_type` 1 |
    | Nitro | 1 GB (1024 MB) | `premium_type` 2 |
    | Nitro Basic | 50 MB | `premium_type` 3 |
    | Server boost Level 0 / 1 | nothing extra | guild `premium_tier` 0 / 1 |
    | Server boost Level 2 | 50 MB for everyone in it | `premium_tier` 2 |
    | Server boost Level 3 | 100 MB for everyone in it | `premium_tier` 3 |
    | Files per message | 10 | Discord's own app; not stated in the help center |

    MB means MiB (1 MB = 1,048,576 bytes; 20 MB = 20,971,520), as in Discord's developer docs. The limit is per file, not per message.
  - **Which limit applies:** in a server channel (and its threads) the larger of the account's limit and the server's boost limit; in DMs and group chats the account's limit only. `premium_type` comes from the READY `user` object and USER_UPDATE (treat missing or unknown as 0). `premium_tier` comes from each READY guild's `properties` object (user-account gateway) or the flat guild in GUILD_CREATE / GUILD_UPDATE (missing = 0). Mac: `User.premiumType`, `Guild.premiumTier`, `Session.uploadContext(_:)`.
  - **Checked when attaching** (Send File, drag and drop, paste), never worked around: a file over the limit isn't attached; past 10 files the rest aren't attached; a folder isn't attached. Nothing is split, compressed or re-encoded. Checked again on Send in case the limit dropped. Discord also runs experiments nothing in the API shows (a 250 MB server add-on, larger limits for some users), so someone in one of those is held to the normal table.
  - **Wording** (Mac `Uploads.limitSentence`, `tooBig`, `tooMany`, `refused`; shown in red under the tray with a beep). `<size>` uses "84 MB", "9.6 MB" (one decimal under 100 MB, rounded up in these messages so 20.01 MB reads "20.1 MB"), "820 KB", "1 GB". Then "You can send up to L" + where it comes from + the higher limits, each listed only if bigger than L and only those that apply here (boosts only in a server; "50-100 MB in a boosted server" below Level 2, "100 MB if this server reaches boost Level 3" when only Level 3 is higher), joined "A or B" / "A, B, or C", or ", the most Discord allows." if none:
    - No Nitro, DM: "clip.mov is 84 MB. You can send up to 20 MB here (50 MB with Nitro Basic or 1 GB with Nitro)."
    - No Nitro, unboosted server: "clip.mov is 84 MB. You can send up to 20 MB here (50 MB with Nitro Basic, 1 GB with Nitro, or 50-100 MB in a boosted server)."
    - No Nitro, Level 2: "clip.mov is 84 MB. You can send up to 50 MB in this server, thanks to its Level 2 boost (1 GB with Nitro or 100 MB if this server reaches boost Level 3)."
    - No Nitro, Level 3, 150 MB file: "clip.mov is 150 MB. You can send up to 100 MB in this server, thanks to its Level 3 boost (1 GB with Nitro)."
    - Nitro Basic, DM: "clip.mov is 84 MB. You can send up to 50 MB with Nitro Basic (1 GB with Nitro)." (Nitro Classic: "with Nitro Classic".)
    - Nitro Basic, unboosted server: "... up to 50 MB with Nitro Basic (1 GB with Nitro or 100 MB if this server reaches boost Level 3)."
    - Nitro: "movie.mkv is 2 GB. You can send up to 1 GB with Nitro, the most Discord allows."
    - Folder: "Photos is a folder. Zip it first to send it."
    - Count: "Discord allows 10 files per message, so p3.png and p4.png weren't attached. Send these first, then add the rest." (one name: "so p3.png wasn't attached"; three or more: "so p3.png and 2 more weren't attached").
    - Discord refuses anyway (HTTP 413, or error code 40005 or 50045, from the attachment slot request or the upload): "Discord refused the file as too big, so nothing was sent. " + the same limit sentence. Not retried; the files stay in the tray. The generic error text no longer says "10 MB".
  - **Paste:** file URLs (Finder) attach as those files. Otherwise copied text wins when it's real text, or when rich text (RTF) came with it (Notes, Excel and Pages put a picture of the text alongside). Text that is only a link (a browser's Copy Image puts the picture's address there) doesn't count. Then each pasteboard item's data is saved in its own format, best first: GIF, WebP, JPEG, PNG, MP4, MOV; TIFF is converted to PNG (Discord doesn't show TIFF); anything else an image can be made from (PDF) becomes PNG as before, only when there's no text. Files are named `image-yyyyMMdd-HHmmss.gif` / `video-....mp4` (`-2`, `-3` for more than one) in the temp folder. Windows equivalents: `CF_HDROP` = file URLs; a browser's `HTML Format` + PNG/GIF data; "GIF" / "PNG" / "image/gif" clipboard formats before `Clipboard.GetImage()` (which loses animation). Mac: `PastePick`.
  - **Tray** above the message box, replacing the "Attached: names" banner: one chip per file with a thumbnail (pictures and videos) or the file's icon, the name, the size and an x; a line "N files, total. Up to L each here. Return sends, Esc removes all."; an orange "name is close to the L limit here." at 90% of the limit, red "name is over the L limit here. Remove it to send." if the limit dropped; while sending, a progress bar and "Uploading... 45%" (the status line says "Uploading clip.mov (1/2)... 45%"). Esc clears the tray and the note. Mac: `AttachmentTray.swift`, `ChatWindowController.addFiles/removeFile/send`.
  - Mac only: pictures dragged from Photos, Mail or a browser arrive as file promises and are attached too (`DroppedFiles`). `--demo --attach` shows the tray with samples.
- **Mentions & notifications read like a chat log (for 1.5.0):** both tabs show the newest at the bottom and open scrolled there.
  - **Mentions:** Discord's inbox (`GET /users/@me/mentions?limit=25&roles=true&everyone=true&before=<oldest id>`) still answers newest first, so each page is drawn in reverse. The first page is appended and lands at the bottom. "Show older mentions" sits at the top (the page's `#older` slot, set before the first page goes in so the bottom stays exact), and scrolling to the top clicks it, as in IM windows. The next older page is put in above with `prepend`, keeping your place. When Discord sends fewer than 25, the slot says "No older mentions." (nothing on a single short first page). Status: "Mentions from Discord's inbox, newest at the bottom."
  - **Notification history:** stored and saved exactly as before (newest first, last 300), only drawn reversed. A new alert while the tab is open is appended at the bottom, highlighted while unseen, instead of redrawing the list. That also fixes the old check on the entry count, which stopped noticing new alerts once the list reached 300. Status: "N alerts, newest at the bottom." Clear history redraws.
  - **Scrolled up:** a new entry doesn't pull you down; the page only follows the bottom when you're already there (the same rule as IM windows).
  - Search Messages results are unchanged.
  - Mac: `AlertsWindowController.historyHtml/mentionsHtml/historyChanged` in `Panes.swift`; `--demo --alerts` has 40 of each and a new alert after 20 s.
  - Windows: `src/AlertsForm.cs` still lists newest first. `LoadMentions` appends each page plus a "more" div at the end, `ShowHistory` iterates newest first, and both status lines say "newest first". Flip them the same way: reverse each page, use `prepend` and `setOlder` from the page script, and append new history entries instead of redrawing.
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

`cd mac && ./build.sh --install` builds a universal app. `swift test` runs the tests (MLS vectors, a full call against a fake voice server, game lists, rich presence socket, Auto-Away, devices, shortcut, unread, unfriend detection, upload limits and paste). Hidden checks are listed at the end of `mac/README.md`.
