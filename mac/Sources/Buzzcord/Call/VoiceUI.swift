import AppKit
import CoreAudio
import SwiftUI

// The voice bar at the bottom of the buddy list: who's in the call (green ring = talking),
// Mute, Deafen and Leave, the lock with the voice privacy code, and the ping.
struct VoiceBarHost: View {
    @ObservedObject var manager = VoiceManager.shared
    let session: Session

    var body: some View {
        if let call = manager.current, call.session === session { VoiceBar(call: call) }
    }
}

struct VoiceBar: View {
    @ObservedObject var call: VoiceCall
    @State private var showCode = false

    var body: some View {
        let _ = call.tick
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Image(systemName: "speaker.wave.2.fill").foregroundStyle(call.connected ? Theme.activity : Theme.sub)
                VStack(alignment: .leading, spacing: 0) {
                    Text(call.title).font(.system(size: 12, weight: .bold)).lineLimit(1)
                    Text(stateLine).font(.system(size: 10)).foregroundStyle(call.error != nil ? Color.red : Theme.sub).lineLimit(1)
                        .help(call.audioInfo)
                }
                Spacer(minLength: 0)
                Button { showCode.toggle() } label: {
                    Image(systemName: call.encrypted ? "lock.fill" : "lock.open").foregroundStyle(call.encrypted ? Theme.activity : Theme.sub)
                }
                .buttonStyle(.borderless)
                .help(call.encrypted ? "End-to-end encrypted. Click for the voice privacy code." : "Not end-to-end encrypted yet")
                .popover(isPresented: $showCode) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Voice privacy code").font(.headline)
                        Text(call.privacyCode ?? "Not available yet").font(.system(size: 14, design: .monospaced)).textSelection(.enabled)
                        Text("Everyone in the call sees the same code when the call is end-to-end encrypted.").font(.system(size: 11)).foregroundStyle(.secondary)
                            .frame(width: 240, alignment: .leading)
                    }
                    .padding(12)
                }
            }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(call.participants, id: \.self) { id in
                        let talking = call.isSpeaking(id)
                        RemoteImage(url: call.session.user(id).avatarUrl(64), corner: 13)
                            .frame(width: 26, height: 26)
                            .overlay(Circle().stroke(talking ? Color.green : Color.clear, lineWidth: 2.5))
                            .help(call.session.name(id))
                            .contextMenu {
                                if id != call.session.me?.id {
                                    Text("Volume for \(call.session.name(id)): \(call.volume(id))%")
                                    ForEach([0, 25, 50, 75, 100, 150, 200], id: \.self) { v in
                                        Button("\(v)%") { call.setVolume(id, v) }
                                    }
                                }
                                Button("View Profile") { call.account?.showProfile(id, guildId: call.guildId) }
                            }
                    }
                }
                .padding(2)
            }
            HStack(spacing: 6) {
                Toggle(isOn: Binding(get: { call.muted }, set: { call.setMuted($0) })) { Label("Mute", systemImage: call.muted ? "mic.slash.fill" : "mic.fill") }
                    .toggleStyle(.button)
                Toggle(isOn: Binding(get: { call.deafened }, set: { call.setDeafened($0) })) { Label("Deafen", systemImage: call.deafened ? "speaker.slash.fill" : "headphones") }
                    .toggleStyle(.button)
                Spacer()
                Button(role: .destructive) { call.leave() } label: { Label("Leave", systemImage: "phone.down.fill") }
                    .tint(.red)
            }
            .controlSize(.small)
            .labelStyle(.titleAndIcon)
        }
        .padding(8)
        .background(Theme.group)
    }

    private var stateLine: String {
        if let e = call.error { return e }
        if call.securing { return "Securing the call..." }
        var s = call.state
        if call.connected && call.pingMs >= 0 { s += " · \(call.pingMs) ms" }
        if call.connected { s += call.encrypted ? " · encrypted" : "" }
        return s
    }
}

// Ringing: Answer joins the call, Ignore stops the ringing for us only.
@MainActor
final class IncomingCallWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: IncomingCallWindowController] = [:]
    private let key: String
    private var ring: NSSound?

    static func show(_ account: AccountController, _ channel: Channel) {
        let key = account.session.tokenKey + channel.id
        if open[key] != nil { return }
        let w = IncomingCallWindowController(account, channel, key)
        open[key] = w
        w.window?.orderFrontRegardless()
        NSApp.requestUserAttention(.criticalRequest)
    }

    static func dismiss(_ account: AccountController, _ channelId: String) {
        open[account.session.tokenKey + channelId]?.close()
    }

    private init(_ account: AccountController, _ channel: Channel, _ key: String) {
        self.key = key
        let w = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 300, height: 150), styleMask: [.titled, .closable, .nonactivatingPanel], backing: .buffered, defer: false)
        w.title = "Incoming call"
        w.level = .floating
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        let s = account.session
        let caller = channel.isDm ? channel.recipientIds.first : nil
        w.contentView = NSHostingView(rootView: VStack(spacing: 10) {
            HStack(spacing: 10) {
                RemoteImage(url: s.user(caller ?? channel.recipientIds.first).avatarUrl(128), corner: 24).frame(width: 48, height: 48)
                VStack(alignment: .leading) {
                    Text(s.title(channel)).font(.system(size: 14, weight: .bold)).lineLimit(1)
                    Text(channel.isGroup ? "Group call" : "is calling you...").font(.system(size: 12)).foregroundStyle(Theme.sub)
                }
                Spacer()
            }
            HStack {
                Button { [weak self] in
                    s.stopRinging(channel.id)
                    self?.close()
                } label: { Label("Ignore", systemImage: "xmark").frame(maxWidth: .infinity) }
                Button { [weak self] in
                    VoiceManager.shared.join(account, channel)
                    self?.close()
                } label: { Label("Answer", systemImage: "phone.fill").frame(maxWidth: .infinity) }
                .tint(.green).keyboardShortcut(.defaultAction)
            }
            .controlSize(.large)
        }
        .padding(14)
        .frame(width: 300)
        .background(Theme.header))
        if let vf = NSScreen.main?.visibleFrame { w.setFrameTopLeftPoint(NSPoint(x: vf.maxX - 320, y: vf.maxY - 20)) }
        if Prefs.bool("Sounds", true), account.session.status != "dnd" {
            ring = NSSound(named: "Submarine")
            ring?.loops = true
            ring?.play()
        }
        // Discord stops ringing after a while; so do we.
        DispatchQueue.main.asyncAfter(deadline: .now() + 45) { [weak self] in self?.close() }
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        ring?.stop()
        IncomingCallWindowController.open.removeValue(forKey: key)
    }
}

// Options > Voice Settings: voice activity (with a live mic meter) or push-to-talk, mic volume
// and "join muted". Devices follow the Mac's Sound settings.
@MainActor
final class VoiceSettingsWindowController: NSWindowController, NSWindowDelegate {
    static var shared: VoiceSettingsWindowController?
    private let meter = MeterModel()
    private var test: AudioEngine?

    static func show() {
        if shared == nil { shared = VoiceSettingsWindowController() }
        shared?.showWindow(nil)
        shared?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private init() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 470), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "Voice Settings"
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: VoiceSettingsView(meter: meter) { [weak self] in self?.devicesChanged() })
        w.center()
        startMeter()
    }

    required init?(coder: NSCoder) { fatalError() }

    // The meter uses the live call's mic when there is one, otherwise a short-lived capture.
    private func startMeter() {
        guard VoiceManager.shared.current == nil else { meter.note = "In a call: changes apply right away."; return }
        Task {
            guard await VoiceManager.micAllowed() else { meter.note = "Microphone access is off for Buzzcord (System Settings > Privacy & Security > Microphone)."; return }
            let a = AudioEngine()
            a.muted = true
            a.onLevel = { [weak self] db in DispatchQueue.main.async { self?.meter.level = db } }
            do { try a.start(captureOnly: true); test = a } catch { meter.note = error.localizedDescription }
        }
    }

    // A different mic or speakers: the call (or the meter) switches right away.
    private func devicesChanged() {
        if let call = VoiceManager.shared.current { call.devicesChanged() } else { test?.restart() }
    }

    func windowWillClose(_ notification: Notification) {
        test?.stop()
        test = nil
        VoiceSettingsWindowController.shared = nil
    }
}

final class MeterModel: ObservableObject {
    @Published var level: Float = -90
    @Published var note = ""
}

// The Mac's audio devices, kept current as things are plugged in and out.
@MainActor
final class DeviceListModel: ObservableObject {
    @Published private(set) var devices = AudioDevices.all()
    @Published private(set) var defaultInput = AudioDevices.defaultDevice(input: true)
    @Published private(set) var defaultOutput = AudioDevices.defaultDevice(input: false)

    private var watch: AudioDevices.Watch?

    init() {
        watch = AudioDevices.Watch { [weak self] in MainActor.assumeIsolated { self?.refresh() } }
    }

    func refresh() {
        devices = AudioDevices.all()
        defaultInput = AudioDevices.defaultDevice(input: true)
        defaultOutput = AudioDevices.defaultDevice(input: false)
    }

    func name(_ id: AudioDeviceID?) -> String { devices.first { $0.id == id }?.name ?? "none" }
}

struct DevicePicker: View {
    let title: String
    let input: Bool
    @ObservedObject var list: DeviceListModel
    @Binding var uid: String
    let nameKey: String
    let changed: () -> Void

    var body: some View {
        let choices = list.devices.filter { (input ? $0.inputs : $0.outputs) > 0 }
        Picker(title, selection: $uid) {
            Text("System default (\(list.name(input ? list.defaultInput : list.defaultOutput)))").tag("")
            ForEach(choices) { d in Text(d.name).tag(d.uid) }
            if !uid.isEmpty && !choices.contains(where: { $0.uid == uid }) {
                Text("\(Prefs.string(nameKey) ?? "Chosen device") (not connected)").tag(uid)
            }
        }
        .onChange(of: uid) {
            Prefs.set(nameKey, choices.first { $0.uid == uid }?.name)
            changed()
        }
    }
}

struct VoiceSettingsView: View {
    @ObservedObject var meter: MeterModel
    var devicesChanged: () -> Void = {}
    @StateObject private var deviceList = DeviceListModel()
    @AppStorage(AudioDevices.inputKey) private var micUid = ""
    @AppStorage(AudioDevices.outputKey) private var speakerUid = ""
    @AppStorage("VoicePushToTalk") private var ptt = false
    @AppStorage("VoicePttKey") private var pttKey = "option"
    @AppStorage("VoiceSensitivity") private var sensitivity: Double = -50
    @AppStorage("VoiceGain") private var gain: Double = 100
    @AppStorage("VoiceJoinMuted") private var joinMuted = false

    var body: some View {
        Form {
            Section {
                DevicePicker(title: "Microphone", input: true, list: deviceList, uid: $micUid, nameKey: AudioDevices.inputKey + "Name", changed: devicesChanged)
                DevicePicker(title: "Speakers", input: false, list: deviceList, uid: $speakerUid, nameKey: AudioDevices.outputKey + "Name", changed: devicesChanged)
                Text("Calls only: the rest of the Mac keeps using System Settings > Sound. A device that's unplugged or switched off falls back to the system default.")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Picker("Input mode", selection: $ptt) {
                Text("Voice activity").tag(false)
                Text("Push to talk").tag(true)
            }
            .pickerStyle(.radioGroup)
            if ptt {
                Picker("Hold to talk", selection: $pttKey) {
                    ForEach(VoiceManager.pttKeys, id: \.0) { k in Text(k.1).tag(k.0) }
                }
            } else {
                VStack(alignment: .leading, spacing: 4) {
                    Slider(value: $sensitivity, in: -80 ... -10) { Text("Sensitivity") }
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Capsule().fill(Theme.accentLight)
                            Capsule().fill(Double(meter.level) > sensitivity ? Color.green : Color.orange)
                                .frame(width: g.size.width * CGFloat(min(1, max(0, (Double(meter.level) + 80) / 70))))
                            Rectangle().fill(Color.primary).frame(width: 2)
                                .offset(x: g.size.width * CGFloat((sensitivity + 80) / 70) - 1)
                        }
                    }
                    .frame(height: 8)
                    Text("Talk normally: the bar should go green while you speak and stay orange when you don't.")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
            }
            Slider(value: $gain, in: 25...200, step: 5) { Text("Mic volume") } minimumValueLabel: { Text("") } maximumValueLabel: { Text("\(Int(gain))%") }
            Toggle("Join calls muted", isOn: $joinMuted)
            Text("Echo cancellation is on whenever macOS can run it on your mic and speakers. When it can't, the voice bar says so: then use headphones.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
            if !meter.note.isEmpty { Text(meter.note).font(.system(size: 11)).foregroundStyle(.secondary) }
        }
        .formStyle(.grouped)
        .frame(width: 400)
        .onChange(of: ptt) { VoiceManager.shared.current?.applySettings() }
        .onChange(of: pttKey) { VoiceManager.shared.current?.applySettings() }
        .onChange(of: sensitivity) { VoiceManager.shared.current?.applySettings() }
        .onChange(of: gain) { VoiceManager.shared.current?.applySettings() }
    }
}
