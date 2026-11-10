using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Buzzcord
{
    // Discord's upload limits, and the checks Buzzcord makes before it attaches or sends a file.
    // Buzzcord never splits, shrinks or re-encodes a file to fit, and never retries a file Discord
    // refused: Discord's limit is the limit. Mirrors mac/Sources/Buzzcord/Uploads.swift; keep the
    // two tables identical.
    public static class UploadLimits
    {
        // Sources, checked 2026-11-10:
        //  - support.discord.com "File Attachments FAQ" (article 25444343291031): 20MB without Nitro
        //    ("As of August 2026 ... up from 10MB"), Nitro Basic 50MB, Nitro up to 1GB.
        //  - support.discord.com "What are Nitro & Nitro Basic?" (115000435108): Nitro Classic 50MB.
        //  - support.discord.com "Server Boosting FAQ" (360028038352): Level 2 50MB, Level 3 100MB,
        //    "for all members (server only)". Levels 0 and 1 add nothing.
        //  - docs.discord.com/developers/reference "Uploading Files": the limit "applies to each file",
        //    and is given in MiB (default 20 MiB). premium_type and premium_tier values from the
        //    User and Guild resource pages.
        //  - 10 files per message: what Discord's own app allows; the help center doesn't state it.
        // Discord also runs experiments (a 250MB server add-on, larger limits for some users) that
        // nothing in the API shows. If Discord refuses a file anyway, its answer is final.
        public const long MB = 1048576;
        public const long Free = 20 * MB;
        public const long NitroClassic = 50 * MB;   // premium_type 1 (no longer sold)
        public const long Nitro = 1024 * MB;        // premium_type 2
        public const long NitroBasic = 50 * MB;     // premium_type 3
        public const long BoostLevel2 = 50 * MB;    // guild premium_tier 2
        public const long BoostLevel3 = 100 * MB;   // guild premium_tier 3
        public const int MaxFiles = 10;

        // The limit that comes with the account (READY user.premium_type).
        public static long Account(int premiumType)
        {
            switch (premiumType)
            {
                case 1: return NitroClassic;
                case 2: return Nitro;
                case 3: return NitroBasic;
                default: return Free;
            }
        }

        // The limit a server's boost level gives everyone in it (guild premium_tier).
        public static long Server(int tier)
        {
            switch (tier)
            {
                case 3: return BoostLevel3;
                case 2: return BoostLevel2;
                default: return Free;
            }
        }

        // In a server channel the larger of the two; in DMs and group chats (guildTier null) the
        // account's own limit.
        public static long Effective(int premiumType, int? guildTier)
        {
            long a = Account(premiumType);
            if (guildTier == null) return a;
            return Math.Max(a, Server(guildTier.Value));
        }
    }

    // Where a file is being sent, as far as the limit goes.
    public class UploadContext
    {
        public int PremiumType;
        public int? GuildTier;   // null in DMs and group chats

        public long Limit { get { return UploadLimits.Effective(PremiumType, GuildTier); } }
    }

    public static class Uploads
    {
        // "84 MB", "9.6 MB", "820 KB", "1 GB". roundUp keeps a file just over the limit from
        // reading as exactly the limit.
        public static string Size(long bytes, bool roundUp = false)
        {
            double mb = (double)bytes / UploadLimits.MB;
            if (mb >= 1024) return Trim(Round1(mb / 1024, roundUp)) + " GB";
            if (mb >= 100) return ((long)(roundUp ? Math.Ceiling(mb) : Math.Round(mb, MidpointRounding.AwayFromZero))).ToString(CultureInfo.InvariantCulture) + " MB";
            if (mb >= 1) return Trim(Round1(mb, roundUp)) + " MB";
            double kb = bytes / 1024.0;
            return Math.Max(1, (long)(roundUp ? Math.Ceiling(kb) : Math.Round(kb, MidpointRounding.AwayFromZero))).ToString(CultureInfo.InvariantCulture) + " KB";
        }

        static double Round1(double v, bool up) { return up ? Math.Ceiling(v * 10) / 10 : Math.Round(v * 10, MidpointRounding.AwayFromZero) / 10; }

        static string Trim(double v)
        {
            return v == Math.Round(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // "You can send up to 20 MB here (50 MB with Nitro Basic, 1 GB with Nitro, or 50-100 MB in
        // a boosted server)." Worded for the account and the chat.
        public static string LimitSentence(UploadContext ctx)
        {
            long limit = ctx.Limit;
            long account = UploadLimits.Account(ctx.PremiumType);
            string s = "You can send up to " + Size(limit);
            if (ctx.GuildTier != null && UploadLimits.Server(ctx.GuildTier.Value) > account)
                s += " in this server, thanks to its Level " + ctx.GuildTier.Value + " boost";
            else
            {
                switch (ctx.PremiumType)
                {
                    case 1: s += " with Nitro Classic"; break;
                    case 2: s += " with Nitro"; break;
                    case 3: s += " with Nitro Basic"; break;
                    default: s += " here"; break;
                }
            }
            var more = new List<string>();
            if (account < UploadLimits.NitroBasic && UploadLimits.NitroBasic > limit) more.Add(Size(UploadLimits.NitroBasic) + " with Nitro Basic");
            if (account < UploadLimits.Nitro && UploadLimits.Nitro > limit) more.Add(Size(UploadLimits.Nitro) + " with Nitro");
            if (ctx.GuildTier != null)
            {
                int tier = ctx.GuildTier.Value;
                bool l2 = tier < 2 && UploadLimits.BoostLevel2 > limit, l3 = tier < 3 && UploadLimits.BoostLevel3 > limit;
                if (l2 && l3) more.Add((UploadLimits.BoostLevel2 / UploadLimits.MB) + "-" + Size(UploadLimits.BoostLevel3) + " in a boosted server");
                else if (l3) more.Add(Size(UploadLimits.BoostLevel3) + " if this server reaches boost Level 3");
            }
            if (more.Count == 0) return s + ", the most Discord allows.";
            return s + " (" + List(more, "or") + ").";
        }

        // Said when a file over the limit is attached; it isn't attached.
        public static string TooBig(string name, long bytes, UploadContext ctx)
        {
            return name + " is " + Size(bytes, true) + ". " + LimitSentence(ctx);
        }

        // Discord refused an upload as too large (413, or error 40005 / 50045). Not retried.
        public static string Refused(UploadContext ctx)
        {
            return "Discord refused the file as too big, so nothing was sent. " + LimitSentence(ctx);
        }

        public static string TooMany(List<string> names)
        {
            string who;
            if (names.Count == 1) who = names[0] + " wasn't";
            else if (names.Count == 2) who = names[0] + " and " + names[1] + " weren't";
            else who = names[0] + " and " + (names.Count - 1) + " more weren't";
            return "Discord allows " + UploadLimits.MaxFiles + " files per message, so " + who + " attached. Send these first, then add the rest.";
        }

        public static string Folder(string name) { return name + " is a folder. Zip it first to send it."; }

        public static string List(List<string> items, string conj)
        {
            if (items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            if (items.Count == 2) return items[0] + " " + conj + " " + items[1];
            return string.Join(", ", items.GetRange(0, items.Count - 1)) + ", " + conj + " " + items[items.Count - 1];
        }

        // Discord said the file is too big: HTTP 413, or error 40005 (request entity too large) or
        // 50045 (file exceeds the maximum size).
        public static bool IsTooLarge(Exception ex)
        {
            var api = ex as ApiException;
            if (api == null && ex != null) api = ex.GetBaseException() as ApiException;
            return api != null && api.IsTooLarge;
        }

        public class Candidate
        {
            public string Name;
            public long Size;
            public bool IsFolder;

            public static Candidate FromPath(string path)
            {
                if (Directory.Exists(path)) return new Candidate { Name = Path.GetFileName(path.TrimEnd('\\', '/')), IsFolder = true };
                return new Candidate { Name = Path.GetFileName(path), Size = File.Exists(path) ? new FileInfo(path).Length : 0 };
            }
        }

        public class CheckResult
        {
            public List<int> Accepted = new List<int>();   // indexes into the candidates
            public List<string> Notes = new List<string>();
        }

        // Which of the candidates can be attached next to `existing` files already in the tray, and
        // what to tell the user about the rest.
        public static CheckResult Check(int existing, List<Candidate> files, UploadContext ctx)
        {
            var r = new CheckResult();
            var fits = new List<int>();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f.IsFolder) { r.Notes.Add(Folder(f.Name)); continue; }
                if (f.Size > ctx.Limit) { r.Notes.Add(TooBig(f.Name, f.Size, ctx)); continue; }
                fits.Add(i);
            }
            int room = Math.Max(0, UploadLimits.MaxFiles - existing);
            for (int i = 0; i < fits.Count && i < room; i++) r.Accepted.Add(fits[i]);
            if (fits.Count > room)
            {
                var names = new List<string>();
                for (int i = room; i < fits.Count; i++) names.Add(files[fits[i]].Name);
                r.Notes.Add(TooMany(names));
            }
            return r;
        }

        // A note for the tray when a file is close to the limit (or over it, if the limit dropped
        // after it was attached), else null.
        public static string TrayWarning(List<Candidate> files, UploadContext ctx, out bool over)
        {
            long limit = ctx.Limit;
            over = false;
            foreach (var f in files)
                if (f.Size > limit) { over = true; return f.Name + " is over the " + Size(limit) + " limit here. Remove it to send."; }
            foreach (var f in files)
                if (f.Size >= limit * 0.9) return f.Name + " is close to the " + Size(limit) + " limit here.";
            return null;
        }

        // "3 files, 12.4 MB. Up to 20 MB each here. Enter sends, Esc removes all."
        public static string Summary(List<Candidate> files, UploadContext ctx)
        {
            long total = 0;
            foreach (var f in files) total += f.Size;
            return files.Count + (files.Count == 1 ? " file, " : " files, ") + Size(total) + ". Up to " + Size(ctx.Limit) + " each here"
                + (files.Count >= UploadLimits.MaxFiles ? ", " + UploadLimits.MaxFiles + " files at most. " : ". ") + "Enter sends, Esc removes all.";
        }
    }

    // What to do with the clipboard on Paste. Choose is pure, so it can be tested with plain lists
    // of clipboard format names.
    public static class PastePick
    {
        public const string FileDrop = "FileDrop";                 // CF_HDROP: files copied in Explorer
        public const string RichText = "Rich Text Format";
        public const string Html = "HTML Format";
        public const string Bitmap = "Bitmap";
        public const string Dib = "DeviceIndependentBitmap";

        // Clipboard data kept in the format it came in. GIF first: a browser also puts a still PNG
        // and a bitmap of the first frame alongside, and Clipboard.GetImage() would lose the
        // animation. The others come after the picture's own address (Html), which gives the
        // original file when a browser copied it.
        static readonly string[][] gifFormats = { new[] { "GIF", "gif" }, new[] { "image/gif", "gif" } };
        static readonly string[][] otherFormats =
        {
            new[] { "image/webp", "webp" },
            new[] { "JFIF", "jpg" }, new[] { "image/jpeg", "jpg" },
            new[] { "PNG", "png" }, new[] { "image/png", "png" },
            new[] { "video/mp4", "mp4" }, new[] { "video/quicktime", "mov" },
        };

        public enum Kind { Files, Data, Remote, Image, Text }

        public class Choice
        {
            public Kind Kind;
            public string Format, Ext;   // Data: the clipboard format to read and the extension to save with
            public string Url;           // Remote: the copied picture's address (http(s) or data:)

            public override string ToString() { return Kind + (Format != null ? " " + Format + "." + Ext : "") + (Url != null ? " " + Url : ""); }
        }

        // formats: the clipboard's format names. text: its plain text, if any. imageSrc: the one
        // picture address in its "HTML Format" fragment, if that's all the fragment holds.
        public static Choice Choose(IList<string> formats, string text, string imageSrc)
        {
            var have = new HashSet<string>(formats);
            if (have.Contains(FileDrop)) return new Choice { Kind = Kind.Files };
            // Copied text stays text, even when the app put a picture of it alongside (Word, Excel,
            // OneNote): real words, or anything copied with rich text. A browser's "Copy image"
            // puts only the picture's address there, which isn't text the user meant to paste.
            if (text != null && (!IsJustLink(text) || have.Contains(RichText))) return new Choice { Kind = Kind.Text };
            foreach (var f in gifFormats) if (have.Contains(f[0])) return new Choice { Kind = Kind.Data, Format = f[0], Ext = f[1] };
            if (imageSrc != null) return new Choice { Kind = Kind.Remote, Url = imageSrc };
            foreach (var f in otherFormats) if (have.Contains(f[0])) return new Choice { Kind = Kind.Data, Format = f[0], Ext = f[1] };
            if (have.Contains(Bitmap) || have.Contains(Dib)) return new Choice { Kind = Kind.Image };
            return new Choice { Kind = Kind.Text };
        }

        // Fallback when Remote couldn't be fetched: the best data the clipboard itself has.
        public static Choice Local(IList<string> formats)
        {
            var have = new HashSet<string>(formats);
            foreach (var f in otherFormats) if (have.Contains(f[0])) return new Choice { Kind = Kind.Data, Format = f[0], Ext = f[1] };
            if (have.Contains(Bitmap) || have.Contains(Dib)) return new Choice { Kind = Kind.Image };
            return null;
        }

        // Browsers put the picture's address next to a copied picture; that alone isn't text the
        // user meant to paste.
        public static bool IsJustLink(string s)
        {
            string t = s.Trim();
            if (t.Length == 0) return true;
            foreach (char c in t) if (char.IsWhiteSpace(c)) return false;
            Uri u;
            if (!Uri.TryCreate(t, UriKind.Absolute, out u)) return false;
            string scheme = u.Scheme.ToLowerInvariant();
            return scheme == "http" || scheme == "https" || scheme == "file" || scheme == "data" || scheme == "blob";
        }

        static readonly Regex ImgTag = new Regex(@"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Tags = new Regex(@"<[^>]*>", RegexOptions.Compiled);

        // The picture's address from a browser's "HTML Format" clipboard text, when the copied
        // fragment is just one picture (no words around it).
        public static string ImageSrc(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            string frag = html;
            int a = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int b = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (a >= 0 && b > a) frag = html.Substring(a + 20, b - a - 20);
            var ms = ImgTag.Matches(frag);
            if (ms.Count != 1) return null;
            if (System.Net.WebUtility.HtmlDecode(Tags.Replace(frag, "")).Trim().Length > 0) return null;
            string src = System.Net.WebUtility.HtmlDecode(ms[0].Groups[1].Success ? ms[0].Groups[1].Value : ms[0].Groups[2].Value).Trim();
            if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return src;
            return null;
        }

        // The extension that fits the bytes, or null if they aren't a picture or video Discord shows.
        public static string Sniff(byte[] d)
        {
            if (d == null || d.Length < 12) return null;
            if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return "gif";
            if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "png";
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
            if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "webp";
            if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p') return d[8] == 'q' && d[9] == 't' ? "mov" : "mp4";
            return null;
        }

        // A data: URL's bytes, or null.
        public static byte[] DataUrl(string url)
        {
            int comma = url.IndexOf(',');
            if (comma < 0 || url.Substring(0, comma).IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0) return null;
            try { return Convert.FromBase64String(url.Substring(comma + 1)); }
            catch (FormatException) { return null; }
        }

        // The copied picture's original file: decoded from a data: URL, or downloaded from its
        // address. Null if it can't be had or isn't a picture or video.
        public static async Task<byte[]> FetchOriginal(string url)
        {
            byte[] d;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) d = DataUrl(url);
            else d = await Api.GetBytes(url);
            return Sniff(d) != null ? d : null;
        }

        // A new file in %TEMP%\Buzzcord: image-yyyyMMdd-HHmmss.gif or video-....mp4 (-2, -3 for
        // more pasted within the same second).
        public static string SaveTemp(byte[] data, string ext)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Buzzcord");
            Directory.CreateDirectory(dir);
            string stamp = (ext == "mp4" || ext == "mov" ? "video-" : "image-") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, stamp + "." + ext);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, stamp + "-" + n + "." + ext);
            File.WriteAllBytes(path, data);
            return path;
        }
    }
}
