import AppKit
import Carbon.HIToolbox
import SwiftUI

// A keyboard shortcut that works from any app (default Option-Command-B) and brings every
// buddy list to the front, like double-clicking the menu bar icon. It uses the system hot key
// service, so it needs no Accessibility permission and sees no other keystrokes.
@MainActor
final class GlobalShortcut {
    static let shared = GlobalShortcut()

    struct Combo: Equatable {
        var keyCode: UInt32
        var modifiers: UInt32   // Carbon: cmdKey, optionKey, controlKey, shiftKey
        var key: String         // what the key shows, e.g. "B"

        static let standard = Combo(keyCode: UInt32(kVK_ANSI_B), modifiers: UInt32(cmdKey | optionKey), key: "B")

        // ⌃⌥⇧⌘ order, as macOS menus show it.
        var display: String {
            var s = ""
            if modifiers & UInt32(controlKey) != 0 { s += "⌃" }
            if modifiers & UInt32(optionKey) != 0 { s += "⌥" }
            if modifiers & UInt32(shiftKey) != 0 { s += "⇧" }
            if modifiers & UInt32(cmdKey) != 0 { s += "⌘" }
            return s + key
        }

        static func carbon(_ flags: NSEvent.ModifierFlags) -> UInt32 {
            var m: UInt32 = 0
            if flags.contains(.command) { m |= UInt32(cmdKey) }
            if flags.contains(.option) { m |= UInt32(optionKey) }
            if flags.contains(.control) { m |= UInt32(controlKey) }
            if flags.contains(.shift) { m |= UInt32(shiftKey) }
            return m
        }

        // Needs Command, Option or Control: Shift alone would eat ordinary typing.
        static func usable(_ modifiers: UInt32) -> Bool { modifiers & UInt32(cmdKey | optionKey | controlKey) != 0 }

        static func keyName(_ e: NSEvent) -> String {
            switch Int(e.keyCode) {
            case kVK_Space: return "Space"
            case kVK_Return: return "↩"
            case kVK_Tab: return "⇥"
            case kVK_Delete: return "⌫"
            case kVK_LeftArrow: return "←"
            case kVK_RightArrow: return "→"
            case kVK_UpArrow: return "↑"
            case kVK_DownArrow: return "↓"
            default:
                if let f = [kVK_F1: 1, kVK_F2: 2, kVK_F3: 3, kVK_F4: 4, kVK_F5: 5, kVK_F6: 6, kVK_F7: 7, kVK_F8: 8, kVK_F9: 9, kVK_F10: 10,
                            kVK_F11: 11, kVK_F12: 12, kVK_F13: 13, kVK_F14: 14, kVK_F15: 15][Int(e.keyCode)] { return "F\(f)" }
                return (e.charactersIgnoringModifiers ?? "?").uppercased()
            }
        }
    }

    var action: () -> Void = {}
    private(set) var problem: String?
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?

    // nil when turned off.
    static var combo: Combo? {
        get {
            if Prefs.bool("ShortcutOff", false) { return nil }
            let d = UserDefaults.standard
            guard let code = d.object(forKey: "ShortcutKeyCode") as? NSNumber, let mods = d.object(forKey: "ShortcutModifiers") as? NSNumber else { return .standard }
            return Combo(keyCode: code.uint32Value, modifiers: mods.uint32Value, key: d.string(forKey: "ShortcutKey") ?? "?")
        }
        set {
            Prefs.set("ShortcutOff", newValue == nil)
            if let c = newValue {
                Prefs.set("ShortcutKeyCode", c.keyCode)
                Prefs.set("ShortcutModifiers", c.modifiers)
                Prefs.set("ShortcutKey", c.key)
            }
        }
    }

    // Registers the saved shortcut. False (with `problem` set) when another app already has it.
    @discardableResult
    func apply() -> Bool {
        unregister()
        problem = nil
        guard let c = Self.combo else { return true }
        installHandler()
        var newRef: EventHotKeyRef?
        let id = EventHotKeyID(signature: OSType(0x4255_5A5A), id: 1)   // 'BUZZ'
        let st = RegisterEventHotKey(c.keyCode, c.modifiers, id, GetApplicationEventTarget(), 0, &newRef)
        guard st == noErr, let newRef else {
            problem = "\(c.display) is already used by another app (\(st)). Pick a different shortcut."
            Log.write("shortcut: couldn't register \(c.display) (\(st))")
            return false
        }
        ref = newRef
        Log.write("shortcut: \(c.display) brings the buddy lists forward")
        return true
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        ref = nil
    }

    private func installHandler() {
        guard handler == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { MainActor.assumeIsolated { GlobalShortcut.shared.action() } }
            return noErr
        }, 1, &spec, nil, &handler)
    }
}

// Options > Buddy List Shortcut: press the new keys.
@MainActor
final class ShortcutWindowController: NSWindowController, NSWindowDelegate {
    private static var shared: ShortcutWindowController?
    private let model = ShortcutModel()
    private var monitor: Any?

    static func show() {
        if shared == nil { shared = ShortcutWindowController() }
        shared?.showWindow(nil)
        shared?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private init() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 170), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "Buddy List Shortcut"
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: ShortcutView(model: model, close: { [weak self] in self?.close() }))
        w.center()
        // While this is open, pressing the old shortcut records it instead of firing it.
        GlobalShortcut.shared.unregister()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self, self.window?.isKeyWindow == true else { return e }
            if e.keyCode == UInt16(kVK_Escape) { self.close(); return nil }
            let mods = GlobalShortcut.Combo.carbon(e.modifierFlags)
            guard GlobalShortcut.Combo.usable(mods) else {
                self.model.message = "Include Command, Option or Control."
                return nil
            }
            self.model.save(GlobalShortcut.Combo(keyCode: UInt32(e.keyCode), modifiers: mods, key: GlobalShortcut.Combo.keyName(e)))
            return nil
        }
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        if let monitor { NSEvent.removeMonitor(monitor) }
        GlobalShortcut.shared.apply()
        ShortcutWindowController.shared = nil
    }
}

@MainActor
final class ShortcutModel: ObservableObject {
    @Published var current = GlobalShortcut.combo?.display ?? "Off"
    @Published var message = "Press the new shortcut. Esc cancels."

    func save(_ c: GlobalShortcut.Combo?) {
        let old = GlobalShortcut.combo
        GlobalShortcut.combo = c
        if GlobalShortcut.shared.apply() {
            current = c?.display ?? "Off"
            message = c == nil ? "The shortcut is off." : "Saved: \(c!.display) brings your buddy lists forward from any app."
        } else {
            message = GlobalShortcut.shared.problem ?? "That shortcut is taken."
            GlobalShortcut.combo = old
        }
        GlobalShortcut.shared.unregister()   // re-registered when the window closes
    }
}

struct ShortcutView: View {
    @ObservedObject var model: ShortcutModel
    let close: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Brings every buddy list to the front from any app:")
                Spacer()
            }
            Text(model.current).font(.system(size: 22, weight: .semibold, design: .rounded))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.panel))
            Text(model.message).font(.system(size: 11)).foregroundStyle(.secondary)
            HStack {
                Button("Turn Off") { model.save(nil) }
                Button("Reset to \(GlobalShortcut.Combo.standard.display)") { model.save(.standard) }
                Spacer()
                Button("Done") { close() }
            }
        }
        .padding(16)
        .frame(width: 360)
    }
}
