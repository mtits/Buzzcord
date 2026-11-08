import AudioToolbox
import CoreAudio
import Foundation

// The Mac's microphones and speakers, for picking call devices apart from System Settings >
// Sound. A choice is remembered by the device's UID, which stays the same across replugging;
// if the device isn't there, calls use the system default.
enum AudioDevices {
    struct Device: Identifiable, Equatable {
        let id: AudioDeviceID
        let uid: String
        let name: String
        let inputs: Int
        let outputs: Int
        let sampleRate: Double
        // USB headsets often appear as two devices (mic, headphones) sharing one model UID.
        let modelUid: String
    }

    static let inputKey = "VoiceInputDevice"
    static let outputKey = "VoiceOutputDevice"

    static var savedInput: String { Prefs.string(inputKey) ?? "" }
    static var savedOutput: String { Prefs.string(outputKey) ?? "" }

    private static func address(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func string(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var addr = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    private static func channels(_ id: AudioDeviceID, input: Bool) -> Int {
        var addr = address(kAudioDevicePropertyStreamConfiguration, input ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, raw) == noErr else { return 0 }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    static func all() -> [Device] {
        var addr = address(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard let uid = string(id, kAudioDevicePropertyDeviceUID) else { return nil }
            var rate: Float64 = 0
            var rateSize = UInt32(MemoryLayout<Float64>.size)
            var rateAddr = address(kAudioDevicePropertyNominalSampleRate)
            AudioObjectGetPropertyData(id, &rateAddr, 0, nil, &rateSize, &rate)
            let d = Device(id: id, uid: uid, name: string(id, kAudioObjectPropertyName) ?? uid,
                           inputs: channels(id, input: true), outputs: channels(id, input: false), sampleRate: rate,
                           modelUid: string(id, kAudioDevicePropertyModelUID) ?? "")
            return d.inputs + d.outputs > 0 ? d : nil
        }
    }

    static func defaultDevice(input: Bool) -> AudioDeviceID? {
        var addr = address(input ? kAudioHardwarePropertyDefaultInputDevice : kAudioHardwarePropertyDefaultOutputDevice)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id) == noErr, id != 0 else { return nil }
        return id
    }

    // The device a saved choice means right now: nil for "System default", or when the
    // chosen device is unplugged (or no longer has a mic / speakers).
    static func pick(saved: String, input: Bool, in devices: [Device]) -> Device? {
        guard !saved.isEmpty else { return nil }
        return devices.first { $0.uid == saved && (input ? $0.inputs : $0.outputs) > 0 }
    }

    // What a call will actually use: the chosen device, else the system default.
    static func current(input: Bool, in devices: [Device] = all()) -> Device? {
        if let d = pick(saved: input ? savedInput : savedOutput, input: input, in: devices) { return d }
        let def = defaultDevice(input: input)
        return devices.first { $0.id == def }
    }

    // Calls back on the main queue when devices come or go or a system default changes.
    // Keep the returned token; dropping it stops the calls.
    final class Watch {
        private var blocks: [(AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []

        init(_ changed: @escaping () -> Void) {
            for sel in [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultInputDevice, kAudioHardwarePropertyDefaultOutputDevice] {
                var addr = address(sel)
                let block: AudioObjectPropertyListenerBlock = { _, _ in changed() }
                if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, DispatchQueue.main, block) == noErr {
                    blocks.append((addr, block))
                }
            }
        }

        deinit {
            for (a, b) in blocks {
                var addr = a
                AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, DispatchQueue.main, b)
            }
        }
    }

    // The mic and speakers are one piece of hardware (a headset, the Mac itself), which is
    // when macOS echo cancellation is worth trying.
    static func sameHardware(_ a: Device?, _ b: Device?) -> Bool {
        guard let a, let b else { return false }
        return a.id == b.id || (!a.modelUid.isEmpty && a.modelUid == b.modelUid)
    }
}
