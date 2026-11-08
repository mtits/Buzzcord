import AppKit
import AVFoundation
import Combine

// The one call Buzzcord is in, across all accounts (there's only one mic). Joining another
// call, on any account, leaves this one first.
@MainActor
final class VoiceManager: ObservableObject {
    static let shared = VoiceManager()
    @Published private(set) var current: VoiceCall?

    func isIn(_ s: Session, _ channelId: String) -> Bool { current?.session === s && current?.channel.id == channelId }

    // Joins a server voice channel, or a DM/group call (ringing the others if nobody's in it yet).
    func join(_ account: AccountController, _ channel: Channel) {
        if isIn(account.session, channel.id) { return }
        current?.leave()
        Task {
            guard await VoiceManager.micAllowed() else {
                showError("Buzzcord needs the microphone for calls. Allow it in System Settings > Privacy & Security > Microphone, then try again.")
                return
            }
            let call = VoiceCall(account: account, channel: channel)
            current = call
            call.connect()
        }
    }

    func leave() { current?.leave() }

    func ended(_ call: VoiceCall) {
        if current === call { current = nil }
    }

    static func micAllowed() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    // MARK: settings

    static var pushToTalk: Bool { Prefs.bool("VoicePushToTalk", false) }
    static var pushToTalkKey: String { Prefs.string("VoicePttKey") ?? "option" }
    static var sensitivity: Float { Float((Prefs.d.object(forKey: "VoiceSensitivity") as? NSNumber)?.doubleValue ?? -50) }
    static var gainPercent: Int { (Prefs.d.object(forKey: "VoiceGain") as? NSNumber)?.intValue ?? 100 }
    static var joinMuted: Bool { Prefs.bool("VoiceJoinMuted", false) }

    static let pttKeys: [(String, String, NSEvent.ModifierFlags)] = [
        ("option", "Option (⌥)", .option), ("control", "Control (⌃)", .control), ("fn", "Fn / Globe", .function), ("shift", "Shift (⇧)", .shift),
    ]

    static func apply(_ audio: AudioEngine) {
        audio.usePushToTalk = pushToTalk
        audio.pushToTalkKey = pttKeys.first { $0.0 == pushToTalkKey }?.2 ?? .option
        audio.sensitivityDb = sensitivity
        audio.inputGain = Float(gainPercent) / 100
    }

    static func sound(_ name: String) { Notifier.play(name) }
}

@MainActor
final class VoiceCall: ObservableObject {
    let session: Session
    let channel: Channel
    weak var account: AccountController?
    @Published var state = "Connecting..."
    @Published var error: String?
    @Published var pingMs = -1
    @Published var muted: Bool
    @Published var deafened = false
    @Published var meTalking = false
    @Published private(set) var tick = 0   // speaking indicators and DAVE state

    private var speaking: Set<UInt64> = []
    private var conn: VoiceConnection?
    private var audio: AudioEngine?
    private var ended = false
    private var poll: Timer?

    init(account: AccountController, channel: Channel) {
        self.account = account
        session = account.session
        self.channel = channel
        muted = VoiceManager.joinMuted
    }

    var guildId: String? { channel.guildId }
    var connected: Bool { (conn?.isReady ?? false) && !ended }
    var encrypted: Bool { conn.map { $0.dave.protocolVersion > 0 && $0.dave.established } ?? false }
    // Connected, but the end-to-end group isn't set up yet: no one can hear anyone until it is.
    var securing: Bool { connected && (conn.map { $0.dave.protocolVersion > 0 && !$0.dave.established } ?? false) }
    var privacyCode: String? { conn?.dave.privacyCode }

    var title: String {
        if channel.isPrivate { return "Call with " + session.title(channel) }
        return "#" + (channel.name ?? "") + (session.guild(of: channel).map { " — " + $0.name } ?? "")
    }

    // Everyone in the call, us included.
    var participants: [String] {
        var list = session.voiceMembers(channel.id).sorted { session.name($0).localizedCaseInsensitiveCompare(session.name($1)) == .orderedAscending }
        if let me = session.me, !list.contains(me.id), !ended { list.insert(me.id, at: 0) }
        return list
    }

    func isSpeaking(_ userId: String) -> Bool {
        if userId == session.me?.id { return meTalking && !muted }
        guard let u = UInt64(userId) else { return false }
        return speaking.contains(u) || (audio?.isPlaying(u) ?? false)
    }

    func connect() {
        Task {
            do {
                let startCall = channel.isPrivate && session.voiceMembers(channel.id).isEmpty
                let server = try await session.joinVoice(guildId: guildId, channelId: channel.id, selfMute: muted, selfDeaf: deafened)
                if ended { session.setVoice(guildId: guildId, channelId: nil, selfMute: false, selfDeaf: false); return }
                if startCall { try? await session.ringCall(channel.id) }
                guard let me = session.me, let uid = UInt64(me.id), let cid = UInt64(channel.id) else { return }
                let c = VoiceConnection(endpoint: server.endpoint, serverId: server.serverId, userId: uid, channelId: cid, sessionId: server.sessionId, token: server.token)
                let a = AudioEngine()
                a.muted = muted
                a.deafened = deafened
                VoiceManager.apply(a)
                for id in session.voiceMembers(channel.id) {
                    if let u = UInt64(id) { a.setVolume(u, Float((Prefs.d.object(forKey: "VoiceVolume." + id) as? NSNumber)?.intValue ?? 100) / 100) }
                }
                a.onFrame = { [weak c] f in c?.sendOpus(f) }
                a.onTransmitting = { [weak self, weak c] t in
                    c?.setSpeaking(t)
                    DispatchQueue.main.async { self?.meTalking = t }
                }
                c.onOpus = { [weak a] u, seq, f in a?.receive(u, seq, f) }
                c.onReady = { [weak self] in DispatchQueue.main.async { self?.ready() } }
                c.onState = { [weak self] st in DispatchQueue.main.async { if let self, !self.ended, st != "Connected" { self.state = st } } }
                c.onClosed = { [weak self] code in DispatchQueue.main.async { self?.end(code == 4014 ? "Disconnected (removed from the call)" : "Disconnected") } }
                c.onPing = { [weak self] ms in DispatchQueue.main.async { self?.pingMs = ms } }
                c.onSpeaking = { [weak self] u, on in DispatchQueue.main.async { if on { self?.speaking.insert(u) } else { self?.speaking.remove(u) }; self?.tick += 1 } }
                c.onUserLeft = { [weak self, weak a] u in a?.removeUser(u); DispatchQueue.main.async { self?.speaking.remove(u); self?.tick += 1 } }
                c.dave.onStateChanged = { [weak self] in DispatchQueue.main.async { self?.tick += 1 } }
                conn = c
                audio = a
                c.start()
            } catch {
                Log.write("voice join: \(error.localizedDescription)")
                end("Couldn't join: " + error.localizedDescription)
            }
        }
    }

    private func ready() {
        guard !ended else { return }
        state = "Voice connected"
        audio?.onRestart = { [weak self] in self?.describeAudio() }
        do {
            try audio?.start()
            describeAudio()
        } catch {
            self.error = error.localizedDescription
            Log.write("audio: \(error.localizedDescription)")
        }
        VoiceManager.sound("Tink")
        // Speaking rings come from the audio as well as op 5; refresh them a few times a second.
        poll = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick += 1 }
        }
    }

    func setMuted(_ m: Bool) {
        muted = m
        if !m && deafened { deafened = false }   // unmuting also undeafens, as in Discord
        update()
    }

    func setDeafened(_ d: Bool) {
        deafened = d
        if d { muted = true } else { muted = VoiceManager.joinMuted && muted }
        update()
    }

    private func update() {
        audio?.muted = muted
        audio?.deafened = deafened
        if !ended { session.setVoice(guildId: guildId, channelId: channel.id, selfMute: muted, selfDeaf: deafened) }
    }

    func setVolume(_ userId: String, _ percent: Int) {
        Prefs.set("VoiceVolume." + userId, percent)
        if let u = UInt64(userId) { audio?.setVolume(u, Float(percent) / 100) }
    }

    func volume(_ userId: String) -> Int { (Prefs.d.object(forKey: "VoiceVolume." + userId) as? NSNumber)?.intValue ?? 100 }

    // Settings changed while in the call.
    func applySettings() {
        guard let audio else { return }
        VoiceManager.apply(audio)
    }

    // A different mic or speakers chosen in Voice Settings.
    func devicesChanged() { audio?.restart() }

    // Which devices the call is on, and anything that had to fall back.
    @Published private(set) var audioInfo = ""

    private func describeAudio() {
        guard let a = audio, !ended else { return }
        state = a.echoCancellation ? "Voice connected" : "Voice connected (no echo cancellation: use headphones)"
        audioInfo = [a.usingInput?.name, a.usingOutput?.name].compactMap { $0 }.joined(separator: " → ")
            + (a.echoCancellation ? ", echo cancellation on" : ", echo cancellation off")
        if !a.note.isEmpty { self.error = nil; state = a.note }
    }

    func leave() {
        if ended { return }
        session.setVoice(guildId: guildId, channelId: nil, selfMute: false, selfDeaf: false)
        end("Disconnected")
    }

    private func end(_ why: String) {
        if ended { return }
        ended = true
        state = why
        poll?.invalidate()
        conn?.stop()
        audio?.stop()
        if why.hasPrefix("Couldn't") { error = why; showError(why, title: "Voice") }
        VoiceManager.sound("Bottle")
        VoiceManager.shared.ended(self)
    }
}
