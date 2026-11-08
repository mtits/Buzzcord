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

## Mac only (candidates to bring to Windows)

- **Auto-Away:** Away after N idle minutes (Options > Auto-Away: off, 5, 10, 15, 30) or when the screen locks; back on input. Shows Away with `afk: true` without changing the saved status; Busy and Invisible untouched. `AutoAway.swift`, `Session.shownStatus`.
- **Reply from the notification banner** and **Mark as Read**; in a server channel the reply references the alerted message. `Support.swift` (Notifier), `AccountController.replyFromBanner`.
- **Global shortcut** (default Option+Command+B, changeable, can be turned off) that brings every buddy list forward. `Shortcut.swift`.
- **Unread Messages window** (Shift+Command+U, or from the menu bar icon): lists exactly what the unread count counts. DMs Discord has no read marker for no longer show a red (1). `Unread.swift`.
- **Menu Bar Only:** hide the Dock icon. Windows already lives in the tray, so probably nothing to do.
- **"Don't Share" for one game** and Share Hidden Games Again. Windows has no per-game hiding, only the per-account switch and Detect running games.
- **Echo cancellation fallback:** tries voice processing on the chosen pair, checks that sound really moves, else runs mic and speakers separately, else falls back to the system default; switches over when devices are plugged in or out mid-call. `VoiceAudio.swift`.

## Not on the Mac

- Arrange groups (custom buddy list group order).
- Quiet while gaming.
- **"Friends can't see your game" warning:** Windows reads Discord's Activity Privacy setting (`show_current_game` in READY `user_settings` and USER_SETTINGS_UPDATE). When it's off, Discord strips your game from what friends see even though Buzzcord sends it, so the header's "Playing X" line turns red with "(friends can't see it)", and clicking it PATCHes `/users/@me/settings` `show_current_game: true`. It also reads SESSIONS_REPLACE to log what Discord actually broadcasts (`presence <id>: sent [...] / Discord shows [...]` in buzzcord.log). `Session.DiscordSharesGames`, `Session.Broadcast`, BuddyListForm header.
- **Theme choice:** Options > Theme (Match Windows, Light, Dark). The Mac follows the system appearance only.
- Neither version has video calls or screen share. (This list used to say only the Mac lacked them; Windows doesn't have them either.)

## Windows since 2026-11-02

Nothing. The newest change in `src/` is from 2026-11-02 09:11 (Philippine time), the profile game Board, and the Mac port already has the Board, so the copy it was written from was current. The repo's `src/` (added in the `windows-source` PR) is the same code.

Windows features from before the copy that the Mac doesn't have, or does differently, are listed under "Not on the Mac" above. They were missing from this file before.

## Building and checking the Mac side

`cd mac && ./build.sh --install` builds a universal app. `swift test` runs 34 tests (MLS vectors, a full call against a fake voice server, game lists, rich presence socket, Auto-Away, devices, shortcut, unread, unfriend detection). Hidden checks are listed at the end of `mac/README.md`.
