# Mac and Windows: what each has

For comparing the Mac port (`mac/`, Swift) with the Windows version (`src/`, C#). The Mac port
was written from a copy of `src/` dated 2026-11-02, so anything added on Windows after that is
missing here. Fill in the "Windows since 2026-11-02" list and the Mac side gets ported.

## Ported from Windows (should match)

- Sign-in: QR code and token, several accounts, tokens in the keychain (Windows: DPAPI).
- Buddy list: Favorites, Recent, Groups, Friends, Offline, Servers, Requests; search, Add a Friend, status and status message. No group reordering yet (Windows: Options > Arrange groups).
- IM windows: history, pictures, GIFs, embeds, replies, reactions, edit, delete, typing, formatting, emoji, Send File, drag and drop, paste screenshots, BUZZ.
- Notifications: DMs, mentions, followed channels, friend requests, sign-ins; mutes respected; Busy silences; pop-up IM windows; Dock badge (Windows: tray and taskbar badge).
- Auto-save of files and the chat log, member sidebar, message search, Mentions & Notifications history.
- Voice calls with DAVE (MLS checked against the IETF vectors in `testdata/mls`), voice activity and push-to-talk, per-user volume, ringing window.
- Profiles with the game Board (Favorite, Rotation, Games I Like, Want to Play), game picker.
- Rich presence over `discord-ipc-N` (Mac: a Unix socket in `$TMPDIR`), game detection, "Don't share", per-account sharing.
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
- **Echo cancellation fallback:** tries voice processing on the chosen pair, checks that sound really moves, else runs mic and speakers separately, else falls back to the system default; switches over when devices are plugged in or out mid-call. `VoiceAudio.swift`.

## Not on the Mac

- Video and screen share (left out on purpose to keep memory low).
- Arrange groups (custom buddy list group order).
- Quiet while gaming.

## Windows since 2026-11-02

(Windows Claude: list what was added or changed on Windows after this date.)

## Building and checking the Mac side

`cd mac && ./build.sh --install` builds a universal app. `swift test` runs 33 tests (MLS vectors, a full call against a fake voice server, game lists, rich presence socket, Auto-Away, devices, shortcut, unread). Hidden checks are listed at the end of `mac/README.md`.
