import AppKit
import SwiftUI

// Help > Donate, like the Windows DonateForm: PayPal for everyone, plus GCash and InstaPay bank
// QR codes for the Philippines. The QR pictures ship inside the app (Resources/donate, copied
// from assets/donate by build.sh).
@MainActor
final class DonateWindowController: NSWindowController, NSWindowDelegate {
    private static var shared: DonateWindowController?

    static func show() {
        if shared == nil { shared = DonateWindowController() }
        shared?.showWindow(nil)
        shared?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private init() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 380, height: 300), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "Donate to Buzzcord"
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        var fit: () -> Void = {}
        let host = NSHostingView(rootView: DonateView(resize: { fit() }))
        w.contentView = host
        // The window grows when a QR code shows and shrinks back, keeping its top edge.
        fit = { [weak w, weak host] in
            guard let w, let host else { return }
            let size = host.fittingSize
            var f = w.frameRect(forContentRect: NSRect(origin: .zero, size: size))
            f.origin = NSPoint(x: w.frame.minX, y: w.frame.maxY - f.height)
            w.setFrame(f, display: true, animate: w.isVisible)
        }
        fit()
        w.center()
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) { DonateWindowController.shared = nil }
}

enum DonateQR: String, CaseIterable {
    case gcash, bank

    var title: String { self == .gcash ? "GCash (Philippines)" : "Bank transfer (Philippines)" }
    var caption: String {
        self == .gcash ? "Scan with GCash, or any bank app that takes InstaPay QR." : "Scan with your bank's app (InstaPay QR)."
    }

    static var bundle: Bundle = .main
    var url: URL? { DonateQR.bundle.url(forResource: rawValue, withExtension: "png", subdirectory: "donate") }
    var image: NSImage? { url.flatMap(NSImage.init(contentsOf:)) }
}

struct DonateView: View {
    static var demoShown: DonateQR?   // --demo --donate gcash|bank
    @State private var shown: DonateQR? = DonateView.demoShown
    var resize: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 44, height: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Buy me a coffee").font(.system(size: 18, weight: .heavy)).italic().foregroundStyle(Theme.accent)
                    Text("Thank you for keeping Buzzcord going!").font(.system(size: 11)).foregroundStyle(Theme.sub)
                }
                Spacer()
            }
            .padding(14)
            .background(Theme.header)

            VStack(alignment: .leading, spacing: 8) {
                Text("Buzzcord is free, with no ads and no tracking. If it made your day a little better, a small tip keeps it going and helps pay for code signing, so macOS stops warning about it.")
                    .font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                choice("PayPal (International)", selected: false) { openExternal(AppDelegate.donateUrl) }
                ForEach(DonateQR.allCases, id: \.self) { q in
                    choice(q.title, selected: shown == q) { shown = shown == q ? nil : q }   // a second click hides it
                }
                if let q = shown {
                    VStack(spacing: 6) {
                        if let img = q.image {
                            Image(nsImage: img).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                                .frame(width: 260, height: 260)
                                .background(Color.white)
                        } else {
                            Text("The QR code is missing from this copy of Buzzcord.").foregroundStyle(.red)
                        }
                        Text(q.caption).font(.system(size: 11)).foregroundStyle(Theme.sub).multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.top, 4)
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 16)
        }
        .frame(width: 380)
        .fixedSize(horizontal: false, vertical: true)
        .background(Theme.panel)
        .onChange(of: shown) { DispatchQueue.main.async { resize() } }
    }

    private func choice(_ title: String, selected: Bool, _ action: @escaping () -> Void) -> some View {
        let label = Text(title).font(.system(size: 12, weight: .bold)).frame(maxWidth: .infinity).padding(.vertical, 4)
        return Group {
            if selected { Button(action: action) { label }.buttonStyle(.borderedProminent).tint(Theme.accent) }
            else { Button(action: action) { label }.buttonStyle(.bordered) }
        }
        .controlSize(.large)
    }
}
