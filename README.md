# Buzzcord

A lightweight, old-school (Yahoo Messenger-style) messenger for Discord. Plain Windows controls, no Electron.
Unofficial: Discord doesn't allow third-party clients for user accounts, so using it carries some risk to the accounts you sign in with.

This is the Windows version (`src/`, C# and WinForms). The macOS port is in `mac/` (see `mac/README.md`), and `mac/PARITY.md` lists what each version has.

## Run

```
powershell -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File run.ps1
```

Smart App Control blocks the unsigned exe, so `run.ps1` loads it into PowerShell instead. On first start Buzzcord makes Start menu and desktop shortcuts (they launch through `conhost --headless`, so no console window flashes, and the taskbar shows Buzzcord, pinnable) and turns on Start with Windows (Options menu to turn off).
Once the exe is code-signed (`build.ps1 -Sign`, same certificate setup as OLED Saver), the shortcuts and startup entry switch to running `bin\Buzzcord.exe` directly.

## Features

- Sign in by QR code (Discord phone app > profile picture > Scan QR Code) or by pasting a token. Several accounts at once, one buddy list window each. Tokens are stored encrypted with Windows DPAPI in `%APPDATA%\Buzzcord\accounts.dat`.
- Friends (online first, with what they're playing), friend requests (accept, ignore, cancel), recent DMs, group chats, servers and their text channels.
- Add a Friend by username (bottom toolbar, Ctrl+N, or from someone's profile). Search box over the list (Ctrl+F) filters friends, chats, servers and channels; Ctrl+F in a chat window searches its whole history with Discord's search.
- Profiles: right-click > View profile, or click a name or avatar. Opening one saves their full-size picture and banner to `Documents\Buzzcord Saved\<account>\Profile pictures`.
- An IM window per conversation: formatting buttons, smileys, server emoji, replies, edit (Up arrow), delete, typing indicator, BUZZ! (shakes the other person's window if they use Buzzcord too).
- Pictures, GIFs and videos (MP4) play inside the chat window. Send files with Send File, drag and drop, or paste a screenshot.
- Files sent in DMs and group chats are saved automatically to `Documents\Buzzcord Saved\<account>\<chat>` (Options menu to turn off or change the folder).
- Rich presence: Buzzcord listens on `\\.\pipe\discord-ipc-0` like the Discord app does, so games send their rich presence to Buzzcord, and it goes out on every signed-in account. It also shows "Playing X" for games it finds running, by comparing process names with Discord's public game list (no game process is ever opened). Quit the official Discord app so games find Buzzcord first; Help > Rich presence status shows what's happening.
- Friends' rich presence (game art, details, party size, elapsed time) appears under the buddy list and beside DM windows.

- Captchas: when Discord asks for one (adding friends, messaging someone new...), Buzzcord shows it in a small window and sends the request once it's solved. That window uses Edge WebView2 (built into Windows 11); Microsoft's WebView2 libraries are in `lib\` and get copied next to the exe.

- Themes: Options > Theme (Match Windows, the default; Light, the classic look; or Dark). Windows switches live, title bars included.
- Favorites: right-click any chat or server channel > Pin to Favorites. "Notify me about every message" follows a channel (a dot marks followed ones).
- Mentions & notifications (bottom toolbar or Ctrl+M): Discord's own mentions inbox, plus a saved history of every alert Buzzcord showed (messages, mentions, friend requests, sign-ins, followed channels). Clicking a tray notification opens its chat.
- Quiet rules: Busy (dnd) silences pop-ups, sounds and balloons. Quiet while gaming (on by default) means no pop-up windows or sounds while a detected game runs, just the tray balloon. Discord's own mute settings are respected.
- Group order: Favorites, Recent chats, Group chats, Friends, Offline friends, Servers, Friend requests by default; Options > Arrange groups to change it.
- Member sidebar: server channels show Discord's member list (role groups, online/offline, role colours, what people are playing); group chats list their members, online first. Click someone for their profile; View > Show members hides it.
- Unfriend alerts: when someone removes (or blocks) you, it's shown and kept in the notification history.
- Friend sign-in alerts with a door chime (Options to turn off). Unread count on the tray icon and a badge on the taskbar button.
- Message archive: DM and group chat text is appended to `Chat log.txt` beside the auto-saved files.
- Reactions: click a reaction to add or remove yours, or use "react" on a message for the smiley picker.
- Help > Donate (PayPal) and About.
- Voice calls (no video):
  - **Server voice channels** appear in the buddy list with the people in them. Double-click one to join; whoever's talking is outlined in green.
  - **DMs and group chats:** the Call button in the chat window rings them. Incoming calls pop up a ringing window with Answer and Ignore, and missed calls go into the notification history.
  - **The voice bar** at the bottom of the buddy list shows who's there, with Mute, Deafen and Leave. Right-click someone to change their volume.
  - **Options > Voice settings:** devices, voice activity (sensitivity with a live mic meter) or push-to-talk, mic volume, and "join muted". Push-to-talk only reads the key's state, with no keyboard hook.
  - **One call at a time** across all accounts.
  - **Encryption:** calls are end-to-end encrypted with Discord's DAVE protocol (MLS). The lock icon on the voice bar shows the voice privacy code, which matches the one in the official app. The code is in `src\Voice\`, checked against the MLS working group's test vectors in `testdata\mls`.
  - **Audio:** Opus comes from Concentus (`lib\Concentus.dll`, Opus license), loaded from memory.
  - **No echo cancellation yet**, so use headphones.

Not included: video calls and screen share, threads and forum channels, Nitro extras.

## Build and test

```
powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Live] [-Ui] [-Out <path>]
```

`-Test` runs the offline self-test, `-Live` adds checks against Discord that don't sign in (gateway handshake, QR login code), and `-Ui` renders the windows with made-up data into `bin\ui\*.png`.
The log is `%APPDATA%\Buzzcord\buzzcord.log`.
