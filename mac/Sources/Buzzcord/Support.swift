import AppKit
import Security
import SwiftUI
import UserNotifications

// Account tokens live in the login keychain (the Mac counterpart of DPAPI on Windows), as one
// item holding the list, so macOS asks at most once for access.
enum Accounts {
    private static let service = "Buzzcord"
    private static let account = "accounts"

    static func load() -> [String] {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data,
              let list = Json.parse(data) as? [String] else { return [] }
        return list
    }

    static func save(_ tokens: [String]) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        let data = Json.data(tokens)
        if SecItemUpdate(q as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecItemNotFound {
            var add = q
            add[kSecValueData as String] = data
            add[kSecAttrLabel as String] = "Buzzcord accounts"
            SecItemAdd(add as CFDictionary, nil)
        }
    }
}

// Avatars, server icons and emoji, kept in memory only (a few MB at most).
@MainActor
final class ImageCache {
    static let shared = ImageCache()
    private let cache: NSCache<NSString, NSImage> = { let c = NSCache<NSString, NSImage>(); c.totalCostLimit = 40 << 20; return c }()
    private var inflight: [String: [(NSImage?) -> Void]] = [:]

    func cached(_ url: String) -> NSImage? { cache.object(forKey: url as NSString) }

    func load(_ url: String, _ done: @escaping (NSImage?) -> Void) {
        if let img = cached(url) { done(img); return }
        if inflight[url] != nil { inflight[url]!.append(done); return }
        inflight[url] = [done]
        Task {
            var img: NSImage?
            if let u = URL(string: url), let (data, _) = try? await Api.plain.data(from: u) { img = NSImage(data: data) }
            if let img { cache.setObject(img, forKey: url as NSString, cost: Int(img.size.width * img.size.height * 4)) }
            let waiting = inflight.removeValue(forKey: url) ?? []
            for w in waiting { w(img) }
        }
    }

    func get(_ url: String) async -> NSImage? {
        await withCheckedContinuation { c in load(url) { c.resume(returning: $0) } }
    }
}

struct RemoteImage: View {
    let url: String?
    var corner: CGFloat = 0
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            if let image {
                Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fill)
            } else {
                Theme.panel
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: corner))
        .task(id: url) {
            guard let url else { image = nil; return }
            image = ImageCache.shared.cached(url)
            if image == nil { image = await ImageCache.shared.get(url) }
        }
    }
}

// The Yahoo-ish palette, in light and dark.
enum Theme {
    static func dyn(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { ap in
            let v = ap.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: 1)
        })
    }

    static let headerTop = dyn(0xFFF3B0, 0x3A3150)
    static let headerBottom = dyn(0xFFD94A, 0x2A2438)
    static let group = dyn(0xE6DEF5, 0x2C2640)
    static let groupText = dyn(0x3B2470, 0xD6C8F5)
    static let panel = dyn(0xF4F1FA, 0x1E1B26)
    static let window = dyn(0xFFFFFF, 0x17141F)
    static let accent = dyn(0x5B2C8F, 0xC4A8F0)
    static let accentLight = dyn(0xC9BFE2, 0x4D3F6B)
    static let sub = dyn(0x777777, 0x9A93AE)
    static let activity = dyn(0x2E8B2E, 0x7BD67B)
    static let selected = dyn(0xDCD0F5, 0x3B2F57)
    static let buzz = Color(red: 0.82, green: 0.2, blue: 0.2)

    static var header: LinearGradient { LinearGradient(colors: [headerTop, headerBottom], startPoint: .top, endPoint: .bottom) }

    static func statusLabel(_ s: String) -> String {
        switch s {
        case "online": return "Available"
        case "idle": return "Away"
        case "dnd": return "Busy"
        case "invisible": return "Invisible"
        default: return "Offline"
        }
    }
}

struct StatusIcon: View {
    let status: String
    var size: CGFloat = 16

    var body: some View {
        // Yahoo-style: a yellow smiley when available.
        let (fill, symbol): (Color, String) = switch status {
        case "online": (Color(red: 1, green: 0.83, blue: 0.15), "face.smiling")
        case "idle": (Color.orange, "moon.fill")
        case "dnd": (Color(red: 0.9, green: 0.2, blue: 0.2), "minus")
        default: (Color.gray.opacity(0.55), "face.smiling")
        }
        ZStack {
            Circle().fill(fill)
            Circle().stroke(Color.black.opacity(0.25), lineWidth: 0.5)
            Image(systemName: symbol)
                .font(.system(size: size * (symbol == "face.smiling" ? 0.95 : 0.55), weight: .bold))
                .foregroundStyle(status == "online" || status == "offline" ? Color.black.opacity(0.7) : Color.white)
        }
        .frame(width: size, height: size)
    }
}

// Pop-ups and sounds, and the Dock badge.
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    static let shared = Notifier()
    var onClick: ((String, String) -> Void)?   // account token key, channel id
    var onReply: ((String, String, String, String) -> Void)?   // account, channel, message id, text
    var onMarkRead: ((String, String) -> Void)?   // account, channel

    func setUp() {
        let c = UNUserNotificationCenter.current()
        c.delegate = self
        c.requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
        // Message banners get Reply (type it right in the banner) and Mark as Read.
        let reply = UNTextInputNotificationAction(identifier: "reply", title: "Reply", options: [],
                                                  textInputButtonTitle: "Send", textInputPlaceholder: "Message")
        let read = UNNotificationAction(identifier: "read", title: "Mark as Read", options: [])
        c.setNotificationCategories([UNNotificationCategory(identifier: "message", actions: [reply, read], intentIdentifiers: [])])
    }

    // message: the message being alerted about; banners for messages can be answered in place.
    func alert(account: String, channel: String?, title: String, body: String, sound: String? = "Pop", message: String? = nil) {
        if let sound, Prefs.bool("Sounds", true) { NSSound(named: NSSound.Name(sound))?.play() }
        guard Prefs.bool("Banners", true) else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body.count > 240 ? String(body.prefix(240)) + "..." : body
        content.userInfo = ["account": account, "channel": channel ?? "", "message": message ?? ""]
        if message != nil && channel != nil { content.categoryIdentifier = "message" }
        content.threadIdentifier = channel ?? account
        let req = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(req)
    }

    static func play(_ name: String) {
        if Prefs.bool("Sounds", true) { NSSound(named: NSSound.Name(name))?.play() }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        let account = info["account"] as? String ?? ""
        let channel = info["channel"] as? String ?? ""
        let message = info["message"] as? String ?? ""
        let action = response.actionIdentifier
        let text = (response as? UNTextInputNotificationResponse)?.userText.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        Task { @MainActor in
            switch action {
            case "reply":
                if !text.isEmpty { Notifier.shared.onReply?(account, channel, message, text) }
            case "read":
                Notifier.shared.onMarkRead?(account, channel)
            case UNNotificationDismissActionIdentifier:
                break
            default:
                NSApp.activate(ignoringOtherApps: true)
                Notifier.shared.onClick?(account, channel)
            }
        }
        completionHandler()
    }
}

// A small text prompt (Add a Friend, custom status...).
@MainActor
func askText(title: String, message: String, initial: String = "", ok: String = "OK", window: NSWindow?) -> String? {
    let alert = NSAlert()
    alert.messageText = title
    alert.informativeText = message
    alert.addButton(withTitle: ok)
    alert.addButton(withTitle: "Cancel")
    let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
    field.stringValue = initial
    alert.accessoryView = field
    alert.window.initialFirstResponder = field
    return alert.runModal() == .alertFirstButtonReturn ? field.stringValue : nil
}

@MainActor
func showError(_ text: String, title: String = "Buzzcord") {
    let alert = NSAlert()
    alert.messageText = title
    alert.informativeText = text
    alert.runModal()
}

@MainActor
func confirm(_ text: String, ok: String) -> Bool {
    let alert = NSAlert()
    alert.messageText = text
    alert.addButton(withTitle: ok)
    alert.addButton(withTitle: "Cancel")
    return alert.runModal() == .alertFirstButtonReturn
}

func openExternal(_ url: String) {
    let l = url.lowercased()
    guard l.hasPrefix("http://") || l.hasPrefix("https://"), let u = URL(string: url) else { return }
    NSWorkspace.shared.open(u)
}
