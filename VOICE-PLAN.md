# Buzzcord voice: plan (voice only, no video)

**Status (2026-11-01):** phases 0–3 are done and pass all their self-tests:
- the official MLS test vectors in `testdata\mls`;
- a full call against a fake voice server on localhost;
- the call manager's join path.

The UI is the voice bar, voice channels in the buddy list, Call buttons, the incoming-call window and Voice settings.

**Live-verified 2026-11-02:** a DM call with Discord's web app is end-to-end encrypted with DAVE. Discord's libdave accepted Buzzcord's Welcome ("Successfully welcomed to MLS Group"), and audio flowed both ways with every sent frame encrypted. The fix that got there: op 26 carries a bare KeyPackage, not an MLSMessage.

## Context
The user doesn't need voice themselves, but wants Buzzcord ready in case its users ask for it. Discord has required end-to-end encryption for calls (the **DAVE** protocol) since **March 1, 2026**. A client without it can't join any call. So this plan is mostly about cryptography, with an audio client on top.

Scope:
- DM calls, group DM calls and server voice channels.
- Talking and listening: voice activity or push-to-talk, mute, deafen, per-user volume.

Out of scope: video, screen share, Go Live, stage channels, and soundboard.

The constraints that shape this plan:
- **The toolchain:** C# 5 with the built-in .NET Framework 4.8 `csc`.
- **No native DLLs.** Smart App Control blocks unsigned ones, and there's no C++ compiler. Everything must be managed code or a Windows API.
- **Vanguard-safe.** No keyboard hooks or injection. Push-to-talk polls the key state.
- **One live call app-wide.** There's one mic. Joining a call on one account first leaves any call on the other.

## Size and order
This adds roughly 7–8k lines, about the size of Buzzcord today (9.2k). It is built in four phases. Each phase ends in offline self-tests, and nothing reaches the UI until phase 3. Phases 0–1 are the risky part, and they are tested against the official MLS and HPKE test vectors before any live call.

## Phase 0: crypto foundation (`src/Voice/Crypto.cs`, `src/Voice/Tls.cs`)
- **AES-GCM, our own.** .NET 4.8 has no `AesGcm`, and Windows CNG GCM rejects the 8-byte tags DAVE uses. So: AES in ECB mode (`AesCryptoServiceProvider`) plus GHASH in managed code. It supports any key size (128 or 256) and any tag length. Audio volume is about 50 frames a second per speaker, so speed is fine.
- **HKDF-SHA256** (RFC 5869) on top of `HMACSHA256`.
- **P-256 ECDH** through CNG P/Invoke (`BCryptSecretAgreement` + `BCryptDeriveKey` with `BCRYPT_KDF_RAW_SECRET`, reversing CNG's little-endian output), because .NET 4.8's `ECDiffieHellmanCng` won't give the raw secret. Key import and export use `ECCPUBLICBLOB` and `ECCPRIVATEBLOB`.
- **ECDSA P-256** through `ECDsaCng`, converting between CNG's r‖s and the DER form MLS uses.
- **HPKE** (RFC 9180) in base mode with DHKEM(P-256, HKDF-SHA256) and AES-128-GCM. This is what MLS uses to encrypt path secrets and Welcomes.
- **TLS presentation-language codec:** varint length prefixes, vectors and optionals, for every MLS struct.

## Phase 1: MLS subset and DAVE session (`src/Voice/Mls.cs`, `src/Voice/Dave.cs`)
**MLS 1.0, ciphersuite 2** (`DHKEMP256_AES128GCM_SHA256_P256`), only the parts DAVE uses:
- **Credentials:** basic credential, whose identity is the user id as a big-endian uint64.
- **Key packages:** lifetime 0 to 2^64−1, single-use, with a fresh one every time Buzzcord waits to be added.
- **Groups:**
  - Array-based ratchet tree: tree math, parent hash and tree hash.
  - Key schedule: joiner, welcome, epoch and exporter secrets, plus the confirmation tag.
  - Transcript hashes and the `GroupContext`.
  - The External Senders group extension, set to the voice gateway.
- **Joining** from a Welcome (op 30): decrypt with HPKE, then verify the GroupInfo signature and the tree.
- **Processing commits** (op 29): the proposals Add and Remove come only from the external sender, then decrypt the UpdatePath and advance the epoch.
- **Creating commits** (op 28) when Buzzcord is the one to commit: a commit with all unrevoked proposals from op 27, an UpdatePath, and a Welcome for every member added. This is needed because when both your accounts call each other, there's no official client to commit.
- **Framing:** handshake messages are PublicMessages. Commits from members carry a membership tag.

**DAVE on top of that:**
- **Opcodes:** JSON ops 21–24 and 31; binary ops 25–30, which are framed as 2-byte seq + 1-byte op + payload.
- **Transitions:** prepare, then ready (op 23), then execute (op 22). Epoch 1 means create a new group. On a bad commit or welcome: reset, send op 31, then a new key package.
- **Sender keys:** `MLS-Exporter("Discord Secure Frames v0", little-endian sender id, 16)`, then a per-sender hash ratchet (RFC 9420 §9.1). The generation is the top byte of the 32-bit nonce, and generations keep counting across nonce wrap. Old keys are kept 10 s after a transition. Each key/nonce pair is accepted only once.
- **Opus frames:** the whole frame is AES-128-GCM encrypted, followed by an 8-byte tag, a ULEB128 nonce, empty ranges, a supplemental size byte, and `0xFAFA`. In passthrough mode (during a downgrade or transition), non-DAVE frames and the `F8 FF FE` silence frame pass through as they are.
- **Privacy code:** the epoch authenticator shown as Discord's 30-digit code (6 groups of 5), so users can compare it with the lock icon in the official app. Per-user verification fingerprints (scrypt) are left out.

## Phase 2: voice transport and audio (`src/Voice/VoiceGateway.cs`, `src/Voice/Audio.cs`, `lib\Concentus.dll`)
**Main gateway, in `Session.cs`:**
- **Joining:** op 4 `{guild_id | null, channel_id, self_mute, self_deaf, self_video:false}`.
- **Connection details:** wait for our own `VOICE_STATE_UPDATE` (session_id) and `VOICE_SERVER_UPDATE` (token, endpoint), and don't cache them.
- **Voice states:** keep them per guild from READY `voice_states` and from `VOICE_STATE_UPDATE`.
- **Calls:** `CALL_CREATE`, `CALL_UPDATE` and `CALL_DELETE` for DM and group calls.
- **REST:** `POST /channels/{id}/call/ring` and `/call/stop-ringing`.

**Voice gateway, v8:**
- **Handshake:** `wss://<endpoint>?v=8`, Hello (op 8) then Identify (op 0, with `max_dave_protocol_version: 1`; `server_id` is the channel id for DMs), then Ready (op 2: ssrc, ip, port, modes).
- **Heartbeat:** op 3 `{t, seq_ack}`.
- **Resume:** op 7 / op 9, with buffered resume.
- **Joins and leaves:** op 11 clients-connect and op 13 disconnect. Op 5 speaking maps ssrc to user.

**UDP:**
- **IP discovery:** a 74-byte packet.
- **Protocol:** op 1 select protocol, mode `aead_aes256_gcm_rtpsize`; op 4 returns the 32-byte key and the DAVE version.
- **Packets:** a 12-byte RTP header (0x80, 0x78, seq, timestamp, ssrc) that is the AAD, then the encrypted payload, then a 4-byte nonce counter. The counter is placed in the first 4 bytes of the 12-byte GCM nonce.

**Opus:** Concentus, a managed C# port of libopus under the MIT license, using its .NET Standard 2.0 build. It's loaded from memory through the existing `AssemblyResolve` in `Program.cs`, the same way WebView2 is, so Smart App Control doesn't see a DLL load. Audio is 48 kHz stereo in 20 ms frames, and packet-loss concealment covers dropped packets.

**Audio, through winmm `waveIn`/`waveOut` P/Invoke:**
- **Device choice:** input and output devices.
- **Receiving:** a 60 ms jitter buffer per speaker, a mixer and per-user volume.
- **Voice activity:** detected by level, with a sensitivity setting and a 300 ms hang time. Five silence frames are sent when you stop talking.
- **Push-to-talk:** `GetAsyncKeyState` is polled every 20 ms. That only reads key state, with no hook, so it's Vanguard-safe.
- **Echo:** there is no echo cancellation in v1, and the Options page says headphones are recommended. Windows' built-in Voice Capture DSP (echo cancellation and noise suppression) could come later as phase 4.

## Phase 3: UI
- **Server voice channels in the buddy list:** they show under each server, with a speaker icon and the people in them as small indented rows. A smiley lights up while someone talks. Double-click joins.
- **Calls in chat windows:** DM and group chat windows get a **Call** toolbar button. Incoming calls open a YM-style "<name> is calling you" window with Answer and Ignore and a synthesized ring sound next to `Sounds.Ding`/`Door`. Busy (dnd) means no ring, only a history alert through `Notifier`.
- **Voice bar:** it docks at the bottom of the buddy list, like `ActivityCard`. It shows:
  - The channel name, connection state and ping.
  - The privacy code on hover.
  - Mute, Deafen and Disconnect buttons.
  - The participant list. Right-click a person to set their volume.
- **Options > Voice settings:**
  - Input and output device, input volume, and a live mic level meter for a mic test.
  - Voice activity or push-to-talk, with a key picker.
  - "Join calls muted".
- **Files:** `VoiceManager.cs` holds the app-wide single call and its state machine, with events on the UI thread. UI goes in `VoiceBar.cs` and `CallForm.cs`. `Models.cs` gets a `VoiceState` class and `Channel.IsVoice` (type 2; stage, type 13, is excluded).

## Reuse
- `Gateway.Send` for op 4, and `Gateway` itself as the model for the voice websocket loop.
- `Json`, the `Api.Post` helpers, `Session.Inject` for tests, `Notifier.Alert`, and the `Sounds` synthesis in `Native.cs`.
- `RowList`, `Theme.Attach`, `ActivityCard`'s docking, and `ImageCache.Scaled` for avatars.
- The `Program.cs` `AssemblyResolve` loading from bytes, and the `build.ps1` lib-copy step.

## Downloads needing your OK at build time
- The Concentus NuGet package from nuget.org.
- MLS test vectors from `github.com/mlswg/mls-implementations` and HPKE vectors from `github.com/cfrg/draft-irtf-cfrg-hpke`, saved under `Buzzcord\testdata`.

## Verification
1. **Offline self-tests in `build.ps1 -Test`:**
   - GCM against NIST vectors, including the 8-byte tag.
   - HKDF against RFC 5869 and HPKE against the RFC 9180 P-256 vectors.
   - MLS ciphersuite-2 vectors: tree-math, crypto-basics, key-schedule, tree-operations, treekem, transcript-hashes, welcome, passive-client-welcome and passive-client-handling-commit.
   - Two in-process DAVE sessions plus a fake external sender run a full join, commit and leave, then swap encrypted Opus frames both ways.
   - An Opus encode and decode loopback through the transport encryption.
2. **Live tests that only you can run, in order:**
   1. Main account calls the alt. Both are Buzzcord, so this tests Buzzcord creating and processing commits.
   2. A DM call with a friend on official Discord. This tests compatibility, and the privacy code should match their lock icon.
   3. A server voice channel with several people.
   4. Push-to-talk while Valorant is running.

   `buzzcord.log` records voice gateway ops, DAVE transitions and epochs. It never records keys or audio.
3. **Risks:**
   - A subtle MLS mismatch shows up as "can't hear anyone" or "kicked after join". The fix is to compare against the test vectors and the log.
   - An unofficial voice client is more unusual traffic than text, so the account risk is a bit higher.
