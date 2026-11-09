import AppKit
import SwiftUI

// Block and unblock, and Messenger > Blocked Users. Discord keeps the block list; it arrives with
// the other relationships (type 2) when Buzzcord connects. Discord never says who blocked you.
@MainActor
enum Blocking {
    static let explanation = "They won't be able to message or call you, and their messages in servers and group chats are hidden. If you're friends, this also removes them as a friend. They aren't told."

    // Asks first. Returns at once; a failure is logged and shown.
    static func block(_ session: Session, _ id: String, location: String) {
        guard confirm("Block \(session.name(id))?\n\n\(explanation)", ok: "Block") else { return }
        run(session, "block", id) { try await session.block(id, location: location) }
    }

    static func unblock(_ session: Session, _ id: String, location: String) {
        run(session, "unblock", id) { try await session.unblock(id, location: location) }
    }

    private static func run(_ session: Session, _ what: String, _ id: String, _ op: @escaping () async throws -> Void) {
        Log.write("relationship: \(what) \(id)")
        Task { @MainActor in
            do { try await op() } catch {
                Log.write("relationship: \(what) \(id) failed: \(error.localizedDescription)")
                showError("Couldn't \(what) \(session.name(id)): \(error.localizedDescription)")
            }
        }
    }

    // Blocked users by name.
    static func list(_ session: Session) -> [String] {
        session.relationships(ofType: 2).sorted { session.name($0).localizedCaseInsensitiveCompare(session.name($1)) == .orderedAscending }
    }
}

@MainActor
final class BlockedWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: BlockedWindowController] = [:]
    private let key: String

    static func show(_ account: AccountController) {
        let key = account.session.tokenKey
        if let w = open[key] { w.window?.makeKeyAndOrderFront(nil); return }
        let w = BlockedWindowController(account, key: key)
        open[key] = w
        w.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private init(_ account: AccountController, key: String) {
        self.key = key
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 420), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "Blocked Users" + (account.session.me.map { " - " + $0.displayName } ?? "")
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 300, height: 260)
        super.init(window: w)
        w.delegate = self
        // Fixed size: the list scrolls instead of the window growing with it.
        let host = NSHostingView(rootView: BlockedView(session: account.session, account: account))
        host.sizingOptions = []
        w.contentView = host
        w.setContentSize(NSSize(width: 360, height: 420))
        w.center()
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) { BlockedWindowController.open[key] = nil }
}

struct BlockedView: View {
    @ObservedObject var session: Session
    weak var account: AccountController?

    var body: some View {
        let ids = Blocking.list(session)
        VStack(alignment: .leading, spacing: 0) {
            Text(ids.isEmpty ? "You haven't blocked anyone." : "Blocked (\(ids.count))")
                .font(.system(size: 12, weight: .bold)).foregroundStyle(Theme.accent)
                .padding(.horizontal, 12).padding(.vertical, 8)
            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(ids, id: \.self) { id in row(id); Divider() }
                }
            }
            Divider()
            Text("Block someone from their profile or by right-clicking them in the buddy list. Unblocking doesn't make you friends again.")
                .font(.system(size: 11)).foregroundStyle(Theme.sub).fixedSize(horizontal: false, vertical: true)
                .padding(12)
        }
        .frame(minWidth: 300, maxWidth: .infinity, minHeight: 260, maxHeight: .infinity)
        .background(Theme.panel)
    }

    private func row(_ id: String) -> some View {
        let u = session.user(id)
        return HStack(spacing: 8) {
            RemoteImage(url: u.avatarUrl(64), corner: 3).frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text(session.name(id)).font(.system(size: 12, weight: .bold)).lineLimit(1)
                if let name = u.username { Text(name).font(.system(size: 11)).foregroundStyle(Theme.sub).lineLimit(1) }
            }
            Spacer()
            Button("Unblock") { Blocking.unblock(session, id, location: "Blocked Users") }
        }
        .padding(.horizontal, 12).padding(.vertical, 6)
        .contentShape(Rectangle())
        .contextMenu { Button("View Profile") { account?.showProfile(id, guildId: nil) } }
    }
}
