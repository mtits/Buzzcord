import Foundation

// Turns Discord messages into HTML for the chat history view. All message text is escaped
// first; only http(s) URLs ever reach an href or src.
@MainActor
enum MessageHtml {
    static let page = """
    <!DOCTYPE html>
    <html><head><meta charset="utf-8">
    <style>
    :root{--bg:#fff;--text:#000;--sub:#999;--link:#2a3ec0;--hover:#f6f3fc;--accent:#5b2c8f;--accentl:#c9bfe2;--panel:#f7f6fa;--code:#f0eef4;--border:#ddd;--dim:#444;--sel:#e6defa;--hl:#fff6cc;--me:#1f4fa0;--them:#b0141c;}
    @media (prefers-color-scheme: dark){:root{--bg:#1e1b26;--text:#e8e4f0;--sub:#8f88a3;--link:#9db4ff;--hover:#29243a;--accent:#c4a8f0;--accentl:#4d3f6b;--panel:#252030;--code:#2c2738;--border:#3a3348;--dim:#b4adc6;--sel:#3b2f57;--hl:#3a3320;--me:#7fa7ff;--them:#ff7b84;}}
    html,body{margin:0;padding:0;background:var(--bg);}
    body{font:13px -apple-system,"Helvetica Neue",Arial,sans-serif;color:var(--text);padding:4px 6px;word-wrap:break-word;-webkit-user-select:text;}
    a{color:var(--link);}
    .m{padding:2px 0 3px 0;position:relative;}
    .m:hover{background:var(--hover);}
    .t{color:var(--sub);font-size:11px;}
    .n{font-weight:bold;text-decoration:none;}
    a.n:hover{text-decoration:underline;}
    .me{color:var(--me);}
    .them{color:var(--them);}
    .sys{color:var(--sub);font-style:italic;}
    .rp{color:var(--sub);font-size:11px;margin-left:14px;}
    .ed{color:var(--sub);font-size:10px;}
    .act{display:none;position:absolute;right:2px;top:1px;background:var(--bg);border:1px solid var(--accentl);font-size:11px;padding:0 3px;}
    .m:hover .act{display:inline;}
    .act a{color:var(--accent);text-decoration:none;margin:0 2px;}
    .media{margin:3px 0 2px 14px;}
    .media img{border:1px solid var(--border);max-width:100%;height:auto;}
    video{max-width:100%;height:auto;}
    .file{display:inline-block;margin:3px 0 2px 14px;padding:4px 8px;border:1px solid var(--accentl);background:var(--hover);}
    .emb{margin:3px 0 2px 14px;padding:4px 8px;border-left:4px solid #9d7bc9;background:var(--panel);max-width:440px;}
    .emb .et{font-weight:bold;}
    .emb .ed2{font-size:12px;color:var(--dim);}
    .emb img{max-width:100%;height:auto;}
    .e{width:22px;height:22px;vertical-align:middle;}
    .sp{background:#333;color:#333;border-radius:2px;}
    .sp:hover{color:#fff;}
    code{font:12px Menlo,monospace;background:var(--code);padding:0 2px;}
    pre{font:12px Menlo,monospace;background:var(--code);padding:4px;margin:2px 0 2px 14px;white-space:pre-wrap;}
    blockquote{margin:0 0 0 14px;padding-left:6px;border-left:3px solid var(--border);color:var(--dim);}
    .mention{background:var(--sel);color:var(--accent);text-decoration:none;}
    .hl{background:var(--hl);}
    .re{display:inline-block;margin:2px 2px 0 0;padding:0 4px;border:1px solid var(--accentl);font-size:11px;background:var(--hover);color:var(--text);cursor:pointer;text-decoration:none;}
    .re.mine{border-color:#9d7bc9;background:var(--sel);}
    #older{text-align:center;font-size:11px;margin:4px;}
    .day{text-align:center;color:var(--sub);font-size:11px;margin:8px 0 4px;border-bottom:1px solid var(--border);}
    </style>
    <script>
    function root(){return document.scrollingElement||document.documentElement;}
    function atBottom(){var r=root();return r.scrollTop+window.innerHeight>=r.scrollHeight-60;}
    function bottom(){var r=root();r.scrollTop=r.scrollHeight;}
    var stick=true;
    window.addEventListener('scroll',function(){stick=atBottom();});
    window.addEventListener('resize',function(){if(stick)bottom();});
    function fixMedia(el){var imgs=el.querySelectorAll('img,video');for(var i=0;i<imgs.length;i++){imgs[i].addEventListener('load',function(){if(stick)bottom();});imgs[i].addEventListener('loadedmetadata',function(){if(stick)bottom();});}}
    function append(html){var s=stick||atBottom();var d=document.createElement('div');d.innerHTML=html;var log=document.getElementById('log');while(d.firstChild){var c=d.firstChild;log.appendChild(c);if(c.nodeType==1)fixMedia(c);}if(s){bottom();stick=true;}}
    function prepend(html){var r=root();var before=r.scrollHeight;var d=document.createElement('div');d.innerHTML=html;var log=document.getElementById('log');var first=log.firstChild;while(d.lastChild){var c=d.lastChild;log.insertBefore(c,first);if(c.nodeType==1)fixMedia(c);first=c;}r.scrollTop+=r.scrollHeight-before;}
    function replace(id,html){var e=document.getElementById(id);if(!e)return;var d=document.createElement('div');d.innerHTML=html;var n=d.firstChild;e.parentNode.replaceChild(n,e);fixMedia(n);if(stick)bottom();}
    function removeMsg(id){var e=document.getElementById(id);if(e)e.parentNode.removeChild(e);}
    function clearLog(){document.getElementById('log').innerHTML='';}
    function setOlder(html){document.getElementById('older').innerHTML=html;}
    window.addEventListener('scroll',function(){if(root().scrollTop<40){var a=document.querySelector('#older a');if(a&&!window.loadingOlder){window.loadingOlder=true;a.click();setTimeout(function(){window.loadingOlder=false;},1500);}}});
    </script></head>
    <body><div id="older"></div><div id="log"></div></body></html>
    """

    static func e(_ s: String?) -> String {
        guard let s else { return "" }
        var out = ""
        out.reserveCapacity(s.count)
        for ch in s {
            switch ch {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            case "'": out += "&#39;"
            default: out.append(ch)
            }
        }
        return out
    }

    static func safeUrl(_ url: String?) -> String? {
        guard let url else { return nil }
        let l = url.lowercased()
        guard l.hasPrefix("https://") || l.hasPrefix("http://") else { return nil }
        return e(url)
    }

    static func appLink(_ action: String, _ arg: String?) -> String {
        "app://" + action + "/" + (arg ?? "").addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
    }

    static let dayFormat: DateFormatter = { let f = DateFormatter(); f.dateFormat = "EEEE, MMMM d, yyyy"; return f }()
    static let timeFormat: DateFormatter = { let f = DateFormatter(); f.dateFormat = "h:mm a"; return f }()

    static func dayDivider(_ t: Date) -> String { "<div class=\"day\">" + e(dayFormat.string(from: t)) + "</div>" }

    static func render(_ s: Session, _ m: JObj) -> String {
        var sb = ""
        let id = m.str("id") ?? ""
        let author = m.obj("author")
        let authorId = author?.str("id")
        let mine = s.me != nil && authorId == s.me?.id
        let mention = !mine && s.mentionsMe(m)
        let type = Int(m.long("type"))
        let time = Snowflake.time(id)

        sb += "<div class=\"m" + (mention ? " hl" : "") + "\" id=\"m" + e(id) + "\">"

        if let reference = m.obj("referenced_message") {
            var snippet = reference.str("content") ?? ""
            if snippet.count > 80 { snippet = String(snippet.prefix(80)) + "..." }
            if snippet.isEmpty { snippet = "(attachment)" }
            sb += "<div class=\"rp\">&#8618; <b>" + e(s.name(reference.obj("author")?.str("id"))) + "</b> " + e(snippet) + "</div>"
        }

        sb += "<span class=\"t\">(" + e(timeFormat.string(from: time)) + ")</span> "

        if let system = systemText(s, m, type) {
            sb += "<span class=\"sys\">" + e(system) + "</span></div>"
            return sb
        }

        var name = s.name(authorId)
        if let author, let aid = authorId, s.users[aid] == nil { name = author.str("global_name") ?? author.str("username") ?? name }
        let cls = mine ? "me" : "them"
        sb += "<a class=\"n \(cls)\" href=\"" + appLink("profile", authorId) + "\" title=\"View profile\">" + e(name) + "</a><span class=\"n \(cls)\">:</span> "
        sb += content(s, m.str("content") ?? "")
        if m.str("edited_timestamp") != nil { sb += " <span class=\"ed\">(edited)</span>" }

        sb += "<span class=\"act\"><a href=\"" + appLink("reply", id) + "\">reply</a>"
        sb += "<a href=\"" + appLink("reactpick", id) + "\">react</a>"
        if mine { sb += "<a href=\"" + appLink("edit", id) + "\">edit</a>" }
        if mine || canManage(s, m) { sb += "<a href=\"" + appLink("delete", id) + "\">delete</a>" }
        sb += "</span>"

        for a in m.objs("attachments") { sb += attachment(a) }
        for st in m.objs("sticker_items") { sb += sticker(st) }
        for em in m.objs("embeds") { sb += embed(s, em) }

        let reactions = m.objs("reactions")
        if !reactions.isEmpty {
            sb += "<div style=\"margin-left:14px\">"
            for r in reactions {
                let emoji = r.obj("emoji")
                // Click to add or remove your own reaction.
                sb += "<a class=\"re" + (r.bool("me") ? " mine" : "") + "\" title=\"" + (r.bool("me") ? "Remove your reaction" : "React with this")
                    + "\" href=\"" + appLink("react", id + "/" + emojiKey(emoji)) + "\">"
                if let emoji, let eid = emoji.str("id") {
                    sb += "<img class=\"e\" style=\"width:16px;height:16px\" src=\"https://cdn.discordapp.com/emojis/" + e(eid)
                        + (emoji.bool("animated") ? ".gif" : ".png") + "?size=32\" alt=\"" + e(emoji.str("name")) + "\">"
                } else if let emoji {
                    sb += e(emoji.str("name"))
                }
                sb += " \(r.long("count"))</a>"
            }
            sb += "</div>"
        }
        sb += "</div>"
        return sb
    }

    // How Discord names an emoji in reaction URLs: "name:id" for custom ones, the character itself otherwise.
    nonisolated static func emojiKey(_ emoji: JObj?) -> String {
        guard let emoji else { return "" }
        if let id = emoji.str("id") { return (emoji.str("name") ?? "") + ":" + id }
        return emoji.str("name") ?? ""
    }

    static func canManage(_ s: Session, _ m: JObj) -> Bool {
        let c = s.findChannel(m.str("channel_id"))
        guard let g = s.guild(of: c), let me = s.me else { return false }
        return g.permissions(in: c, me: me.id) & Guild.manageMessages != 0
    }

    static func systemText(_ s: Session, _ m: JObj, _ type: Int) -> String? {
        let who = m.obj("author").map { s.name($0.str("id")) } ?? "Someone"
        let target = m.objs("mentions").first.map { $0.str("global_name") ?? $0.str("username") ?? "someone" }
        switch type {
        case 0, 19, 20, 23: return nil
        case 1: return who + " added " + (target ?? "someone") + " to the group."
        case 2: return who + " removed " + (target ?? "someone") + " from the group."
        case 3: return who + " started a call."
        case 4: return who + " changed the group name to " + (m.str("content") ?? "") + "."
        case 5: return who + " changed the group icon."
        case 6: return who + " pinned a message."
        case 7: return who + " joined the server."
        case 8: return who + " boosted the server!"
        default: return (m.str("content") ?? "").isEmpty ? who + " did something Buzzcord can't show." : nil
        }
    }

    static func size(_ bytes: Int64) -> String {
        if bytes >= 1024 * 1024 { return String(format: "%.1f MB", Double(bytes) / 1_048_576) }
        if bytes >= 1024 { return "\(bytes / 1024) KB" }
        return "\(bytes) B"
    }

    static func fit(_ w: Int64, _ h: Int64, _ maxW: Int, _ maxH: Int) -> (Int, Int) {
        if w <= 0 || h <= 0 { return (maxW, maxH) }
        let k = min(1.0, min(Double(maxW) / Double(w), Double(maxH) / Double(h)))
        return (max(1, Int(Double(w) * k)), max(1, Int(Double(h) * k)))
    }

    static func withQuery(_ url: String, _ q: String) -> String { url + (url.contains("?") ? "&" : "?") + q }

    static func attachment(_ a: JObj) -> String {
        let url = a.str("url") ?? ""
        let proxy = a.str("proxy_url") ?? url
        let file = a.str("filename") ?? "file"
        let ct = (a.str("content_type") ?? "").lowercased()
        let ext = file.contains(".") ? "." + (file.split(separator: ".").last.map(String.init)?.lowercased() ?? "") : ""
        let (ow, oh) = fit(a.long("width"), a.long("height"), 360, 280)
        let open = appLink("open", url)
        let spoiler = file.hasPrefix("SPOILER_")

        if !spoiler && (ct.hasPrefix("image/") || [".png", ".jpg", ".jpeg", ".gif", ".webp"].contains(ext)), safeUrl(url) != nil {
            let src = ext == ".gif" || ct == "image/gif" ? url : withQuery(proxy, "format=png&width=\(ow)&height=\(oh)")
            return "<div class=\"media\"><a href=\"" + e(open) + "\" title=\"" + e(file) + "\"><img src=\"" + (safeUrl(src) ?? "") + "\" width=\"\(ow)\" height=\"\(oh)\"></a></div>"
        }
        if !spoiler && ([".mp4", ".m4v", ".mov", ".webm"].contains(ext) || ct.hasPrefix("video/")), safeUrl(url) != nil {
            let poster = withQuery(proxy, "format=jpeg&width=\(ow)&height=\(oh)")
            return "<div class=\"media\"><video controls preload=\"none\" width=\"\(ow)\" height=\"\(oh)\" poster=\"" + (safeUrl(poster) ?? "") + "\" src=\"" + (safeUrl(url) ?? "") + "\"></video><br>"
                + "<a href=\"" + e(open) + "\">" + e(file) + "</a> <span class=\"t\">" + size(a.long("size")) + "</span></div>"
        }
        return "<div class=\"file\">&#128206; <a href=\"" + e(open) + "\">" + e(file) + "</a> <span class=\"t\">" + size(a.long("size")) + (spoiler ? " (spoiler)" : "") + "</span></div>"
    }

    static func sticker(_ st: JObj) -> String {
        let fmt = st.long("format_type")
        let id = st.str("id") ?? ""
        if fmt == 1 || fmt == 2 || fmt == 4 {
            return "<div class=\"media\"><img src=\"https://media.discordapp.net/stickers/" + e(id) + (fmt == 4 ? ".gif" : ".png") + "?size=160\" width=\"120\" height=\"120\" alt=\"" + e(st.str("name")) + "\"></div>"
        }
        return "<div class=\"media t\">[sticker: " + e(st.str("name")) + "]</div>"
    }

    static func embed(_ s: Session, _ em: JObj) -> String {
        let type = em.str("type")
        let thumb = em.obj("thumbnail"), video = em.obj("video"), image = em.obj("image")
        if type == "gifv", let video, let vurl = safeUrl(video.str("url")) {
            let (ow, oh) = fit(video.long("width"), video.long("height"), 300, 240)
            return "<div class=\"media\"><video autoplay loop muted playsinline width=\"\(ow)\" height=\"\(oh)\" src=\"" + vurl + "\"></video></div>"
        }
        if type == "image", let thumb {
            guard var src = thumb.str("proxy_url") ?? thumb.str("url"), safeUrl(src) != nil else { return "" }
            let (ow, oh) = fit(thumb.long("width"), thumb.long("height"), 360, 280)
            let gif = (thumb.str("url") ?? "").lowercased().contains(".gif")
            if !gif { src = withQuery(src, "format=png&width=\(ow)&height=\(oh)") }
            return "<div class=\"media\"><a href=\"" + e(appLink("open", em.str("url") ?? thumb.str("url"))) + "\"><img src=\"" + (safeUrl(src) ?? "") + "\" width=\"\(ow)\" height=\"\(oh)\"></a></div>"
        }
        var sb = "<div class=\"emb\">"
        if let p = em.obj("provider")?.str("name") { sb += "<div class=\"t\">" + e(p) + "</div>" }
        let authorName = em.obj("author")?.str("name")
        if let authorName { sb += "<div class=\"ed2\"><b>" + e(authorName) + "</b></div>" }
        if let title = em.str("title") {
            sb += "<div class=\"et\">"
            if let link = safeUrl(em.str("url")) { sb += "<a href=\"" + link + "\">" + e(title) + "</a>" } else { sb += e(title) }
            sb += "</div>"
        }
        if var d = em.str("description") {
            if d.count > 400 { d = String(d.prefix(400)) + "..." }
            sb += "<div class=\"ed2\">" + content(s, d) + "</div>"
        }
        for f in em.objs("fields") {
            sb += "<div class=\"ed2\"><b>" + e(f.str("name")) + "</b><br>" + content(s, f.str("value") ?? "") + "</div>"
        }
        let pic = image ?? (["video", "article", "link", "rich"].contains(type ?? "") ? thumb : nil)
        if let pic, let src = pic.str("proxy_url") ?? pic.str("url"), safeUrl(src) != nil {
            let (ow, oh) = fit(pic.long("width"), pic.long("height"), image != nil ? 400 : 160, image != nil ? 300 : 120)
            let target = safeUrl(em.str("url")) != nil ? em.str("url") : pic.str("url")
            sb += "<div><a href=\"" + e(appLink("open", target)) + "\"><img src=\"" + (safeUrl(withQuery(src, "format=png&width=\(ow)&height=\(oh)")) ?? "") + "\" width=\"\(ow)\" height=\"\(oh)\"></a></div>"
        }
        if let footer = em.obj("footer")?.str("text") { sb += "<div class=\"t\">" + e(footer) + "</div>" }
        sb += "</div>"
        // Bare link previews with nothing to show are noise.
        if em.str("title") == nil && em.str("description") == nil && pic == nil && authorName == nil { return "" }
        return sb
    }

    // MARK: - message text (Discord markdown subset)

    private static func re(_ p: String, _ o: NSRegularExpression.Options = []) -> NSRegularExpression {
        try! NSRegularExpression(pattern: p, options: o)
    }

    static let codeBlock = re("```(?:[a-zA-Z0-9_+-]*\\n)?([\\s\\S]*?)```")
    static let inlineCode = re("`([^`\\n]+)`")
    static let userMention = re("&lt;@!?(\\d+)&gt;")
    static let roleMention = re("&lt;@&amp;(\\d+)&gt;")
    static let channelMention = re("&lt;#(\\d+)&gt;")
    static let customEmoji = re("&lt;(a?):(\\w+):(\\d+)&gt;")
    static let timestamp = re("&lt;t:(-?\\d+)(?::([tTdDfFR]))?&gt;")
    static let maskedLink = re("\\[([^\\]\\n]+)\\]\\((https?://[^\\s)]+)\\)")
    static let bareLink = re("(?<![\"=])(https?://[^\\s<]+[^\\s<.,:;\"')\\]!?])")
    static let bold = re("\\*\\*(.+?)\\*\\*", .dotMatchesLineSeparators)
    static let underline = re("__(.+?)__", .dotMatchesLineSeparators)
    static let italic = re("(?<![\\w*])\\*(?!\\s)(.+?)(?<!\\s)\\*(?![\\w*])|(?<![\\w_])_(?!\\s)(.+?)(?<!\\s)_(?![\\w_])")
    static let strike = re("~~(.+?)~~", .dotMatchesLineSeparators)
    static let spoiler = re("\\|\\|(.+?)\\|\\|", .dotMatchesLineSeparators)
    static let slot = re("\u{1}(\\d+)\u{2}")

    // Replaces every match with what f returns for its groups (nil for a group that didn't take part).
    static func sub(_ r: NSRegularExpression, _ text: String, _ f: ([String?]) -> String) -> String {
        let ns = text as NSString
        var out = ""
        var last = 0
        for m in r.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            out += ns.substring(with: NSRange(location: last, length: m.range.location - last))
            var groups: [String?] = []
            for i in 0..<m.numberOfRanges {
                let rg = m.range(at: i)
                groups.append(rg.location == NSNotFound ? nil : ns.substring(with: rg))
            }
            out += f(groups)
            last = m.range.location + m.range.length
        }
        out += ns.substring(from: last)
        return out
    }

    static func content(_ s: Session, _ input: String) -> String {
        var slots: [String] = []
        func hold(_ html: String) -> String { slots.append(html); return "\u{1}\(slots.count - 1)\u{2}" }

        var text = sub(codeBlock, input) { g in
            var v = g[1] ?? ""
            while v.hasSuffix("\n") { v.removeLast() }
            return hold("<pre>" + e(v) + "</pre>")
        }
        text = sub(inlineCode, text) { g in hold("<code>" + e(g[1]) + "</code>") }
        text = e(text)

        text = sub(userMention, text) { g in hold("<a class=\"mention\" href=\"" + appLink("profile", g[1]) + "\">@" + e(s.name(g[1])) + "</a>") }
        text = sub(roleMention, text) { g in
            let role = s.guilds.values.lazy.compactMap { $0.roles[g[1] ?? ""] }.first
            return hold("<span class=\"mention\">@" + e(role?.name ?? "role") + "</span>")
        }
        text = sub(channelMention, text) { g in
            let c = s.findChannel(g[1])
            return hold("<span class=\"mention\">#" + e(c.map { $0.name ?? s.title($0) } ?? "channel") + "</span>")
        }
        text = sub(customEmoji, text) { g in
            hold("<img class=\"e\" src=\"https://cdn.discordapp.com/emojis/" + (g[3] ?? "") + (g[1] == "a" ? ".gif" : ".png")
                + "?size=48\" alt=\":" + (g[2] ?? "") + ":\" title=\":" + (g[2] ?? "") + ":\">")
        }
        text = sub(timestamp, text) { g in
            guard let v = Double(g[1] ?? "") else { return g[0] ?? "" }
            let t = Date(timeIntervalSince1970: v)
            let f = DateFormatter()
            let str: String
            switch g[2] ?? "f" {
            case "t": f.dateFormat = "h:mm a"; str = f.string(from: t)
            case "T": f.dateFormat = "h:mm:ss a"; str = f.string(from: t)
            case "d": f.dateFormat = "MM/dd/yyyy"; str = f.string(from: t)
            case "D": f.dateFormat = "MMMM d, yyyy"; str = f.string(from: t)
            case "F": f.dateFormat = "EEEE, MMMM d, yyyy h:mm a"; str = f.string(from: t)
            case "R": str = RelativeDateTimeFormatter().localizedString(for: t, relativeTo: Date())
            default: f.dateFormat = "MMMM d, yyyy h:mm a"; str = f.string(from: t)
            }
            return hold("<code>" + e(str) + "</code>")
        }
        text = sub(maskedLink, text) { g in hold("<a href=\"" + (g[2] ?? "") + "\">" + (g[1] ?? "") + "</a>") }
        text = sub(bareLink, text) { g in hold("<a href=\"" + (g[1] ?? "") + "\">" + (g[1] ?? "") + "</a>") }

        text = sub(bold, text) { g in "<b>" + (g[1] ?? "") + "</b>" }
        text = sub(underline, text) { g in "<u>" + (g[1] ?? "") + "</u>" }
        text = sub(italic, text) { g in "<i>" + (g[1] ?? g[2] ?? "") + "</i>" }
        text = sub(strike, text) { g in "<s>" + (g[1] ?? "") + "</s>" }
        text = sub(spoiler, text) { g in "<span class=\"sp\">" + (g[1] ?? "") + "</span>" }

        // Line-level markup: quotes and headings.
        let lines = text.components(separatedBy: "\n")
        var sb = ""
        for (i, raw) in lines.enumerated() {
            var line = raw
            if line.hasPrefix("&gt; ") { line = "<blockquote>" + line.dropFirst(5) + "</blockquote>" }
            else if line.hasPrefix("### ") { line = "<b>" + line.dropFirst(4) + "</b>" }
            else if line.hasPrefix("## ") { line = "<b style=\"font-size:15px\">" + line.dropFirst(3) + "</b>" }
            else if line.hasPrefix("# ") { line = "<b style=\"font-size:17px\">" + line.dropFirst(2) + "</b>" }
            else if line.hasPrefix("-# ") { line = "<span class=\"t\">" + line.dropFirst(3) + "</span>" }
            sb += line
            if i < lines.count - 1 && !line.hasSuffix("</blockquote>") { sb += "<br>" }
        }
        text = sb

        var pass = 0
        while pass < 3 && text.contains("\u{1}") {
            text = sub(slot, text) { g in slots[Int(g[1] ?? "0") ?? 0] }
            pass += 1
        }
        return text
    }
}
