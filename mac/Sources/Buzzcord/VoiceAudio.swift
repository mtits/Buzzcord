import AppKit
import AudioToolbox
import AVFoundation
import os

// Opus through AudioToolbox's own codec: 48 kHz stereo, 20 ms packets, as Discord uses.
final class OpusCodec {
    static let rate = 48000.0, frameSamples = 960
    private var converter: AudioConverterRef?
    private let encoding: Bool
    private var maxPacket = 1500

    private final class Feed {
        var data: Data
        let desc = UnsafeMutablePointer<AudioStreamPacketDescription>.allocate(capacity: 1)
        var done = false
        init(_ d: Data) { data = d; desc.initialize(to: AudioStreamPacketDescription()) }
        deinit { desc.deallocate() }
    }

    private static var pcmFormat = AudioStreamBasicDescription(
        mSampleRate: rate, mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
        mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: 2, mBitsPerChannel: 16, mReserved: 0)
    private static var opusFormat = AudioStreamBasicDescription(
        mSampleRate: rate, mFormatID: kAudioFormatOpus, mFormatFlags: 0, mBytesPerPacket: 0, mFramesPerPacket: UInt32(frameSamples),
        mBytesPerFrame: 0, mChannelsPerFrame: 2, mBitsPerChannel: 0, mReserved: 0)

    init(encoder: Bool, bitrate: UInt32 = 64000) {
        encoding = encoder
        var pcm = OpusCodec.pcmFormat, opus = OpusCodec.opusFormat
        if encoder {
            AudioConverterNew(&pcm, &opus, &converter)
            var br = bitrate
            if let converter {
                AudioConverterSetProperty(converter, kAudioConverterEncodeBitRate, 4, &br)
                var size = UInt32(4), out: UInt32 = 0
                if AudioConverterGetProperty(converter, kAudioConverterPropertyMaximumOutputPacketSize, &size, &out) == noErr, out > 0 { maxPacket = Int(out) }
            }
        } else {
            AudioConverterNew(&opus, &pcm, &converter)
        }
    }

    deinit { if let converter { AudioConverterDispose(converter) } }

    // 960 interleaved stereo samples in, one Opus packet out.
    func encode(_ stereo: [Int16]) -> Data? {
        guard encoding, let converter else { return nil }
        let feed = Feed(stereo.withUnsafeBufferPointer { Data(buffer: $0) })
        var out = Data(count: maxPacket)
        var desc = AudioStreamPacketDescription()
        var packets: UInt32 = 1
        let status = out.withUnsafeMutableBytes { op -> OSStatus in
            var abl = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(op.count), mData: op.baseAddress))
            return AudioConverterFillComplexBuffer(converter, { _, ioCount, ioData, _, ud in
                let f = Unmanaged<Feed>.fromOpaque(ud!).takeUnretainedValue()
                if f.done { ioCount.pointee = 0; return 1 }
                f.done = true
                ioCount.pointee = UInt32(OpusCodec.frameSamples)
                f.data.withUnsafeMutableBytes { p in
                    ioData.pointee.mBuffers.mData = p.baseAddress
                    ioData.pointee.mBuffers.mDataByteSize = UInt32(p.count)
                    ioData.pointee.mBuffers.mNumberChannels = 2
                }
                return noErr
            }, Unmanaged.passUnretained(feed).toOpaque(), &packets, &abl, &desc)
        }
        guard (status == noErr || status == 1), packets == 1, desc.mDataByteSize > 0 else { return nil }
        return out.prefix(Int(desc.mDataByteSize))
    }

    // One Opus packet in, up to 960 stereo samples out (fewer on the very first packet).
    func decode(_ packet: Data) -> [Int16]? {
        guard !encoding, let converter, !packet.isEmpty else { return nil }
        let feed = Feed(packet)
        feed.desc.pointee = AudioStreamPacketDescription(mStartOffset: 0, mVariableFramesInPacket: 0, mDataByteSize: UInt32(packet.count))
        var pcm = [Int16](repeating: 0, count: OpusCodec.frameSamples * 2)
        var frames = UInt32(OpusCodec.frameSamples)
        let status = pcm.withUnsafeMutableBytes { op -> OSStatus in
            var abl = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(op.count), mData: op.baseAddress))
            return AudioConverterFillComplexBuffer(converter, { _, ioCount, ioData, outDesc, ud in
                let f = Unmanaged<Feed>.fromOpaque(ud!).takeUnretainedValue()
                if f.done { ioCount.pointee = 0; return 1 }
                f.done = true
                ioCount.pointee = 1
                f.data.withUnsafeMutableBytes { p in
                    ioData.pointee.mBuffers.mData = p.baseAddress
                    ioData.pointee.mBuffers.mDataByteSize = UInt32(p.count)
                }
                outDesc?.pointee = f.desc
                return noErr
            }, Unmanaged.passUnretained(feed).toOpaque(), &frames, &abl, nil)
        }
        guard status == noErr || status == 1, frames > 0 else { return nil }
        if frames < UInt32(OpusCodec.frameSamples) {
            // The decoder's start-up delay: pad the front so every frame is 20 ms.
            pcm = [Int16](repeating: 0, count: (OpusCodec.frameSamples - Int(frames)) * 2) + pcm.prefix(Int(frames) * 2)
        }
        return pcm
    }
}

// One speaker's incoming audio: reorders packets by RTP sequence, waits for 60 ms before
// starting, fills short gaps with silence, and stops after 100 ms of nothing.
final class PlaybackStream {
    private static let startDepth = 3, maxMisses = 5, maxDepth = 25
    private let decoder = OpusCodec(encoder: false)
    private var buffer: [UInt16: Data] = [:]
    private var next: UInt16 = 0
    private var misses = 0
    private var lock = os_unfair_lock()
    private(set) var playing = false
    var volume: Float = 1

    func push(_ seq: UInt16, _ opus: Data) {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        if playing && Int16(bitPattern: seq &- next) < 0 { return }   // too late
        buffer[seq] = opus
        if buffer.count > PlaybackStream.maxDepth { buffer.removeAll(); playing = false }   // fell far behind; start over
    }

    func nextFrame() -> [Int16]? {
        os_unfair_lock_lock(&lock); defer { os_unfair_lock_unlock(&lock) }
        if !playing {
            if buffer.count < PlaybackStream.startDepth { return nil }
            var first = true
            for s in buffer.keys where first || Int16(bitPattern: s &- next) < 0 { next = s; first = false }
            playing = true
            misses = 0
        }
        defer { next &+= 1 }
        if let opus = buffer.removeValue(forKey: next) {
            misses = 0
            return decoder.decode(opus) ?? [Int16](repeating: 0, count: OpusCodec.frameSamples * 2)
        }
        misses += 1
        if buffer.isEmpty && misses > PlaybackStream.maxMisses { playing = false; return nil }
        return [Int16](repeating: 0, count: OpusCodec.frameSamples * 2)   // lost or late
    }
}

// The mic and speakers for a call, on Core Audio I/O units. It first tries macOS voice
// processing on the chosen mic and speakers (echo cancellation and noise suppression); if
// that won't run on these devices, the mic and speakers get a unit each. A device that won't
// start (unplugged, or a wireless headset that's switched off) falls back to the system
// default. Frames are 20 ms of Opus.
final class AudioEngine {
    static let silenceFrame = Data([0xF8, 0xFF, 0xFE])

    var muted = false
    var deafened = false
    var usePushToTalk = false
    var pushToTalkKey: NSEvent.ModifierFlags = .option
    var sensitivityDb: Float = -50   // voice activity threshold
    var inputGain: Float = 1

    var onFrame: ((Data) -> Void)?              // Opus frame to send (capture thread)
    var onTransmitting: ((Bool) -> Void)?       // start/stop talking (capture thread)
    var onLevel: ((Float) -> Void)?             // dBFS of each frame, for a meter
    var onRestart: (() -> Void)?                // devices changed mid-call and audio restarted (main)
    // Overrides for the saved choice (--audio-probe); nil reads Voice Settings.
    var inputDevice: AudioDeviceID?
    var outputDevice: AudioDeviceID?
    var tryVoiceProcessing = true

    private(set) var echoCancellation = false
    private(set) var micCallbacks = 0
    private(set) var renderCalls = 0
    private(set) var usingInput: AudioDevices.Device?
    private(set) var usingOutput: AudioDevices.Device?
    private(set) var note = ""                  // what had to fall back, for the voice bar
    private let encoder = OpusCodec(encoder: true)
    private var units: [AudioUnit] = []
    private var micUnit: AudioUnit?
    private var micRate: Double = OpusCodec.rate
    private let micBuffer = UnsafeMutablePointer<Float>.allocate(capacity: 16384)
    private var captureConverter: AVAudioConverter?
    private var outChannels = 2
    private var pending: [Float] = []
    private var transmitting = false
    private var silenceToSend = 0
    private var hangUntil = Date.distantPast
    private var streams: [UInt64: PlaybackStream] = [:]
    private var volumes: [UInt64: Float] = [:]
    private var streamLock = os_unfair_lock()
    private var outFifo: [Int16] = []
    private var running = false
    private var captureOnly = false
    private var watch: AudioDevices.Watch?

    deinit {
        watch = nil
        stop()
        micBuffer.deallocate()
    }

    // Opens the mic and speakers. Throws if no mic will start (no permission, no device).
    func start(captureOnly: Bool = false) throws {
        guard !running else { return }
        self.captureOnly = captureOnly
        let devices = AudioDevices.all()
        note = ""
        func choose(_ input: Bool) -> AudioDevices.Device? {
            if let id = input ? inputDevice : outputDevice { return devices.first { $0.id == id } }
            let saved = input ? AudioDevices.savedInput : AudioDevices.savedOutput
            if !saved.isEmpty && AudioDevices.pick(saved: saved, input: input, in: devices) == nil {
                note = "Your chosen \(input ? "microphone" : "speakers") aren't connected, so the system default is used."
            }
            return AudioDevices.current(input: input, in: devices)
        }
        let defaultMic = AudioDevices.defaultDevice(input: true).flatMap { id in devices.first { $0.id == id } }
        let defaultOut = AudioDevices.defaultDevice(input: false).flatMap { id in devices.first { $0.id == id } }
        guard let mic = choose(true) ?? defaultMic else { throw Self.error("No microphone found.") }
        let speakers = captureOnly ? nil : (choose(false) ?? defaultOut)

        // 1. Voice processing on the pair, if sound really flows.
        if tryVoiceProcessing, let speakers {
            do {
                let u = try makeUnit(voiceProcessing: true, mic: mic, speakers: speakers)
                if waitForSound(mic: true, speakers: true) {
                    echoCancellation = true
                    finishStart(mic, speakers)
                    return
                }
                Log.write("audio: voice processing started but no sound moved on \(mic.name) / \(speakers.name)")
                dispose(u)
            } catch {
                Log.write("audio: voice processing didn't start on \(mic.name) / \(speakers.name) (\(error.localizedDescription))")
            }
        }

        // 2. A unit each, falling back to the system default for whichever won't start.
        echoCancellation = false
        var usedMic: AudioDevices.Device?
        for d in [mic, defaultMic].compactMap({ $0 }) where usedMic == nil {
            do {
                let u = try makeUnit(voiceProcessing: false, mic: d, speakers: nil)
                if waitForSound(mic: true, speakers: false) { usedMic = d } else { dispose(u) }
            } catch { Log.write("audio: microphone \(d.name) didn't start (\(error.localizedDescription))") }
            if usedMic == nil && d == mic && mic != defaultMic { note = "\(mic.name) didn't respond, so the system default microphone is used." }
        }
        guard let usedMic else { stop(); throw Self.error("The microphone didn't start. Check it's connected and switched on.") }
        var usedOut: AudioDevices.Device?
        if let speakers {
            for d in [speakers, defaultOut].compactMap({ $0 }) where usedOut == nil {
                do {
                    let u = try makeUnit(voiceProcessing: false, mic: nil, speakers: d)
                    if waitForSound(mic: false, speakers: true) { usedOut = d } else { dispose(u) }
                } catch { Log.write("audio: speakers \(d.name) didn't start (\(error.localizedDescription))") }
                if usedOut == nil && d == speakers && speakers != defaultOut { note = "\(speakers.name) didn't respond, so the system default speakers are used." }
            }
            if usedOut == nil { note = "The speakers didn't start: check they're connected and switched on." }
        }
        finishStart(usedMic, usedOut)
    }

    private func finishStart(_ mic: AudioDevices.Device, _ speakers: AudioDevices.Device?) {
        usingInput = mic
        usingOutput = speakers
        running = true
        Log.write("audio: using \(mic.name) and \(speakers?.name ?? "no speakers"), echo cancellation \(echoCancellation ? "on" : "off")")
        listenForDeviceChanges()
    }

    // Closes and reopens the devices (Voice Settings changed, or a device came or went).
    func restart() {
        guard running else { return }
        let only = captureOnly
        stop()
        do { try start(captureOnly: only) } catch { note = error.localizedDescription; Log.write("audio restart: \(error.localizedDescription)") }
        onRestart?()
    }

    func stop() {
        for u in units { dispose(u) }
        units.removeAll()
        micUnit = nil
        running = false
    }

    private static func error(_ text: String, _ code: OSStatus = 1) -> NSError {
        NSError(domain: "Buzzcord", code: Int(code), userInfo: [NSLocalizedDescriptionKey: text])
    }

    // Sound moved within a moment: some devices start "fine" and then never run.
    private func waitForSound(mic: Bool, speakers: Bool) -> Bool {
        let m0 = micCallbacks, r0 = renderCalls
        let until = Date().addingTimeInterval(0.8)
        while Date() < until {
            if (!mic || micCallbacks > m0) && (!speakers || renderCalls > r0) { return true }
            usleep(10_000)
        }
        return false
    }

    private func dispose(_ u: AudioUnit) {
        AudioOutputUnitStop(u)
        AudioUnitUninitialize(u)
        AudioComponentInstanceDispose(u)
        units.removeAll { $0 == u }
        if micUnit == u { micUnit = nil }
    }

    @discardableResult
    private func makeUnit(voiceProcessing: Bool, mic: AudioDevices.Device?, speakers: AudioDevices.Device?) throws -> AudioUnit {
        var desc = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                             componentSubType: voiceProcessing ? kAudioUnitSubType_VoiceProcessingIO : kAudioUnitSubType_HALOutput,
                                             componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0)
        guard let comp = AudioComponentFindNext(nil, &desc) else { throw Self.error("No audio unit.") }
        var made: AudioUnit?
        try check(AudioComponentInstanceNew(comp, &made), "open audio")
        guard let u = made else { throw Self.error("No audio unit.") }
        do {
            func set<T>(_ prop: AudioUnitPropertyID, _ scope: AudioUnitScope, _ el: AudioUnitElement, _ value: T, _ what: String) throws {
                var v = value
                try check(AudioUnitSetProperty(u, prop, scope, el, &v, UInt32(MemoryLayout<T>.size)), what)
            }
            try set(kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, UInt32(mic != nil ? 1 : 0), "enable mic")
            try set(kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, UInt32(speakers != nil ? 1 : 0), "enable speakers")
            if voiceProcessing {
                if let mic { try set(kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 1, mic.id, "use \(mic.name)") }
                if let speakers { try set(kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, speakers.id, "use \(speakers.name)") }
                // Voice processing turns other apps' sound way down by default; keep that light.
                var duck = AUVoiceIOOtherAudioDuckingConfiguration(mEnableAdvancedDucking: true, mDuckingLevel: .min)
                AudioUnitSetProperty(u, kAUVoiceIOProperty_OtherAudioDuckingConfiguration, kAudioUnitScope_Global, 0, &duck, UInt32(MemoryLayout.size(ofValue: duck)))
            } else if let d = mic ?? speakers {
                try set(kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, d.id, "use \(d.name)")
            }
            let me = Unmanaged.passUnretained(self).toOpaque()
            if let mic {
                // Voice processing converts to any rate; a plain unit hands over the device's own rate.
                micRate = voiceProcessing || mic.sampleRate <= 0 ? OpusCodec.rate : mic.sampleRate
                try set(kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, Self.floatFormat(micRate, 1), "mic format")
                captureConverter = micRate == OpusCodec.rate ? nil
                    : AVAudioConverter(from: AVAudioFormat(standardFormatWithSampleRate: micRate, channels: 1)!,
                                       to: AVAudioFormat(standardFormatWithSampleRate: OpusCodec.rate, channels: 1)!)
                pending.removeAll()
                try set(kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0,
                        AURenderCallbackStruct(inputProc: { ref, flags, ts, _, frames, _ in
                            Unmanaged<AudioEngine>.fromOpaque(ref).takeUnretainedValue().micReady(flags, ts, frames)
                        }, inputProcRefCon: me), "mic callback")
            }
            if speakers != nil {
                outChannels = 2
                if (try? set(kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, Self.floatFormat(OpusCodec.rate, 2), "speaker format")) == nil {
                    outChannels = 1   // voice processing may only take mono
                    try set(kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, Self.floatFormat(OpusCodec.rate, 1), "speaker format")
                }
                try set(kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0,
                        AURenderCallbackStruct(inputProc: { ref, _, _, _, frames, abl in
                            if let abl { Unmanaged<AudioEngine>.fromOpaque(ref).takeUnretainedValue().render(Int(frames), UnsafeMutableAudioBufferListPointer(abl)) }
                            return noErr
                        }, inputProcRefCon: me), "speaker callback")
            }
            try check(AudioUnitInitialize(u), "set up audio")
            units.append(u)
            if mic != nil { micUnit = u }
            try check(AudioOutputUnitStart(u), "start audio")
            return u
        } catch {
            dispose(u)
            throw error
        }
    }

    private func check(_ st: OSStatus, _ what: String) throws {
        if st != noErr { throw Self.error("Couldn't \(what) (\(st)).", st) }
    }

    private static func floatFormat(_ rate: Double, _ channels: UInt32) -> AudioStreamBasicDescription {
        AudioStreamBasicDescription(mSampleRate: rate, mFormatID: kAudioFormatLinearPCM,
                                    mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                                    mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: channels, mBitsPerChannel: 32, mReserved: 0)
    }

    // Unplugging a device in use, or changing the system default while it's followed,
    // restarts audio on whatever is right now.
    private func listenForDeviceChanges() {
        if watch == nil { watch = AudioDevices.Watch { [weak self] in self?.devicesChanged() } }
    }

    private var restartPending = false

    private func devicesChanged() {
        guard running, !restartPending else { return }
        restartPending = true
        // Devices often come and go in bursts; settle first.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.7) { [weak self] in
            guard let self else { return }
            self.restartPending = false
            let devices = AudioDevices.all()
            let wantIn = self.inputDevice.flatMap { id in devices.first { $0.id == id } } ?? AudioDevices.current(input: true, in: devices)
            let wantOut = self.captureOnly ? nil : (self.outputDevice.flatMap { id in devices.first { $0.id == id } } ?? AudioDevices.current(input: false, in: devices))
            if wantIn?.id != self.usingInput?.id || wantOut?.id != self.usingOutput?.id {
                Log.write("audio: devices changed; restarting on \(wantIn?.name ?? "?") / \(wantOut?.name ?? "none")")
                self.restart()
            }
        }
    }

    // MARK: capture

    private func micReady(_ flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>, _ ts: UnsafePointer<AudioTimeStamp>, _ frames: UInt32) -> OSStatus {
        guard let unit = micUnit, frames <= 16384 else { return noErr }
        var abl = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: 1, mDataByteSize: frames * 4, mData: micBuffer))
        let st = AudioUnitRender(unit, flags, ts, 1, frames, &abl)
        guard st == noErr else { return st }
        micCallbacks &+= 1
        let samples = UnsafeBufferPointer(start: micBuffer, count: Int(frames))
        if let conv = captureConverter { convert(samples, conv) } else { pending.append(contentsOf: samples) }
        while pending.count >= OpusCodec.frameSamples {
            let frame = Array(pending.prefix(OpusCodec.frameSamples))
            pending.removeFirst(OpusCodec.frameSamples)
            processCaptured(frame, Date())
        }
        return noErr
    }

    private func convert(_ samples: UnsafeBufferPointer<Float>, _ conv: AVAudioConverter) {
        guard let inBuf = AVAudioPCMBuffer(pcmFormat: conv.inputFormat, frameCapacity: AVAudioFrameCount(samples.count)) else { return }
        inBuf.frameLength = AVAudioFrameCount(samples.count)
        inBuf.floatChannelData![0].update(from: samples.baseAddress!, count: samples.count)
        let ratio = conv.outputFormat.sampleRate / conv.inputFormat.sampleRate
        guard let out = AVAudioPCMBuffer(pcmFormat: conv.outputFormat, frameCapacity: AVAudioFrameCount(Double(samples.count) * ratio) + 32) else { return }
        var given = false
        var err: NSError?
        conv.convert(to: out, error: &err) { _, status in
            if given { status.pointee = .noDataNow; return nil }
            given = true
            status.pointee = .haveData
            return inBuf
        }
        if let ch = out.floatChannelData?[0] { pending.append(contentsOf: UnsafeBufferPointer(start: ch, count: Int(out.frameLength))) }
    }

    var pushToTalkDown: Bool { NSEvent.modifierFlags.contains(pushToTalkKey) }

    // One 20 ms mono frame from the mic (floats, -1...1).
    func processCaptured(_ mono: [Float], _ now: Date) {
        var sum: Float = 0
        for s in mono { let v = s * inputGain; sum += v * v }
        let db = 10 * log10(sum / Float(mono.count) + 1e-12)
        onLevel?(db)

        let want: Bool
        if muted { want = false }
        else if usePushToTalk { want = pushToTalkDown }
        else {
            if db > sensitivityDb { hangUntil = now.addingTimeInterval(0.3) }
            want = now < hangUntil
        }

        if want {
            if !transmitting { transmitting = true; onTransmitting?(true) }
            silenceToSend = 5
            var stereo = [Int16](repeating: 0, count: mono.count * 2)
            for i in 0..<mono.count {
                let c = Int16(max(-32768, min(32767, mono[i] * inputGain * 32767)))
                stereo[2 * i] = c
                stereo[2 * i + 1] = c
            }
            if let packet = encoder.encode(stereo) { onFrame?(packet) }
        } else if transmitting {
            // Discord expects five silence frames when someone stops talking.
            if silenceToSend > 0 { silenceToSend -= 1; onFrame?(AudioEngine.silenceFrame); return }
            transmitting = false
            onTransmitting?(false)
        }
    }

    // MARK: playback

    func receive(_ user: UInt64, _ seq: UInt16, _ opus: Data) {
        os_unfair_lock_lock(&streamLock)
        let s = streams[user] ?? PlaybackStream()
        if streams[user] == nil { s.volume = volumes[user] ?? 1; streams[user] = s }
        os_unfair_lock_unlock(&streamLock)
        s.push(seq, opus)
    }

    func setVolume(_ user: UInt64, _ v: Float) {
        os_unfair_lock_lock(&streamLock)
        volumes[user] = v
        streams[user]?.volume = v
        os_unfair_lock_unlock(&streamLock)
    }

    func removeUser(_ user: UInt64) {
        os_unfair_lock_lock(&streamLock)
        streams.removeValue(forKey: user)
        os_unfair_lock_unlock(&streamLock)
    }

    // Who is audible right now, for the speaking indicators.
    func isPlaying(_ user: UInt64) -> Bool {
        os_unfair_lock_lock(&streamLock); defer { os_unfair_lock_unlock(&streamLock) }
        return streams[user]?.playing == true
    }

    // 20 ms of interleaved stereo.
    private func mix() -> [Int16] {
        os_unfair_lock_lock(&streamLock)
        let list = Array(streams.values)
        os_unfair_lock_unlock(&streamLock)
        var acc = [Int32](repeating: 0, count: OpusCodec.frameSamples * 2)
        for s in list {
            guard let pcm = s.nextFrame(), !deafened else { continue }
            let v = s.volume
            for i in 0..<acc.count { acc[i] += Int32(Float(pcm[i]) * v) }
        }
        return acc.map { Int16(max(-32768, min(32767, $0))) }
    }

    // Fills the speakers: two channels, or one (voice processing) with left and right mixed.
    private func render(_ frames: Int, _ abl: UnsafeMutableAudioBufferListPointer) {
        renderCalls &+= 1
        while outFifo.count < frames * 2 { outFifo += mix() }
        if abl.count >= 2, let l = abl[0].mData?.assumingMemoryBound(to: Float.self), let r = abl[1].mData?.assumingMemoryBound(to: Float.self) {
            for i in 0..<frames {
                l[i] = Float(outFifo[2 * i]) / 32768
                r[i] = Float(outFifo[2 * i + 1]) / 32768
            }
        } else if abl.count == 1, let m = abl[0].mData?.assumingMemoryBound(to: Float.self) {
            for i in 0..<frames { m[i] = (Float(outFifo[2 * i]) + Float(outFifo[2 * i + 1])) / 65536 }
        }
        outFifo.removeFirst(frames * 2)
    }
}
