import AppKit
import SwiftUI

// Discord's profile game widgets ("Board"): Favorite, Games in Rotation, Games I Like and
// Want to Play. Read from a profile's "widgets"; changed by replacing the whole set with
// PUT /users/@me/widgets.
enum GameBoard {
    struct Kind: Identifiable {
        let type: String, title: String, max: Int
        var id: String { type }
        // Favorite and Rotation are the "detailed" widgets: they carry a comment and tags.
        var detailed: Bool { type == "favorite_games" || type == "current_games" }
    }

    static let kinds = [
        Kind(type: "favorite_games", title: "Favorite", max: 1),
        Kind(type: "current_games", title: "Games in Rotation", max: 5),
        Kind(type: "played_games", title: "Games I Like", max: 20),
        Kind(type: "want_to_play_games", title: "Want to Play", max: 20)]

    static func kind(_ type: String) -> Kind? { kinds.first { $0.type == type } }

    static let changed = Notification.Name("BuzzcordGameBoardChanged")   // object: the Session

    struct Game {
        let id: String
        var name: String?
        var icon: String?
        var iconUrl: String? { icon.map { "https://cdn.discordapp.com/app-icons/\(id)/\($0).png?size=128" } }
        var title: String { name ?? "Game \(id)" }
    }

    @MainActor private static var cache: [String: Game] = [:]

    @MainActor static func cached(_ appId: String) -> Game? { cache[appId] }

    // Name and picture of a game, from Discord's public application info. Cached.
    @MainActor static func info(_ appId: String) async -> Game {
        if let g = cache[appId] { return g }
        var g = Game(id: appId)
        do {
            let text = try await Api.getString(Api.base + "/applications/\(appId)/rpc")
            if let j = Json.parse(Data(text.utf8)) as? JObj { g.name = j.str("name"); g.icon = j.str("icon") }
        } catch { Log.write("game info \(appId): \(error.localizedDescription)") }
        if g.name == nil { g.name = GameCatalog.shared.name(of: appId) }
        cache[appId] = g
        return g
    }

    @MainActor static func fetchAll(_ ids: [String]) async {
        await withTaskGroup(of: Void.self) { group in
            for id in Set(ids) where cache[id] == nil { group.addTask { _ = await info(id) } }
        }
    }

    static func gameIds(_ widgets: [Any]?) -> [String] {
        (widgets ?? []).compactMap { ($0 as? JObj)?.obj("data") }.flatMap { $0.objs("games").compactMap { $0.str("game_id") } }
    }

    // Each game once, in the order first seen.
    static func uniqueIds(_ widgets: [Any]?) -> [String] {
        var seen = Set<String>()
        return gameIds(widgets).filter { seen.insert($0).inserted }
    }

    static func entries(_ widgets: [Any]?, _ type: String) -> [JObj] {
        for w in widgets ?? [] {
            if let data = (w as? JObj)?.obj("data"), data.str("type") == type { return data.objs("games") }
        }
        return []
    }

    static func tagText(_ tag: String) -> String {
        let t = tag.replacingOccurrences(of: "_", with: " ")
        return t.prefix(1).uppercased() + t.dropFirst()
    }

    // MARK: - changing your own board

    // Applies one change to the widget list. Returns nil when it worked, else why not.
    static func change(_ widgets: inout [Any], type: String, appId: String, add: Bool) -> String? {
        guard let kind = kind(type) else { return "Unknown list." }
        var at = widgets.lastIndex { ($0 as? JObj)?.obj("data")?.str("type") == type }
        if at == nil {
            if !add { return nil }
            widgets.append(["data": ["type": type, "games": [Any]()] as JObj] as JObj)
            at = widgets.count - 1
        }
        var w = widgets[at!] as! JObj
        var data = w.obj("data")!
        var games = data.arr("games") ?? []
        let existing = games.firstIndex { ($0 as? JObj)?.str("game_id") == appId }
        if !add {
            if let existing { games.remove(at: existing) }
        } else {
            if existing != nil { return "It's already in \(kind.title)." }
            if kind.max == 1 { games.removeAll() }   // a new favorite replaces the old one
            else if games.count >= kind.max { return "\(kind.title) is full (\(kind.max) games). Remove one first." }
            games.insert(["game_id": appId] as JObj, at: 0)
        }
        data["games"] = games
        w["data"] = data
        widgets[at!] = w
        return nil
    }

    // Loads, changes and saves in one go. Returns nil when it worked, else a message.
    @MainActor static func update(_ s: Session, type: String, appId: String, add: Bool) async -> String? {
        guard let me = s.me else { return "Not signed in yet." }
        do {
            let p = try await s.api.get("/users/\(me.id)/profile") as? JObj
            var widgets = p?.arr("widgets") ?? []
            if let problem = change(&widgets, type: type, appId: appId, add: add) { return problem }
            let body: [Any] = widgets.compactMap { o in
                guard let w = o as? JObj, let data = w.obj("data") else { return nil }
                var out: JObj = ["data": data]
                if let id = w.str("id") { out["id"] = id }
                return out
            }
            try await s.api.put("/users/@me/widgets", ["widgets": body])
            NotificationCenter.default.post(name: changed, object: s)
            return nil
        } catch {
            Log.write("game board: \(error.localizedDescription)")
            return error.localizedDescription
        }
    }

    // "Add <game> to my profile" > Favorite / Games in Rotation / ..., for profiles and the buddy list.
    @MainActor static func addMenu(_ s: Session, appId: String, name: String?, report: @escaping (String) -> Void) -> some View {
        Menu("Add \(name ?? "this game") to My Profile") {
            ForEach(kinds) { kind in
                Button(kind.title) {
                    Task {
                        let problem = await update(s, type: kind.type, appId: appId, add: true)
                        report(problem ?? "\(name ?? "The game") added to \(kind.title).")
                    }
                }
            }
        }
    }
}

// Discord's public list of detectable games (about 25,000), for the game picker and for
// spotting games running on this Mac. The 13 MB download is boiled down to a small file in
// Application Support and refreshed weekly.
final class GameCatalog: @unchecked Sendable {
    static let shared = GameCatalog()

    struct Entry: Identifiable, Hashable { let id: String; let name: String }

    private let lock = NSLock()
    private var names: [String: String] = [:]          // app id -> name
    private var byName: [String: String] = [:]         // lowercased name -> app id
    private var macApps: [(pattern: String, id: String)] = []   // Discord's darwin executables
    private var loadTask: Task<Void, Never>?
    static let cacheUrl = AppPaths.support.appendingPathComponent("detectable.tsv")

    var isLoaded: Bool { lock.withLock { !names.isEmpty } }

    func name(of id: String) -> String? { lock.withLock { names[id] } }

    func id(named name: String) -> String? { lock.withLock { byName[name.lowercased()] } }

    // Loads the cached list, downloading it first when it's missing or a week old.
    func ensureLoaded() async {
        let task = lock.withLock { () -> Task<Void, Never> in
            if let t = loadTask { return t }
            let t = Task.detached(priority: .utility) { [self] in
                let url = GameCatalog.cacheUrl
                let age = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate).map { Date().timeIntervalSince($0) }
                if age == nil || age! > 7 * 86400 { await refresh() }
                read()
            }
            loadTask = t
            return t
        }
        await task.value
    }

    private func refresh() async {
        do {
            let (data, _) = try await Api.plain.data(from: URL(string: Api.base + "/applications/detectable")!)
            guard let list = Json.parse(data) as? [Any] else { return }
            var out = ""
            for case let app as JObj in list {
                guard let id = app.str("id"), let name = clean(app.str("name")) else { continue }
                out += "n\t\(id)\t\(name)\n"
                for exe in app.objs("executables") where exe.str("os") == "darwin" && !exe.bool("is_launcher") {
                    guard let n = clean(exe.str("name"))?.lowercased(), !Self.generic.contains(n) else { continue }
                    out += "x\t\(id)\t\(n.hasPrefix(">") ? String(n.dropFirst()) : n)\n"
                }
            }
            try Data(out.utf8).write(to: Self.cacheUrl, options: .atomic)
        } catch { Log.write("detectable refresh: \(error.localizedDescription)") }
    }

    private func clean(_ s: String?) -> String? {
        guard let s, !s.isEmpty else { return nil }
        return s.replacingOccurrences(of: "\t", with: " ").replacingOccurrences(of: "\n", with: " ")
    }

    private static let generic: Set<String> = [">java", "java", "python", "node", "wine", "wine64", "love", "dosbox", "retroarch"]

    private func read() {
        if let text = try? String(contentsOf: Self.cacheUrl, encoding: .utf8) { parse(text) }
    }

    func parse(_ text: String) {
        var n: [String: String] = [:], b: [String: String] = [:], x: [(String, String)] = []
        for line in text.split(separator: "\n") {
            let p = line.split(separator: "\t", maxSplits: 2).map(String.init)
            guard p.count == 3 else { continue }
            if p[0] == "n" { n[p[1]] = p[2]; if b[p[2].lowercased()] == nil { b[p[2].lowercased()] = p[1] } }
            else if p[0] == "x" { x.append((p[2], p[1])) }
        }
        lock.withLock { names = n; byName = b; macApps = x }
    }

    // Games whose name contains the text, names starting with it first, then shorter names.
    func search(_ text: String, max: Int = 60) -> [Entry] {
        let t = text.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return [] }
        let all = lock.withLock { names }
        var found = all.compactMap { id, name in name.range(of: t, options: [.caseInsensitive, .diacriticInsensitive]) != nil ? Entry(id: id, name: name) : nil }
        found.sort { a, b in
            let sa = a.name.lowercased().hasPrefix(t.lowercased()), sb = b.name.lowercased().hasPrefix(t.lowercased())
            if sa != sb { return sa }
            if a.name.count != b.name.count { return a.name.count < b.name.count }
            return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }
        return Array(found.prefix(max))
    }

    // The game for a running program, from Discord's darwin executable names
    // ("hearthstone.app", "_retail_/world of warcraft.app", ">hl2_osx").
    func match(path: String) -> String? {
        let p = "/" + path.lowercased()
        return lock.withLock {
            macApps.first { p.hasSuffix("/" + $0.pattern) || p.contains("/" + $0.pattern + "/") }?.id
        }
    }
}

// "Playing X" for games running on this Mac, shared on every signed-in account. Off until
// turned on in Options (Show What I'm Playing, which also starts rich presence). Only the list of running apps is read; no game is ever touched.
@MainActor
final class GameDetector: ObservableObject {
    static let shared = GameDetector()

    struct Running: Identifiable, Equatable {
        let key: String, name: String, appId: String?, start: Int64
        var id: String { key }
    }

    @Published private(set) var running: [Running] = []
    private var timer: Timer?
    private var isGame: [String: Bool] = [:]   // bundle path -> looks like a game

    static var enabled: Bool { demo || Prefs.bool("DetectGames", false) }
    static var demo = false   // --demo --playing
    static var hidden: [String] { Prefs.strings("HiddenGames") }

    static func hide(_ name: String) {
        Prefs.set("HiddenGames", hidden.filter { $0 != name } + [name])
        shared.scan()
        Playing.changed()
    }

    static func unhideAll() { Prefs.set("HiddenGames", [String]()); shared.scan(); Playing.changed() }

    // Activities for the gateway: what's running, oldest first.
    var activities: [JObj] {
        running.map { g in
            var a: JObj = ["name": g.name, "type": 0, "timestamps": ["start": g.start]]
            if let id = g.appId { a["application_id"] = id }
            return a
        }
    }

    func start() {
        guard Self.enabled else { stop(); return }
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { _ in MainActor.assumeIsolated { GameDetector.shared.scan() } }
            for n in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
                NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(appsChanged), name: n, object: nil)
            }
        }
        Task {
            await GameCatalog.shared.ensureLoaded()
            scan()
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        scan()
    }

    @objc private func appsChanged(_ n: Notification) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { MainActor.assumeIsolated { GameDetector.shared.scan() } }
    }

    func scan(force: Bool = false) {
        var now: [Running] = []
        if Self.enabled || force {
            let hidden = Set(Self.hidden)
            for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
                guard let found = identify(app), !hidden.contains(found.name), !now.contains(where: { $0.key == found.key }) else { continue }
                now.append(running.first { $0.key == found.key } ?? found)
            }
        }
        if now != running {
            running = now
            Log.write("games: " + (now.isEmpty ? "none" : now.map(\.name).joined(separator: ", ")))
            Playing.changed()
        }
    }

    private func identify(_ app: NSRunningApplication) -> Running? {
        let start = Int64((app.launchDate ?? Date()).timeIntervalSince1970 * 1000)
        // Discord's own list of Mac games first.
        if let path = app.executableURL?.path, let id = GameCatalog.shared.match(path: path) {
            let name = GameCatalog.shared.name(of: id) ?? app.localizedName ?? "a game"
            return Running(key: id, name: name, appId: id, start: start)
        }
        // Otherwise an app filed under Games, or anything Steam installed.
        guard let bundle = app.bundleURL, let name = app.localizedName, looksLikeGame(app, bundle) else { return nil }
        let id = GameCatalog.shared.id(named: name)
        return Running(key: id ?? "name:" + name, name: id.flatMap { GameCatalog.shared.name(of: $0) } ?? name, appId: id, start: start)
    }

    private static let notGames: Set<String> = ["com.valvesoftware.steam", "com.epicgames.EpicGamesLauncher", "net.battle.app",
                                                 "com.blizzard.Battle.net", "com.hnc.Discord", "app.buzzcord.mac"]

    private func looksLikeGame(_ app: NSRunningApplication, _ bundle: URL) -> Bool {
        let key = bundle.path
        if let known = isGame[key] { return known }
        let bid = app.bundleIdentifier ?? ""
        var game = false
        if !Self.notGames.contains(bid) && !bid.hasPrefix("com.apple.") {
            let category = Bundle(url: bundle)?.object(forInfoDictionaryKey: "LSApplicationCategoryType") as? String ?? ""
            game = category.contains("games") || key.lowercased().contains("/steamapps/common/")
        }
        isGame[key] = game
        return game
    }
}

// Picks a game by name from Discord's list.
struct GamePickerView: View {
    let title: String
    let onPick: (GameCatalog.Entry?) -> Void
    @State private var text = ""
    @State private var results: [GameCatalog.Entry] = []
    @State private var selection: GameCatalog.Entry.ID?
    @State private var loading = !GameCatalog.shared.isLoaded

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            TextField("Search games...", text: $text)
                .textFieldStyle(.roundedBorder)
                .onChange(of: text) { refresh() }
                .onSubmit { pick() }
            List(results, selection: $selection) { g in
                Text(g.name).tag(g.id)
                    .onTapGesture(count: 2) { selection = g.id; pick() }
                    .onTapGesture { selection = g.id }
            }
            .overlay {
                if loading { ProgressView("Getting Discord's game list...").controlSize(.small) }
                else if results.isEmpty && !text.isEmpty { Text("No games found.").foregroundStyle(Theme.sub) }
            }
            HStack {
                Spacer()
                Button("Cancel") { onPick(nil) }.keyboardShortcut(.cancelAction)
                Button("Add") { pick() }.keyboardShortcut(.defaultAction).disabled(selection == nil)
            }
        }
        .padding(14)
        .frame(width: 340, height: 380)
        .task {
            await GameCatalog.shared.ensureLoaded()
            loading = false
            refresh()
        }
    }

    private func refresh() {
        results = GameCatalog.shared.search(text)
        selection = results.first?.id
    }

    private func pick() {
        guard let id = selection, let g = results.first(where: { $0.id == id }) else { return }
        onPick(g)
    }
}

// The Board on a profile. Your own profile also shows empty lists, with an add button on each.
struct BoardView: View {
    @ObservedObject var session: Session
    let widgets: [Any]?
    let games: [String: GameBoard.Game]
    let isMe: Bool
    let report: (String) -> Void
    @State private var picking: GameBoard.Kind?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(GameBoard.kinds) { kind in
                let entries = GameBoard.entries(widgets, kind.type)
                if !entries.isEmpty || isMe { section(kind, entries) }
            }
        }
        .sheet(item: $picking) { kind in
            GamePickerView(title: "Add to \(kind.title)") { g in
                picking = nil
                guard let g else { return }
                Task {
                    let problem = await GameBoard.update(session, type: kind.type, appId: g.id, add: true)
                    report(problem ?? "\(g.name) added to \(kind.title).")
                }
            }
        }
    }

    private func section(_ kind: GameBoard.Kind, _ entries: [JObj]) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Text(kind.title.uppercased() + (kind.max > 1 ? " (\(entries.count))" : ""))
                    .font(.system(size: 10, weight: .bold)).foregroundStyle(Theme.sub)
                if isMe && (kind.max == 1 || entries.count < kind.max) {
                    Button(entries.isEmpty || kind.max > 1 ? "+ add" : "change") { picking = kind }
                        .buttonStyle(.plain).font(.system(size: 10)).foregroundStyle(Theme.accent)
                }
            }
            if entries.isEmpty { Text("Nothing here yet.").font(.system(size: 11)).foregroundStyle(Theme.sub) }
            if kind.detailed {
                ForEach(entries.indices, id: \.self) { i in detailed(kind, entries[i]) }
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 64), spacing: 6, alignment: .top)], alignment: .leading, spacing: 6) {
                    ForEach(entries.indices, id: \.self) { i in tile(kind, entries[i]) }
                }
            }
        }
    }

    private func game(_ e: JObj) -> GameBoard.Game { games[e.str("game_id") ?? ""] ?? GameBoard.Game(id: e.str("game_id") ?? "") }

    private func icon(_ g: GameBoard.Game, _ size: CGFloat) -> some View {
        Group {
            if let url = g.iconUrl { RemoteImage(url: url, corner: 6) }
            else { RoundedRectangle(cornerRadius: 6).fill(Theme.accentLight) }
        }
        .frame(width: size, height: size)
    }

    private func detailed(_ kind: GameBoard.Kind, _ e: JObj) -> some View {
        let g = game(e)
        return HStack(alignment: .top, spacing: 8) {
            icon(g, 40)
            VStack(alignment: .leading, spacing: 3) {
                Text(g.title).font(.system(size: 12, weight: .bold))
                let tags = e.strs("tags")
                if !tags.isEmpty {
                    HStack(spacing: 4) {
                        ForEach(tags, id: \.self) { t in
                            Text(GameBoard.tagText(t)).font(.system(size: 10))
                                .padding(.horizontal, 5).padding(.vertical, 1)
                                .background(Capsule().fill(Theme.group))
                        }
                    }
                }
                if let c = e.str("comment"), !c.isEmpty { Text(c).font(.system(size: 11)).foregroundStyle(Theme.sub) }
            }
            Spacer(minLength: 0)
        }
        .contentShape(Rectangle())
        .contextMenu { menu(kind, g) }
        .help(g.title)
    }

    private func tile(_ kind: GameBoard.Kind, _ e: JObj) -> some View {
        let g = game(e)
        return VStack(spacing: 3) {
            icon(g, 44)
            Text(g.title).font(.system(size: 10)).lineLimit(2).multilineTextAlignment(.center)
        }
        .frame(width: 64)
        .contentShape(Rectangle())
        .contextMenu { menu(kind, g) }
        .help(g.title)
    }

    @ViewBuilder private func menu(_ kind: GameBoard.Kind, _ g: GameBoard.Game) -> some View {
        if isMe {
            Button("Remove from \(kind.title)") {
                Task {
                    let problem = await GameBoard.update(session, type: kind.type, appId: g.id, add: false)
                    report(problem ?? "\(g.title) removed from \(kind.title).")
                }
            }
        }
        GameBoard.addMenu(session, appId: g.id, name: g.name, report: report)
    }
}
