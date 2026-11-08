import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins
import SwiftUI
import WebKit

// MARK: - sign in

@MainActor
final class LoginModel: ObservableObject {
    @Published var qr: NSImage?
    @Published var status = "Getting a code from Discord..."
    @Published var scannedName: String?
    @Published var scannedAvatar: String?
    @Published var token = ""
    @Published var busy = false
    private var auth: RemoteAuth?
    let done: (String) -> Void

    init(done: @escaping (String) -> Void) { self.done = done }

    func start() {
        auth?.cancel()
        qr = nil
        scannedName = nil
        status = "Getting a code from Discord..."
        let a = RemoteAuth()
        auth = a
        a.onQr = { [weak self] url in
            self?.qr = LoginModel.qrImage(url)
            self?.status = "Open Discord on your phone, tap your profile picture > Scan QR Code, and point it here."
        }
        a.onScanned = { [weak self] name, avatar in
            self?.scannedName = name
            self?.scannedAvatar = avatar
            self?.status = "Confirm the login on your phone."
        }
        a.onSuccess = { [weak self] token in self?.finish(token) }
        a.onFailed = { [weak self] msg in self?.status = msg; self?.qr = nil }
        a.start()
    }

    func stop() { auth?.cancel() }

    func useToken() {
        let t = token.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        guard !t.isEmpty else { return }
        busy = true
        Task {
            defer { busy = false }
            do {
                _ = try await Api(token: t).get("/users/@me")
                finish(t)
            } catch {
                status = (error as? ApiError)?.status == 401 ? "Discord didn't accept that token." : error.localizedDescription
            }
        }
    }

    private func finish(_ token: String) {
        auth?.cancel()
        done(token)
    }

    static func qrImage(_ text: String) -> NSImage? {
        let f = CIFilter.qrCodeGenerator()
        f.message = Data(text.utf8)
        f.correctionLevel = "M"
        guard let out = f.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)) else { return nil }
        let rep = NSCIImageRep(ciImage: out)
        let img = NSImage(size: rep.size)
        img.addRepresentation(rep)
        return img
    }
}

@MainActor
final class LoginWindowController: NSWindowController, NSWindowDelegate {
    let model: LoginModel
    var onClose: (() -> Void)?

    init(done: @escaping (String) -> Void) {
        var close: (() -> Void)?
        model = LoginModel { token in done(token); close?() }
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 590), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "Sign in to Buzzcord"
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: LoginView(model: model))
        w.center()
        close = { [weak self] in self?.window?.close() }
        model.start()
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        model.stop()
        onClose?()
    }
}

struct LoginView: View {
    @ObservedObject var model: LoginModel

    var body: some View {
        VStack(spacing: 12) {
            HStack {
                Text("Buzzcord").font(.system(size: 22, weight: .heavy)).foregroundStyle(Theme.accent)
                Spacer()
            }
            .padding(14)
            .background(Theme.header)
            ZStack {
                Color.white
                if let name = model.scannedName {
                    VStack(spacing: 8) {
                        RemoteImage(url: model.scannedAvatar, corner: 40).frame(width: 80, height: 80)
                        Text(name).font(.headline).foregroundStyle(.black)
                    }
                } else if let qr = model.qr {
                    Image(nsImage: qr).interpolation(.none).resizable().frame(width: 220, height: 220)
                } else {
                    ProgressView()
                }
            }
            .frame(width: 240, height: 240)
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.accentLight))
            Text(model.status).font(.system(size: 12)).multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 20)
            Button("New code") { model.start() }
            Divider().padding(.horizontal, 20)
            VStack(alignment: .leading, spacing: 6) {
                Text("Or paste a token").font(.system(size: 12, weight: .semibold))
                HStack {
                    SecureField("Token", text: $model.token).textFieldStyle(.roundedBorder).onSubmit { model.useToken() }
                    Button("Sign In") { model.useToken() }.disabled(model.busy || model.token.isEmpty)
                }
                Text("Paste your own token only. Never paste one someone sent you: that's how accounts get stolen.")
                    .font(.system(size: 11, weight: .semibold)).foregroundStyle(Color(red: 0.75, green: 0.2, blue: 0.1))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 20)
            Text("Unofficial: Discord doesn't allow third-party clients for user accounts, so using it carries some risk to the accounts you sign in with.")
                .font(.system(size: 10)).foregroundStyle(Theme.sub).multilineTextAlignment(.center).padding(.horizontal, 20)
            Spacer(minLength: 0)
        }
        .frame(width: 360, height: 590)
        .background(Theme.panel)
    }
}

// MARK: - captcha

// Shows Discord's hCaptcha so the user can solve it. The page is served as
// https://discord.com/... from inside the app: hCaptcha only works on the site its key belongs
// to, and nothing is actually fetched from that address.
@MainActor
final class CaptchaWindowController: NSWindowController, NSWindowDelegate, WKScriptMessageHandler {
    private static var busy = false
    private let challenge: JObj
    private var continuation: CheckedContinuation<String?, Never>?
    private let web: WKWebView
    private let note = NSTextField(wrappingLabelWithString: "Discord wants to make sure you're a person before it sends this. Solve the puzzle below; Buzzcord sends your request as soon as you're done.")

    static func ask(_ challenge: JObj) async -> String? {
        if busy { return nil }
        busy = true
        defer { busy = false }
        let c = CaptchaWindowController(challenge)
        return await withCheckedContinuation { k in
            c.continuation = k
            c.showWindow(nil)
            c.window?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    private init(_ challenge: JObj) {
        self.challenge = challenge
        let config = WKWebViewConfiguration()
        web = WKWebView(frame: .zero, configuration: config)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 640), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "Buzzcord - Quick check"
        w.level = .floating
        w.isReleasedWhenClosed = false
        super.init(window: w)
        config.userContentController.add(self, name: "buzz")
        w.delegate = self
        let stack = NSStackView(views: [note, web])
        stack.orientation = .vertical
        stack.edgeInsets = NSEdgeInsets(top: 10, left: 10, bottom: 10, right: 10)
        stack.spacing = 8
        web.translatesAutoresizingMaskIntoConstraints = false
        w.contentView = stack
        w.center()
        web.loadHTMLString(page(), baseURL: URL(string: "https://discord.com/buzzcord-captcha"))
    }

    required init?(coder: NSCoder) { fatalError() }

    func userContentController(_ uc: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let msg = message.body as? String, !msg.isEmpty else { return }
        if msg.hasPrefix("error:") {
            note.stringValue = "The captcha hit a problem (\(msg.dropFirst(6))). Close this window and try again."
            return
        }
        finish(msg)
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        web.configuration.userContentController.removeScriptMessageHandler(forName: "buzz")
        finish(nil)
    }

    private func finish(_ v: String?) {
        continuation?.resume(returning: v)
        continuation = nil
    }

    private func page() -> String {
        let dark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        if (challenge.str("captcha_service") ?? "hcaptcha") != "hcaptcha" {
            return "<p style='font:13px -apple-system;padding:16px'>Discord asked for a kind of captcha Buzzcord doesn't support (" + MessageHtml.e(challenge.str("captcha_service")) + ").</p>"
        }
        return """
        <!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <style>html,body{margin:0;height:100%;background:\(dark ? "#17141f" : "#fff");font:13px -apple-system,sans-serif;}
        body{display:flex;align-items:center;justify-content:center;}</style>
        <script>
        function send(m){window.webkit.messageHandlers.buzz.postMessage(m);}
        function go(){
          var id=hcaptcha.render('box',{sitekey:\(Json.js(challenge.str("captcha_sitekey"))),
            callback:function(t){send(t);},
            'error-callback':function(e){send('error:'+e);},
            'expired-callback':function(){hcaptcha.reset(id);}, theme:'\(dark ? "dark" : "light")'});
          var rq=\(Json.js(challenge.str("captcha_rqdata")));
          if(rq) hcaptcha.setData(id,{rqdata:rq});
        }
        </script>
        <script src="https://js.hcaptcha.com/1/api.js?render=explicit&onload=go&recaptchacompat=off" async defer></script>
        </head><body><div id="box"></div></body></html>
        """
    }
}

// MARK: - profile

@MainActor
final class ProfileModel: ObservableObject {
    @Published var profile: JObj?
    @Published var error: String?
    @Published var games: [String: GameBoard.Game] = [:]
    @Published var note: String?
}

@MainActor
final class ProfileWindowController: NSWindowController, NSWindowDelegate {
    private static var open: [String: ProfileWindowController] = [:]
    private let key: String
    private let model = ProfileModel()
    private let session: Session
    private let userId: String
    private let guildId: String?
    private var boardObserver: NSObjectProtocol?
    static var demoProfiles: [String: JObj] = [:]   // --demo only

    static func open(account: AccountController, userId: String, guildId: String?) {
        let key = account.session.tokenKey + "/" + userId
        if let w = open[key] { w.window?.makeKeyAndOrderFront(nil); return }
        let w = ProfileWindowController(account: account, userId: userId, guildId: guildId, key: key)
        open[key] = w
        w.showWindow(nil)
    }

    private init(account: AccountController, userId: String, guildId: String?, key: String) {
        self.key = key
        session = account.session
        self.userId = userId
        self.guildId = guildId
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 340, height: 460), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        w.title = account.session.name(userId) + " - Profile"
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = NSHostingView(rootView: ProfileView(account: account, session: account.session, userId: userId, guildId: guildId, model: model))
        w.center()
        load()
        // Your own Board changed (here or from the buddy list): show the new lists.
        boardObserver = NotificationCenter.default.addObserver(forName: GameBoard.changed, object: session, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { if let self, self.userId == self.session.me?.id { self.load() } }
        }
    }

    private func load() {
        Task {
            var q = "/users/\(userId)/profile?with_mutual_guilds=false&with_mutual_friends=false"
            if let guildId { q += "&guild_id=\(guildId)" }
            do {
                var p = ProfileWindowController.demoProfiles[userId]
                if p == nil { p = try await session.api.get(q) as? JObj }
                model.profile = p
                let ids = GameBoard.uniqueIds(p?.arr("widgets"))
                await GameBoard.fetchAll(ids)
                // uniqueIds: the same game can be in two lists (Favorite and Games I Like, say).
                model.games = Dictionary(ids.compactMap { id in GameBoard.cached(id).map { (id, $0) } }, uniquingKeysWith: { a, _ in a })
            } catch { model.error = error.localizedDescription }
        }
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        if let boardObserver { NotificationCenter.default.removeObserver(boardObserver) }
        ProfileWindowController.open.removeValue(forKey: key)
    }
}

struct ProfileView: View {
    let account: AccountController
    @ObservedObject var session: Session
    let userId: String
    let guildId: String?
    @ObservedObject var model: ProfileModel

    var body: some View {
        let u = session.user(userId)
        let p = session.presence(userId)
        let profileUser = model.profile?.obj("user")
        let rel = session.relationships[userId] ?? 0
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                ZStack(alignment: .bottomLeading) {
                    banner(profileUser).frame(height: 100).clipped()
                        .contentShape(Rectangle())
                        .onTapGesture { if let m = bannerPicture(u, profileUser) { PictureWindowController.show(url: m.url, title: m.title, fileName: m.fileName) } }
                        .contextMenu { if let m = bannerPicture(u, profileUser) { m } }
                    RemoteImage(url: u.avatarUrl(256), corner: 40).frame(width: 80, height: 80)
                        .overlay(Circle().stroke(Theme.window, lineWidth: 4))
                        .contentShape(Circle())
                        .onTapGesture { if let m = avatarPicture(u) { PictureWindowController.show(url: m.url, title: m.title, fileName: m.fileName) } }
                        .contextMenu {
                            if let m = avatarPicture(u) { m }
                            if let m = serverPicture(u) {
                                Divider()
                                Button("View Server Picture") { PictureWindowController.show(url: m.url, title: m.title, fileName: m.fileName) }
                                Button("Download Server Picture...") { Task { _ = await Picture.download(m.url, fileName: m.fileName, window: NSApp.keyWindow) } }
                            }
                        }
                        .help("Click to see it full size")
                        .offset(x: 14, y: 40)
                }
                .padding(.bottom, 40)
                VStack(alignment: .leading, spacing: 4) {
                    Text(session.name(userId)).font(.system(size: 18, weight: .bold))
                    Text("@" + (u.username ?? profileUser?.str("username") ?? "")).foregroundStyle(Theme.sub)
                    if let pr = model.profile?.obj("user_profile")?.str("pronouns"), !pr.isEmpty { Text(pr).font(.system(size: 11)).foregroundStyle(Theme.sub) }
                    HStack(spacing: 5) {
                        StatusIcon(status: p.status, size: 14)
                        Text(p.customStatus ?? Theme.statusLabel(p.status)).font(.system(size: 12))
                    }
                }
                .padding(.horizontal, 14)
                if let act = p.mainActivity {
                    ActivityCard(activity: act).padding(.horizontal, 14)
                        .contextMenu {
                            // A game with a Discord app id can go on your own Board.
                            if act.long("type") == 0, let app = act.str("application_id") {
                                GameBoard.addMenu(session, appId: app, name: act.str("name")) { model.note = $0 }
                            }
                        }
                }
                if let bio = profileUser?.str("bio") ?? model.profile?.obj("user_profile")?.str("bio"), !bio.isEmpty {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("ABOUT ME").font(.system(size: 10, weight: .bold)).foregroundStyle(Theme.sub)
                        Text(bio).font(.system(size: 12)).textSelection(.enabled)
                    }
                    .padding(.horizontal, 14)
                }
                if model.profile != nil {
                    BoardView(session: session, widgets: model.profile?.arr("widgets"), games: model.games,
                              isMe: userId == session.me?.id) { model.note = $0 }
                        .padding(.horizontal, 14)
                }
                if let n = model.note { Text(n).font(.system(size: 11)).foregroundStyle(Theme.accent).padding(.horizontal, 14) }
                if let e = model.error { Text(e).font(.system(size: 11)).foregroundStyle(Theme.sub).padding(.horizontal, 14) }
                HStack {
                    if userId != session.me?.id {
                        Button("Send IM") { account.openChat(userId: userId) }
                        switch rel {
                        case 1: Button("Remove Friend") {
                            if confirm("Remove \(session.name(userId)) from your friends?", ok: "Remove") {
                                Task { try? await session.removeRelationship(userId, location: "User Profile") }
                            }
                        }
                        case 3: Button("Accept Request") { Task { try? await session.addOrAcceptFriend(userId, location: "User Profile") } }
                        case 4: Button("Cancel Request") { Task { try? await session.removeRelationship(userId, location: "User Profile") } }
                        case 2: EmptyView()
                        default: Button("Add Friend") {
                            Task {
                                do { try await session.addOrAcceptFriend(userId, location: "User Profile") }
                                catch { showError(error.localizedDescription) }
                            }
                        }
                        }
                    }
                }
                .padding(14)
            }
        }
        .background(Theme.window)
        .frame(minWidth: 300, minHeight: 380)
    }

    private func avatarPicture(_ u: User) -> PictureMenu? {
        guard let url = Picture.avatar(u) else { return nil }
        return PictureMenu(url: url, title: session.name(userId) + " - Profile Picture", fileName: Picture.fileName(u, "profile picture", url: url))
    }

    // Their picture for this server, when they set a different one.
    private func serverPicture(_ u: User) -> PictureMenu? {
        guard let guildId, let h = model.profile?.obj("guild_member")?.str("avatar") else { return nil }
        let url = Picture.serverAvatar(u, guildId: guildId, hash: h)
        return PictureMenu(url: url, title: session.name(userId) + " - Server Picture", fileName: Picture.fileName(u, "server picture", url: url))
    }

    private func bannerPicture(_ u: User, _ pu: JObj?) -> PictureMenu? {
        guard let b = pu?.str("banner") else { return nil }
        let url = Picture.banner(userId, b)
        return PictureMenu(url: url, title: session.name(userId) + " - Banner", fileName: Picture.fileName(u, "banner", url: url))
    }

    @ViewBuilder private func banner(_ pu: JObj?) -> some View {
        if let b = pu?.str("banner") {
            RemoteImage(url: "https://cdn.discordapp.com/banners/\(userId)/\(b).png?size=600")
        } else if let c = pu?["accent_color"] as? NSNumber {
            let v = c.intValue
            Color(red: Double((v >> 16) & 0xFF) / 255, green: Double((v >> 8) & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
        } else {
            Theme.header
        }
    }
}
