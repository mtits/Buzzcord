using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Buzzcord
{
    // Turns Discord messages into HTML for the chat history view. All message text is escaped
    // first; only http(s) URLs ever reach an href or src.
    public static class MessageHtml
    {
        // The chat page with the current theme's colours; setTheme() swaps them live.
        public static string Page { get { return PageTemplate.Replace("/*THEME*/", Theme.ChatCss()); } }

        const string PageTemplate = @"<!DOCTYPE html>
<html><head><meta http-equiv=""X-UA-Compatible"" content=""IE=edge""><meta charset=""utf-8"">
<style>
html,body{margin:0;padding:0;background:#fff;}
body{font:13px Arial,Helvetica,sans-serif;color:#000;padding:4px 6px;word-wrap:break-word;}
a{color:#2a3ec0;}
.m{padding:2px 0 3px 0;position:relative;}
.m:hover{background:#f6f3fc;}
.t{color:#999;font-size:11px;}
.n{font-weight:bold;text-decoration:none;}
a.n:hover{text-decoration:underline;}
.me{color:#1f4fa0;}
.them{color:#b0141c;}
.sys{color:#777;font-style:italic;}
a.bs{text-decoration:none;cursor:pointer;}
a.bs:hover{text-decoration:underline;}
.blk .bb{display:none;}
.blk.open .bb{display:block;margin-top:2px;}
.rp{color:#777;font-size:11px;margin-left:14px;}
.ed{color:#999;font-size:10px;}
.act{display:none;position:absolute;right:2px;top:1px;background:#fff;border:1px solid #c9bfe2;font-size:11px;padding:0 3px;}
.m:hover .act{display:inline;}
.act a{color:#5b2c8f;text-decoration:none;margin:0 2px;}
.media{margin:3px 0 2px 14px;}
.media img{border:1px solid #ddd;max-width:100%;}
.file{display:inline-block;margin:3px 0 2px 14px;padding:4px 8px;border:1px solid #d8d0ea;background:#f6f3fc;}
.emb{margin:3px 0 2px 14px;padding:4px 8px;border-left:4px solid #9d7bc9;background:#f7f6fa;max-width:440px;}
.emb .et{font-weight:bold;}
.emb .ed2{font-size:12px;color:#333;}
.e{width:22px;height:22px;vertical-align:middle;}
.sp{background:#333;color:#333;}
.sp:hover{color:#fff;}
code{font:12px Consolas,monospace;background:#f0eef4;padding:0 2px;}
pre{font:12px Consolas,monospace;background:#f0eef4;padding:4px;margin:2px 0 2px 14px;white-space:pre-wrap;}
blockquote{margin:0 0 0 14px;padding-left:6px;border-left:3px solid #ccc;color:#444;}
.mention{background:#e6defa;color:#5b2c8f;text-decoration:none;}
.hl{background:#fff6cc;}
.re{display:inline-block;margin:2px 2px 0 0;padding:0 4px;border:1px solid #d8d0ea;font-size:11px;background:#faf9fd;color:#000;cursor:pointer;}
.re.mine{border-color:#9d7bc9;background:#ede6fa;}
#older{text-align:center;font-size:11px;margin:4px;}
.pill{display:inline-block;padding:0 5px;margin-right:3px;border:1px solid #ddd5ee;background:#f3f0fa;font-size:10px;}
.day{text-align:center;color:#888;font-size:11px;margin:8px 0 4px;border-bottom:1px solid #eee;}
</style>
<style id=""theme"">/*THEME*/</style>
<script>
function root(){return document.documentElement;}
function atBottom(){var r=root();return r.scrollTop+r.clientHeight>=r.scrollHeight-60;}
function bottom(){var r=root();r.scrollTop=r.scrollHeight;}
var stick=true;
window.onscroll=function(){stick=atBottom();};
document.onmousewheel=function(){var e=window.event;root().scrollTop-=e.wheelDelta;stick=atBottom();return false;};
function fixMedia(el){var imgs=el.getElementsByTagName('img');for(var i=0;i<imgs.length;i++){imgs[i].onload=function(){if(stick)bottom();};}}
function append(html){var s=stick||atBottom();var d=document.createElement('div');d.innerHTML=html;var log=document.getElementById('log');while(d.firstChild){var c=d.firstChild;log.appendChild(c);if(c.nodeType==1)fixMedia(c);}if(s){bottom();stick=true;}}
function prepend(html){var r=root();var before=r.scrollHeight;var d=document.createElement('div');d.innerHTML=html;var log=document.getElementById('log');var first=log.firstChild;while(d.lastChild){var c=d.lastChild;log.insertBefore(c,first);first=c;}r.scrollTop+=r.scrollHeight-before;}
function replace(id,html){var e=document.getElementById(id);if(!e)return;var d=document.createElement('div');d.innerHTML=html;var n=d.firstChild;e.parentNode.replaceChild(n,e);if(stick)bottom();}
function removeMsg(id){var e=document.getElementById(id);if(e)e.parentNode.removeChild(e);}
function setOlder(html){document.getElementById('older').innerHTML=html;}
function clearLog(){document.getElementById('log').innerHTML='';}
function blk(a){var p=a.parentNode;p.className=p.className=='blk'?'blk open':'blk';return false;}
function setTheme(css){document.getElementById('theme').textContent=css;}
</script></head>
<body><div id=""older""></div><div id=""log""></div></body></html>";

        public static string E(string s)
        {
            return s == null ? "" : WebUtility.HtmlEncode(s);
        }

        static string SafeUrl(string url)
        {
            if (url == null) return null;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
            return E(url);
        }

        static string AppLink(string action, string arg)
        {
            return "app://" + action + "/" + Uri.EscapeDataString(arg ?? "");
        }

        public static string DayDivider(DateTime t)
        {
            return "<div class=\"day\">" + E(t.ToString("dddd, MMMM d, yyyy")) + "</div>";
        }

        public static string Render(Session s, JObj m)
        {
            string id = m.Str("id");
            var author = m.Obj("author");
            // Like Discord: a blocked user's message is folded away, one click shows it. The outer
            // div keeps the message's id, so edits and deletes still find it.
            if (author == null || !s.IsBlocked(author.Str("id"))) return Message(s, m, true);
            return "<div class=\"m\" id=\"m" + E(id) + "\"><div class=\"blk\"><a class=\"sys bs\" href=\"#\" onclick=\"return blk(this)\" title=\"Show or hide\">("
                + E(Snowflake.Time(id).ToString("h:mm tt", CultureInfo.InvariantCulture)) + ") Blocked message</a><div class=\"bb\">"
                + Message(s, m, false) + "</div></div></div>";
        }

        static string Message(Session s, JObj m, bool anchor)
        {
            var sb = new StringBuilder();
            string id = m.Str("id");
            var author = m.Obj("author");
            string authorId = author != null ? author.Str("id") : null;
            bool mine = s.Me != null && authorId == s.Me.Id;
            bool mention = !mine && s.MentionsMe(m);
            int type = (int)m.Long("type");
            DateTime time = Snowflake.Time(id);

            sb.Append("<div class=\"m").Append(mention ? " hl" : "").Append("\"");
            if (anchor) sb.Append(" id=\"m").Append(E(id)).Append("\"");
            sb.Append(">");

            var reference = m.Obj("referenced_message");
            if (reference != null)
            {
                var ra = reference.Obj("author");
                string snippet = reference.Str("content") ?? "";
                if (snippet.Length > 80) snippet = snippet.Substring(0, 80) + "...";
                if (snippet.Length == 0) snippet = "(attachment)";
                if (ra != null && s.IsBlocked(ra.Str("id"))) snippet = "(blocked message)";
                sb.Append("<div class=\"rp\">&#8618; <b>").Append(E(ra != null ? s.NameOf(ra.Str("id")) : "?")).Append("</b> ").Append(E(snippet)).Append("</div>");
            }

            sb.Append("<span class=\"t\">(").Append(E(time.ToString("h:mm tt", CultureInfo.InvariantCulture))).Append(")</span> ");

            string system = SystemText(s, m, type);
            if (system != null)
            {
                sb.Append("<span class=\"sys\">").Append(E(system)).Append("</span></div>");
                return sb.ToString();
            }

            string name = authorId != null ? s.NameOf(authorId) : "?";
            if (author != null && !s.Users.ContainsKey(authorId)) name = author.Str("global_name") ?? author.Str("username") ?? name;
            sb.Append("<a class=\"n ").Append(mine ? "me" : "them").Append("\" href=\"").Append(AppLink("profile", authorId)).Append("\" title=\"View profile\">")
                .Append(E(name)).Append("</a><span class=\"n ").Append(mine ? "me" : "them").Append("\">:</span> ");
            sb.Append(Content(s, m.Str("content") ?? ""));
            if (m.Str("edited_timestamp") != null) sb.Append(" <span class=\"ed\">(edited)</span>");

            sb.Append("<span class=\"act\"><a href=\"").Append(AppLink("reply", id)).Append("\">reply</a>");
            sb.Append("<a href=\"").Append(AppLink("reactpick", id)).Append("\">react</a>");
            if (mine) sb.Append("<a href=\"").Append(AppLink("edit", id)).Append("\">edit</a>");
            if (mine || CanManage(s, m)) sb.Append("<a href=\"").Append(AppLink("delete", id)).Append("\">delete</a>");
            sb.Append("</span>");

            foreach (var a in m.Objs("attachments")) sb.Append(Attachment(a));
            foreach (var st in m.Objs("sticker_items")) sb.Append(Sticker(st));
            foreach (var e in m.Objs("embeds")) sb.Append(Embed(s, e, m.Str("content") ?? ""));

            var reactions = m.Arr("reactions");
            if (reactions != null && reactions.Count > 0)
            {
                sb.Append("<div style=\"margin-left:14px\">");
                foreach (var ro in reactions)
                {
                    var r = ro as JObj;
                    if (r == null) continue;
                    var emoji = r.Obj("emoji");
                    // Click to add or remove your own reaction.
                    sb.Append("<a class=\"re").Append(r.Bool("me") ? " mine" : "").Append("\" style=\"text-decoration:none\" title=\"")
                      .Append(r.Bool("me") ? "Remove your reaction" : "React with this").Append("\" href=\"")
                      .Append(AppLink("react", id + "/" + EmojiKey(emoji))).Append("\">");
                    if (emoji != null && emoji.Str("id") != null)
                        sb.Append("<img class=\"e\" style=\"width:16px;height:16px\" src=\"https://cdn.discordapp.com/emojis/").Append(E(emoji.Str("id"))).Append(emoji.Bool("animated") ? ".gif" : ".png").Append("?size=32\" alt=\"").Append(E(emoji.Str("name"))).Append("\">");
                    else if (emoji != null) sb.Append(EmojiImg(emoji.Str("name"), 16) ?? E(emoji.Str("name")));
                    sb.Append(" ").Append(r.Long("count")).Append("</a>");
                }
                sb.Append("</div>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        // An <img> of a unicode emoji in color, or null when color emoji can't be drawn.
        public static string EmojiImg(string emoji, int px)
        {
            string uri = string.IsNullOrEmpty(emoji) ? null : Emoji.DataUri(emoji, px);
            if (uri == null) return null;
            return "<img class=\"emo\" src=\"" + uri + "\" alt=\"" + E(emoji) + "\" width=\"" + px + "\" height=\"" + px
                + "\" style=\"vertical-align:" + (px > 24 ? "middle" : "-4px") + ";margin:0 1px\">";
        }

        // How Discord names an emoji in reaction URLs: "name:id" for custom ones, the character itself otherwise.
        public static string EmojiKey(JObj emoji)
        {
            if (emoji == null) return "";
            return emoji.Str("id") != null ? emoji.Str("name") + ":" + emoji.Str("id") : emoji.Str("name") ?? "";
        }

        static bool CanManage(Session s, JObj m)
        {
            var c = s.FindChannel(m.Str("channel_id"));
            var g = s.GuildOf(c);
            return g != null && (g.PermissionsIn(c, s.Me.Id) & Guild.ManageMessages) != 0;
        }

        static string SystemText(Session s, JObj m, int type)
        {
            var author = m.Obj("author");
            string who = author != null ? s.NameOf(author.Str("id")) : "Someone";
            string target = null;
            foreach (var u in m.Objs("mentions")) { target = u.Str("global_name") ?? u.Str("username"); break; }
            switch (type)
            {
                case 0: case 19: case 20: case 23: return null;
                case 1: return who + " added " + (target ?? "someone") + " to the group.";
                case 2: return who + " removed " + (target ?? "someone") + " from the group.";
                case 3: return who + " started a call.";
                case 4: return who + " changed the group name to " + (m.Str("content") ?? "") + ".";
                case 5: return who + " changed the group icon.";
                case 6: return who + " pinned a message.";
                case 7: return who + " joined the server.";
                case 8: return who + " boosted the server!";
                default: return string.IsNullOrEmpty(m.Str("content")) ? who + " did something Buzzcord can't show." : null;
            }
        }

        static string Size(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024) + " KB";
            return bytes + " B";
        }

        static void Fit(long w, long h, int maxW, int maxH, out int ow, out int oh)
        {
            if (w <= 0 || h <= 0) { ow = maxW; oh = maxH; return; }
            double k = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
            ow = Math.Max(1, (int)(w * k));
            oh = Math.Max(1, (int)(h * k));
        }

        static string WithQuery(string url, string q)
        {
            return url + (url.Contains("?") ? "&" : "?") + q;
        }

        static string Attachment(JObj a)
        {
            string url = a.Str("url"), proxy = a.Str("proxy_url") ?? url, file = a.Str("filename") ?? "file";
            string ct = (a.Str("content_type") ?? "").ToLowerInvariant();
            int dot = file.LastIndexOf('.');
            string ext = dot >= 0 ? file.Substring(dot).ToLowerInvariant() : ""; // Path.GetExtension throws on odd names
            long w = a.Long("width"), h = a.Long("height");
            int ow, oh;
            Fit(w, h, 360, 280, out ow, out oh);
            string open = AppLink("open", url);
            bool spoiler = file.StartsWith("SPOILER_");

            if (!spoiler && (ct.StartsWith("image/") || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp") && SafeUrl(url) != null)
            {
                string src = ext == ".gif" || ct == "image/gif" ? url
                    : WithQuery(proxy, "format=png&width=" + ow + "&height=" + oh);
                return "<div class=\"media\"><a href=\"" + E(open) + "\" title=\"" + E(file) + "\"><img src=\"" + SafeUrl(src) + "\" width=\"" + ow + "\" height=\"" + oh + "\"></a></div>";
            }
            if (!spoiler && (ext == ".mp4" || ext == ".m4v" || ct == "video/mp4") && SafeUrl(url) != null)
            {
                string poster = WithQuery(proxy, "format=jpeg&width=" + ow + "&height=" + oh);
                return "<div class=\"media\"><video controls preload=\"none\" width=\"" + ow + "\" height=\"" + oh + "\" poster=\"" + SafeUrl(poster) + "\" src=\"" + SafeUrl(url) + "\"></video><br>"
                    + "<a href=\"" + E(open) + "\">" + E(file) + "</a> <span class=\"t\">" + Size(a.Long("size")) + "</span></div>";
            }
            return "<div class=\"file\">&#128206; <a href=\"" + E(open) + "\">" + E(file) + "</a> <span class=\"t\">" + Size(a.Long("size")) + (spoiler ? " (spoiler)" : "") + "</span></div>";
        }

        static string Sticker(JObj st)
        {
            long fmt = st.Long("format_type");
            string id = st.Str("id");
            if (fmt == 1 || fmt == 2 || fmt == 4)
                return "<div class=\"media\"><img src=\"https://media.discordapp.net/stickers/" + E(id) + (fmt == 4 ? ".gif" : ".png") + "?size=160\" width=\"120\" height=\"120\" alt=\"" + E(st.Str("name")) + "\"></div>";
            return "<div class=\"media t\">[sticker: " + E(st.Str("name")) + "]</div>";
        }

        static string Embed(Session s, JObj e, string content)
        {
            string type = e.Str("type");
            var thumb = e.Obj("thumbnail");
            var video = e.Obj("video");
            var image = e.Obj("image");
            int ow, oh;
            if (type == "gifv" && video != null && SafeUrl(video.Str("url")) != null)
            {
                Fit(video.Long("width"), video.Long("height"), 300, 240, out ow, out oh);
                return "<div class=\"media\"><video autoplay loop muted width=\"" + ow + "\" height=\"" + oh + "\" src=\"" + SafeUrl(video.Str("url")) + "\"></video></div>";
            }
            if (type == "image" && thumb != null)
            {
                string src = thumb.Str("proxy_url") ?? thumb.Str("url");
                if (SafeUrl(src) == null) return "";
                Fit(thumb.Long("width"), thumb.Long("height"), 360, 280, out ow, out oh);
                bool gif = (thumb.Str("url") ?? "").ToLowerInvariant().Contains(".gif");
                if (!gif) src = WithQuery(src, "format=png&width=" + ow + "&height=" + oh);
                return "<div class=\"media\"><a href=\"" + E(AppLink("open", e.Str("url") ?? thumb.Str("url"))) + "\"><img src=\"" + SafeUrl(src) + "\" width=\"" + ow + "\" height=\"" + oh + "\"></a></div>";
            }
            var sb = new StringBuilder("<div class=\"emb\">");
            var provider = e.Obj("provider");
            if (provider != null && provider.Str("name") != null) sb.Append("<div class=\"t\">").Append(E(provider.Str("name"))).Append("</div>");
            var author = e.Obj("author");
            if (author != null && author.Str("name") != null) sb.Append("<div class=\"ed2\"><b>").Append(E(author.Str("name"))).Append("</b></div>");
            if (e.Str("title") != null)
            {
                string link = SafeUrl(e.Str("url"));
                sb.Append("<div class=\"et\">");
                if (link != null) sb.Append("<a href=\"").Append(link).Append("\">").Append(E(e.Str("title"))).Append("</a>");
                else sb.Append(E(e.Str("title")));
                sb.Append("</div>");
            }
            if (e.Str("description") != null)
            {
                string d = e.Str("description");
                if (d.Length > 400) d = d.Substring(0, 400) + "...";
                sb.Append("<div class=\"ed2\">").Append(Content(s, d)).Append("</div>");
            }
            foreach (var f in e.Objs("fields"))
                sb.Append("<div class=\"ed2\"><b>").Append(E(f.Str("name"))).Append("</b><br>").Append(Content(s, f.Str("value") ?? "")).Append("</div>");
            var pic = image ?? (type == "video" || type == "article" || type == "link" || type == "rich" ? thumb : null);
            if (pic != null)
            {
                string src = pic.Str("proxy_url") ?? pic.Str("url");
                if (SafeUrl(src) != null)
                {
                    Fit(pic.Long("width"), pic.Long("height"), image != null ? 400 : 160, image != null ? 300 : 120, out ow, out oh);
                    string target = SafeUrl(e.Str("url")) != null ? e.Str("url") : pic.Str("url");
                    sb.Append("<div><a href=\"").Append(E(AppLink("open", target))).Append("\"><img src=\"").Append(SafeUrl(WithQuery(src, "format=png&width=" + ow + "&height=" + oh))).Append("\" width=\"").Append(ow).Append("\" height=\"").Append(oh).Append("\"></a></div>");
                }
            }
            var footer = e.Obj("footer");
            if (footer != null && footer.Str("text") != null) sb.Append("<div class=\"t\">").Append(E(footer.Str("text"))).Append("</div>");
            sb.Append("</div>");
            // Bare link previews with nothing to show are noise.
            if (e.Str("title") == null && e.Str("description") == null && pic == null && (author == null || author.Str("name") == null)) return "";
            return sb.ToString();
        }

        // ---- message text (Discord markdown subset) ----

        static readonly Regex CodeBlock = new Regex(@"```(?:[a-zA-Z0-9_+-]*\n)?([\s\S]*?)```", RegexOptions.Compiled);
        static readonly Regex InlineCode = new Regex(@"`([^`\n]+)`", RegexOptions.Compiled);
        static readonly Regex UserMention = new Regex(@"&lt;@!?(\d+)&gt;", RegexOptions.Compiled);
        static readonly Regex RoleMention = new Regex(@"&lt;@&amp;(\d+)&gt;", RegexOptions.Compiled);
        static readonly Regex ChannelMention = new Regex(@"&lt;#(\d+)&gt;", RegexOptions.Compiled);
        static readonly Regex CustomEmoji = new Regex(@"&lt;(a?):(\w+):(\d+)&gt;", RegexOptions.Compiled);
        static readonly Regex Timestamp = new Regex(@"&lt;t:(-?\d+)(?::([tTdDfFR]))?&gt;", RegexOptions.Compiled);
        static readonly Regex MaskedLink = new Regex(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)", RegexOptions.Compiled);
        static readonly Regex BareLink = new Regex(@"(?<![""=])(https?://[^\s<]+[^\s<.,:;""')\]!?])", RegexOptions.Compiled);
        static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Underline = new Regex(@"__(.+?)__", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Italic = new Regex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])", RegexOptions.Compiled);
        static readonly Regex Strike = new Regex(@"~~(.+?)~~", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Spoiler = new Regex(@"\|\|(.+?)\|\|", RegexOptions.Compiled | RegexOptions.Singleline);

        public static string Content(Session s, string text)
        {
            var slots = new List<string>();
            Func<string, string> hold = html => { slots.Add(html); return "\u0001" + (slots.Count - 1) + "\u0002"; };

            text = CodeBlock.Replace(text, m => hold("<pre>" + E(m.Groups[1].Value.TrimEnd('\n')) + "</pre>"));
            text = InlineCode.Replace(text, m => hold("<code>" + E(m.Groups[1].Value) + "</code>"));
            // Color emoji as pictures (IE draws them in black and white); emoji-only messages are big.
            int px = Emoji.OnlyEmoji(text) ? 44 : 20;
            text = Emoji.ReplaceAll(text, e => { string img = EmojiImg(e, px); return img != null ? hold(img) : e; });
            text = E(text);

            text = UserMention.Replace(text, m => hold("<a class=\"mention\" href=\"" + AppLink("profile", m.Groups[1].Value) + "\">@" + E(s.NameOf(m.Groups[1].Value)) + "</a>"));
            text = RoleMention.Replace(text, m => hold("<span class=\"mention\">@role</span>"));
            text = ChannelMention.Replace(text, m =>
            {
                var c = s.FindChannel(m.Groups[1].Value);
                return hold("<span class=\"mention\">#" + E(c != null ? c.Name ?? s.ChannelTitle(c) : "channel") + "</span>");
            });
            text = CustomEmoji.Replace(text, m => hold("<img class=\"e\" src=\"https://cdn.discordapp.com/emojis/" + m.Groups[3].Value
                + (m.Groups[1].Value == "a" ? ".gif" : ".png") + "?size=48\" alt=\":" + m.Groups[2].Value + ":\" title=\":" + m.Groups[2].Value + ":\">"));
            text = Timestamp.Replace(text, m =>
            {
                long v;
                if (!long.TryParse(m.Groups[1].Value, out v)) return m.Value;
                var t = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(v).ToLocalTime();
                string f = m.Groups[2].Success ? m.Groups[2].Value : "f";
                string str;
                switch (f)
                {
                    case "t": str = t.ToString("h:mm tt"); break;
                    case "T": str = t.ToString("h:mm:ss tt"); break;
                    case "d": str = t.ToString("MM/dd/yyyy"); break;
                    case "D": str = t.ToString("MMMM d, yyyy"); break;
                    case "F": str = t.ToString("dddd, MMMM d, yyyy h:mm tt"); break;
                    case "R": str = Relative(t); break;
                    default: str = t.ToString("MMMM d, yyyy h:mm tt"); break;
                }
                return hold("<code>" + E(str) + "</code>");
            });
            text = MaskedLink.Replace(text, m => hold("<a href=\"" + m.Groups[2].Value + "\">" + m.Groups[1].Value + "</a>"));
            text = BareLink.Replace(text, m => hold("<a href=\"" + m.Groups[1].Value + "\">" + m.Groups[1].Value + "</a>"));

            text = Bold.Replace(text, "<b>$1</b>");
            text = Underline.Replace(text, "<u>$1</u>");
            text = Italic.Replace(text, m => "<i>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</i>");
            text = Strike.Replace(text, "<s>$1</s>");
            text = Spoiler.Replace(text, "<span class=\"sp\">$1</span>");

            // Line-level markup: quotes and headings.
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("&gt; ")) line = "<blockquote>" + line.Substring(5) + "</blockquote>";
                else if (line.StartsWith("### ")) line = "<b>" + line.Substring(4) + "</b>";
                else if (line.StartsWith("## ")) line = "<b style=\"font-size:15px\">" + line.Substring(3) + "</b>";
                else if (line.StartsWith("# ")) line = "<b style=\"font-size:17px\">" + line.Substring(2) + "</b>";
                else if (line.StartsWith("-# ")) line = "<span class=\"t\">" + line.Substring(3) + "</span>";
                sb.Append(line);
                if (i < lines.Length - 1 && !line.EndsWith("</blockquote>")) sb.Append("<br>");
            }
            text = sb.ToString();

            for (int pass = 0; pass < 3 && text.IndexOf('\u0001') >= 0; pass++)
                text = Regex.Replace(text, "\u0001(\\d+)\u0002", m => slots[int.Parse(m.Groups[1].Value)]);
            return text;
        }

        static string Relative(DateTime t)
        {
            var d = t - DateTime.Now;
            bool past = d.TotalSeconds < 0;
            d = d.Duration();
            string v = d.TotalDays >= 365 ? (int)(d.TotalDays / 365) + " years"
                : d.TotalDays >= 30 ? (int)(d.TotalDays / 30) + " months"
                : d.TotalDays >= 1 ? (int)d.TotalDays + " days"
                : d.TotalHours >= 1 ? (int)d.TotalHours + " hours"
                : d.TotalMinutes >= 1 ? (int)d.TotalMinutes + " minutes" : "a few seconds";
            return past ? v + " ago" : "in " + v;
        }
    }
}
