import AppKit
import SwiftUI
import UniformTypeIdentifiers

// Full-size profile pictures and banners. Discord's CDN gives the picture at the size it was
// uploaded when asked for size=4096 (it never scales up), and animated ones ("a_" hashes) as GIFs.
enum Picture {
    static func ext(_ hash: String) -> String { hash.hasPrefix("a_") ? "gif" : "png" }

    static func avatar(_ u: User) -> String? {
        guard let a = u.avatar, !a.isEmpty else { return nil }
        return "https://cdn.discordapp.com/avatars/\(u.id)/\(a).\(ext(a))?size=4096"
    }

    static func serverAvatar(_ u: User, guildId: String, hash: String) -> String {
        "https://cdn.discordapp.com/guilds/\(guildId)/users/\(u.id)/avatars/\(hash).\(ext(hash))?size=4096"
    }

    static func banner(_ userId: String, _ hash: String) -> String {
        "https://cdn.discordapp.com/banners/\(userId)/\(hash).\(ext(hash))?size=4096"
    }

    // "alyssa profile picture.gif"
    static func fileName(_ u: User, _ what: String, url: String) -> String {
        let ext = URL(string: url)?.pathExtension ?? "png"
        let name = (u.username ?? u.id).components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: "_")
        return "\(name) \(what).\(ext.isEmpty ? "png" : ext)"
    }

    // Asks where to save, then downloads the original file (an animated GIF stays animated).
    @MainActor static func download(_ url: String, fileName: String, window: NSWindow?, data known: Data? = nil) async -> String {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = fileName
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        if let t = UTType(filenameExtension: (fileName as NSString).pathExtension) { panel.allowedContentTypes = [t] }
        let ok: Bool
        if let window { ok = await panel.beginSheetModal(for: window) == .OK } else { ok = panel.runModal() == .OK }
        guard ok, let dest = panel.url else { return "" }
        do {
            var data = known
            if data == nil, let u = URL(string: url) { data = try await Api.plain.data(from: u).0 }
            try data!.write(to: dest, options: .atomic)
            return "Saved \(dest.lastPathComponent)"
        } catch {
            Log.write("save picture: \(error.localizedDescription)")
            return "Couldn't save it: \(error.localizedDescription)"
        }
    }
}

// Shows one picture at its actual size (scaled down to fit the screen if it's bigger), with
// pinch or Cmd+/- to zoom and a Download button.
@MainActor
final class PictureWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: PictureWindowController] = [:]

    private let url: String
    private let fileName: String
    private let imageView = NSImageView()
    private let scroll = NSScrollView()
    private let info = NSTextField(labelWithString: "Loading...")
    private var data: Data?
    private var pixels = NSSize.zero

    static func show(url: String, title: String, fileName: String) {
        if let w = open[url] { w.window?.makeKeyAndOrderFront(nil); return }
        let w = PictureWindowController(url: url, title: title, fileName: fileName)
        open[url] = w
        w.showWindow(nil)
        w.load()
    }

    private init(url: String, title: String, fileName: String) {
        self.url = url
        self.fileName = fileName
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 360), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = title
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 380, height: 200)
        super.init(window: w)
        w.delegate = self

        imageView.imageScaling = .scaleAxesIndependently
        imageView.animates = true
        imageView.imageFrameStyle = .none
        scroll.contentView = CenteringClipView()
        scroll.documentView = imageView
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.allowsMagnification = true
        scroll.minMagnification = 0.05
        scroll.maxMagnification = 16
        scroll.backgroundColor = .windowBackgroundColor
        scroll.drawsBackground = true

        info.font = .systemFont(ofSize: 11)
        info.textColor = .secondaryLabelColor
        info.lineBreakMode = .byTruncatingTail
        info.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let fit = NSButton(title: "Fit", target: self, action: #selector(zoomToFit(_:)))
        let actual = NSButton(title: "Actual Size", target: self, action: #selector(actualSize(_:)))
        let save = NSButton(title: "Download...", target: self, action: #selector(download(_:)))
        save.keyEquivalent = "s"
        save.keyEquivalentModifierMask = .command
        for b in [fit, actual, save] { b.bezelStyle = .rounded; b.controlSize = .small }
        let bar = NSStackView(views: [info, NSView(), fit, actual, save])
        bar.orientation = .horizontal
        bar.edgeInsets = NSEdgeInsets(top: 6, left: 10, bottom: 6, right: 10)
        let stack = NSStackView(views: [scroll, bar])
        stack.orientation = .vertical
        stack.spacing = 0
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)
        w.contentView = stack
        w.center()
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) { PictureWindowController.open.removeValue(forKey: url) }

    private func load() {
        Task {
            do {
                guard let u = URL(string: url) else { return }
                let (d, resp) = try await Api.plain.data(from: u)
                if let code = (resp as? HTTPURLResponse)?.statusCode, code != 200 { setInfo("Couldn't load the picture (Discord said \(code)).") ; return }
                guard let img = NSImage(data: d), let rep = img.representations.first else { setInfo("Couldn't open the picture."); return }
                data = d
                pixels = NSSize(width: rep.pixelsWide, height: rep.pixelsHigh)
                img.size = pixels   // one picture pixel per point at Actual Size
                imageView.image = img
                imageView.frame = NSRect(origin: .zero, size: pixels)
                let kb = Double(d.count) / 1024
                info.stringValue = "\(Int(pixels.width)) × \(Int(pixels.height)) pixels, " + (kb >= 1024 ? String(format: "%.1f MB", kb / 1024) : String(format: "%.0f KB", kb))
                    + (url.contains(".gif") ? ", animated" : "")
                sizeWindowToPicture()
            } catch {
                setInfo("Couldn't load the picture: \(error.localizedDescription)")
            }
        }
    }

    private func setInfo(_ text: String) {
        info.stringValue = text
        info.toolTip = text
    }

    // As big as the picture, up to most of the screen; then zoomed to fit.
    private func sizeWindowToPicture() {
        guard let w = window, let screen = w.screen ?? NSScreen.main else { return }
        let bar: CGFloat = 36
        let room = screen.visibleFrame.insetBy(dx: 40, dy: 40)
        let scale = min(1, room.width / pixels.width, (room.height - bar) / pixels.height)
        let content = NSSize(width: max(380, pixels.width * scale), height: max(200, pixels.height * scale + bar))
        var frame = w.frameRect(forContentRect: NSRect(origin: .zero, size: content))
        frame.origin = NSPoint(x: room.midX - frame.width / 2, y: room.midY - frame.height / 2)
        w.setFrame(frame, display: true)
        zoomToFit(nil)
    }

    @objc func zoomToFit(_ sender: Any?) {
        guard pixels.width > 0 else { return }
        let box = scroll.contentSize
        scroll.magnification = min(1, box.width / pixels.width, box.height / pixels.height)
    }

    @objc func actualSize(_ sender: Any?) { scroll.magnification = 1 }

    @objc func download(_ sender: Any?) {
        Task {
            let text = await Picture.download(url, fileName: fileName, window: window, data: data)
            if !text.isEmpty { setInfo(text) }
        }
    }
}

// Keeps a picture smaller than the window in the middle instead of the bottom-left corner.
private final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var r = super.constrainBoundsRect(proposedBounds)
        guard let doc = documentView else { return r }
        if r.width > doc.frame.width { r.origin.x = (doc.frame.width - r.width) / 2 }
        if r.height > doc.frame.height { r.origin.y = (doc.frame.height - r.height) / 2 }
        return r
    }
}

// Right-click menu for a profile picture or banner.
struct PictureMenu: View {
    let url: String
    let title: String
    let fileName: String

    var body: some View {
        Button("View Full Size") { PictureWindowController.show(url: url, title: title, fileName: fileName) }
        Button("Download Full Size...") { Task { _ = await Picture.download(url, fileName: fileName, window: NSApp.keyWindow) } }
    }
}
