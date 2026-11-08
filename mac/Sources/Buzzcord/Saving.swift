import AppKit

// Saves every file sent in DMs (and group chats) to Documents/Buzzcord Saved/<account>/<chat>.
// An index of saved attachment ids makes it skip files it already has.
@MainActor
enum AutoSaver {
    private static let indexUrl = AppPaths.support.appendingPathComponent("saved.idx")
    private static var saved: Set<String>?
    private static var queue: [(id: String, url: URL, dest: URL)] = []
    private static var running = false

    static var saveDms: Bool { Prefs.bool("AutoSaveDMs", true) }
    static var saveGroups: Bool { Prefs.bool("AutoSaveGroups", true) }

    static var root: URL {
        if let p = Prefs.string("SaveFolder"), !p.isEmpty { return URL(fileURLWithPath: p, isDirectory: true) }
        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Buzzcord Saved", isDirectory: true)
    }

    static func folder(_ s: Session, _ c: Channel) -> URL {
        let account = clean(s.me?.username ?? "account")
        let chat = clean(c.isDm ? s.title(c) : s.title(c) + (c.isGroup ? " (group)" : ""))
        return root.appendingPathComponent(account, isDirectory: true).appendingPathComponent(chat, isDirectory: true)
    }

    private static func loadIndex() -> Set<String> {
        if let saved { return saved }
        let text = (try? String(contentsOf: indexUrl, encoding: .utf8)) ?? ""
        let set = Set(text.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty })
        saved = set
        return set
    }

    // Call for any message seen, live or from history.
    static func consider(_ s: Session, _ m: JObj) {
        let atts = m.objs("attachments")
        guard !atts.isEmpty, let c = s.findChannel(m.str("channel_id")),
              (c.isDm && saveDms) || (c.isGroup && saveGroups) else { return }
        let done = loadIndex()
        let dir = folder(s, c)
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let when = f.string(from: Snowflake.time(m.str("id")))
        for a in atts {
            guard let id = a.str("id"), let u = a.str("url").flatMap(URL.init(string:)), !done.contains(id),
                  !queue.contains(where: { $0.id == id }) else { continue }
            queue.append((id, u, dir.appendingPathComponent(when + " " + clean(a.str("filename") ?? "file"))))
        }
        pump()
    }

    private static func pump() {
        guard !running, !queue.isEmpty else { return }
        running = true
        let job = queue.removeFirst()
        Task {
            defer { running = false; pump() }
            if loadIndex().contains(job.id) { return }
            do {
                let fm = FileManager.default
                try fm.createDirectory(at: job.dest.deletingLastPathComponent(), withIntermediateDirectories: true)
                var dest = job.dest
                var n = 2
                while fm.fileExists(atPath: dest.path) {
                    let base = job.dest.deletingPathExtension().lastPathComponent, ext = job.dest.pathExtension
                    dest = job.dest.deletingLastPathComponent().appendingPathComponent(base + " (\(n))" + (ext.isEmpty ? "" : "." + ext))
                    n += 1
                }
                let (tmp, resp) = try await Api.plain.download(from: job.url)
                guard ((resp as? HTTPURLResponse)?.statusCode ?? 0) < 300 else { throw ApiError(status: (resp as? HTTPURLResponse)?.statusCode ?? 0, body: nil, raw: "download failed") }
                try fm.moveItem(at: tmp, to: dest)
                saved?.insert(job.id)
                if let h = try? FileHandle(forWritingTo: indexUrl) {
                    h.seekToEndOfFile(); h.write(Data((job.id + "\n").utf8)); try? h.close()
                } else {
                    try? Data((job.id + "\n").utf8).write(to: indexUrl)
                }
            } catch {
                Log.write("autosave \(job.url): \(error.localizedDescription)")
            }
        }
    }

    static func clean(_ name: String) -> String {
        var r = String(name.map { "/:\\\0".contains($0) ? "_" : $0 }).trimmingCharacters(in: .whitespaces)
        while r.hasSuffix(".") { r.removeLast() }
        while r.hasPrefix(".") { r.removeFirst() }
        if r.count > 80 { r = String(r.prefix(80)) }
        return r.isEmpty ? "_" : r
    }

    static func reveal(_ dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(dir)
    }
}

// DM and group chat text, appended to "Chat log.txt" beside the auto-saved files. A
// per-channel last-saved id keeps each message from being written twice.
@MainActor
enum Archive {
    private static let indexUrl = AppPaths.support.appendingPathComponent("archive.idx")
    private static var last: [String: String]?

    static var enabled: Bool { Prefs.bool("ArchiveChats", true) }

    private static func loadIndex() -> [String: String] {
        if let last { return last }
        var d: [String: String] = [:]
        for line in ((try? String(contentsOf: indexUrl, encoding: .utf8)) ?? "").split(separator: "\n") {
            let p = line.split(separator: " ")
            if p.count == 2 { d[String(p[0])] = String(p[1]) }
        }
        last = d
        return d
    }

    // Call for every message seen, oldest first.
    static func consider(_ s: Session, _ m: JObj) {
        guard enabled, let c = s.findChannel(m.str("channel_id")), c.isPrivate, let id = m.str("id") else { return }
        var index = loadIndex()
        if let prev = index[c.id], Snowflake.compare(id, prev) <= 0 { return }
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        var line = "[\(f.string(from: Snowflake.time(id)))] \(s.name(m.obj("author")?.str("id"))): "
        let content = m.str("content") ?? ""
        line += content.replacingOccurrences(of: "\r", with: "").replacingOccurrences(of: "\n", with: "\n    ")
        let files = m.objs("attachments").compactMap { $0.str("filename") }
        if !files.isEmpty { line += " [attachments: \(files.joined(separator: ", "))]" }
        if content.isEmpty && files.isEmpty && m.arr("sticker_items") != nil { line += "[sticker]" }
        let url = AutoSaver.folder(s, c).appendingPathComponent("Chat log.txt")
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = Data((line + "\n").utf8)
            if let h = try? FileHandle(forWritingTo: url) {
                h.seekToEndOfFile(); h.write(data); try? h.close()
            } else {
                try data.write(to: url)
            }
            index[c.id] = id
            last = index
            try index.map { "\($0.key) \($0.value)" }.joined(separator: "\n").write(to: indexUrl, atomically: true, encoding: .utf8)
        } catch {
            Log.write("archive: \(error.localizedDescription)")
        }
    }
}
