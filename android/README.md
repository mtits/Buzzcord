# Buzzcord for Android

The Android port of Buzzcord, built around one thing: **several Discord accounts in one light app,
switched instantly**. No app clones, no second copy of Discord, no reconnect when you switch.

## How the switching works

- Every account is signed in at once inside one process. Each one costs a single websocket
  (zlib-compressed), not a second app.
- **Tap** an account picture in the purple bar, or **swipe** the buddy list sideways, to switch.
  It's instant because nothing reconnects.
- Each picture shows that account's unread count and status. The envelope icon shows unread
  chats on **every** account in one list.
- **Long-press** an account picture to pause it (disconnects it: no data, no battery), move it, or
  sign out.
- Notifications say which account they came in on, and **Reply** from the notification sends as
  that account. Chats always show "as <account>" under the title, so you never post from the
  wrong one.
- Long-press the Buzzcord launcher icon for a shortcut straight into each account.

## Signing in

- **Log in**: Discord's own login page inside the app (works with 2FA and captchas). Buzzcord keeps
  only the token, encrypted with a key in the Android Keystore, in storage that's never backed up.
- **QR code**: scan with the Discord app on another device.
- **Token**: paste one.

## What's in v0.1

Buddy list (recent chats, friends online/offline, friend requests, servers and their channels),
search, status and status message, add a friend, chats with pictures, GIFs, embeds, replies,
edit, delete, typing, file upload, BUZZ! (vibrates), mention/mute rules matching the desktop
versions, mark as read synced with Discord.

Not yet: voice calls, reactions (shown, not added), member lists, rich presence.

## Building

GitHub Actions builds the APK on every push that touches `android/` (Actions > Android >
the run > Buzzcord-android artifact). Locally: open `android/` in Android Studio, or run
`./gradlew assembleRelease` with JDK 17 and the Android SDK. The release APK is signed with the
debug key for sideloading.

Needs Android 8.0 or newer. Uses OkHttp, Coil, ZXing and Jetpack Compose; JSON is Android's
built-in org.json.

> Buzzcord is unofficial and not made or endorsed by Discord Inc. Discord's Terms of Service don't
> allow third-party clients on user accounts; use it at your own risk.
