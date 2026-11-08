import AppKit
import Carbon.HIToolbox
import XCTest
@testable import Buzzcord

final class DeviceTests: XCTestCase {
    let razerMic = AudioDevices.Device(id: 123, uid: "razer:1", name: "Razer", inputs: 1, outputs: 0, sampleRate: 48000, modelUid: "razer-model")
    let razerOut = AudioDevices.Device(id: 127, uid: "razer:2", name: "Razer", inputs: 0, outputs: 2, sampleRate: 48000, modelUid: "razer-model")
    let macMic = AudioDevices.Device(id: 84, uid: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone", inputs: 1, outputs: 0, sampleRate: 48000, modelUid: "Digital Mic")

    func testSavedChoice() {
        let all = [razerMic, razerOut, macMic]
        XCTAssertNil(AudioDevices.pick(saved: "", input: true, in: all), "empty means the system default")
        XCTAssertEqual(AudioDevices.pick(saved: "razer:1", input: true, in: all), razerMic)
        XCTAssertNil(AudioDevices.pick(saved: "razer:2", input: true, in: all), "headphones aren't a microphone")
        XCTAssertEqual(AudioDevices.pick(saved: "razer:2", input: false, in: all), razerOut)
        XCTAssertNil(AudioDevices.pick(saved: "razer:1", input: true, in: [macMic]), "unplugged: back to the default")
    }

    func testSameHardware() {
        XCTAssertTrue(AudioDevices.sameHardware(razerMic, razerOut), "a headset's mic and headphones are two devices of one model")
        XCTAssertFalse(AudioDevices.sameHardware(macMic, razerOut))
        XCTAssertFalse(AudioDevices.sameHardware(nil, razerOut))
    }

    func testListsThisMac() {
        let all = AudioDevices.all()
        XCTAssertFalse(all.isEmpty, "Core Audio lists devices")
        XCTAssertTrue(all.allSatisfy { !$0.uid.isEmpty && $0.inputs + $0.outputs > 0 })
    }
}

final class ShortcutTests: XCTestCase {
    func testDisplayAndModifiers() {
        XCTAssertEqual(GlobalShortcut.Combo.standard.display, "⌥⌘B")
        let all = GlobalShortcut.Combo(keyCode: 0, modifiers: UInt32(cmdKey | optionKey | controlKey | shiftKey), key: "A")
        XCTAssertEqual(all.display, "⌃⌥⇧⌘A", "modifiers in the order macOS menus use")
        XCTAssertEqual(GlobalShortcut.Combo.carbon([.command, .option]), UInt32(cmdKey | optionKey))
        XCTAssertEqual(GlobalShortcut.Combo.carbon([.control, .shift]), UInt32(controlKey | shiftKey))
        XCTAssertFalse(GlobalShortcut.Combo.usable(UInt32(shiftKey)), "Shift alone would steal typing")
        XCTAssertFalse(GlobalShortcut.Combo.usable(0))
        XCTAssertTrue(GlobalShortcut.Combo.usable(UInt32(controlKey)))
    }
}
