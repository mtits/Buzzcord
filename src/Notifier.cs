using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Buzzcord
{
    public enum AlertKind { Message, Mention, FriendRequest, FriendAccepted, SignIn, Followed, Unfriended, Call }

    public class AlertEntry
    {
        public DateTime Time;
        public AlertKind Kind;
        public string Title, Text, ChannelId, UserId;
        public bool Seen;
    }

    // Every alert goes through here: it decides between a pop-up window, a sound and a tray
    // balloon (staying quiet while you're Busy or gaming), and keeps the history that the
    // Mentions window shows.
    public static class Notifier
    {
        public struct Decision { public bool Popup, Sound, Balloon; }

        public static AppContext App;
        public static event Action<Session> HistoryChanged;

        public static bool QuietWhileGaming { get { return Settings.GetBool("QuietWhileGaming", true); } }
        public static bool SignInAlerts { get { return Settings.GetBool("SignInAlerts", true); } }

        static bool Gaming
        {
            get { return PresenceManager.Instance != null && PresenceManager.Instance.Detector.Running.Any(); }
        }

        // Busy: nothing interrupts (history and taskbar flash only). Gaming: no windows or sounds,
        // just the tray balloon. Otherwise as configured.
        public static Decision Decide(string status, bool gaming, bool quietWhileGaming, bool popupSetting)
        {
            if (status == "dnd") return new Decision();
            if (gaming && quietWhileGaming) return new Decision { Balloon = true };
            return new Decision { Popup = popupSetting, Sound = true, Balloon = true };
        }

        public static Decision DecideFor(Session s)
        {
            return Decide(s.Status, Gaming, QuietWhileGaming, Settings.GetBool("PopupNew", true));
        }

        // Records the alert and plays/shows what the quiet rules allow. balloon=false when the
        // caller already showed something (an open chat window, a pop-up).
        public static void Alert(BuddyListForm from, AlertKind kind, string title, string text, string channelId, string userId, bool balloon)
        {
            Alert(from, kind, title, text, channelId, userId, balloon, null);
        }

        // messageId: for message alerts, which get the quick-reply pop-up instead of a balloon.
        public static void Alert(BuddyListForm from, AlertKind kind, string title, string text, string channelId, string userId, bool balloon, string messageId)
        {
            var s = from.Session;
            var d = DecideFor(s);
            Record(s, new AlertEntry { Time = DateTime.Now, Kind = kind, Title = title, Text = Shorten(text), ChannelId = channelId, UserId = userId });
            if (d.Sound)
            {
                if (kind == AlertKind.SignIn) Sounds.Door();
                else Sounds.Ding();
            }
            if (!d.Balloon || !balloon || App == null) return;
            bool message = kind == AlertKind.Message || kind == AlertKind.Mention || kind == AlertKind.Followed;
            // While a game runs, a window on top could interrupt it; the balloon is gentler there.
            if (message && messageId != null && channelId != null && !Gaming) App.QuickReply(from, title, text, channelId, messageId);
            else App.Balloon(from, title, text, channelId, userId);
        }

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

        // ---- history ----

        const int Keep = 300;
        static readonly Dictionary<string, List<AlertEntry>> history = new Dictionary<string, List<AlertEntry>>();

        static string PathFor(Session s) { return Path.Combine(Settings.Folder, "alerts-" + s.TokenKey + ".json"); }

        public static List<AlertEntry> History(Session s)
        {
            List<AlertEntry> list;
            if (history.TryGetValue(s.TokenKey, out list)) return list;
            list = new List<AlertEntry>();
            try
            {
                if (File.Exists(PathFor(s)))
                    foreach (var o in (Json.Parse(File.ReadAllText(PathFor(s), Encoding.UTF8)) as List<object>) ?? new List<object>())
                    {
                        var j = o as JObj;
                        if (j == null) continue;
                        AlertKind kind;
                        if (!Enum.TryParse(j.Str("kind"), out kind)) kind = AlertKind.Message;
                        DateTime time;
                        DateTime.TryParse(j.Str("time"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out time);
                        list.Add(new AlertEntry { Time = time, Kind = kind, Title = j.Str("title"), Text = j.Str("text"), ChannelId = j.Str("channel"), UserId = j.Str("user"), Seen = j.Bool("seen") });
                    }
            }
            catch (Exception ex) { Program.Log("alerts: " + ex.Message); }
            history[s.TokenKey] = list;
            return list;
        }

        public static int Unseen(Session s)
        {
            int n = 0;
            foreach (var a in History(s)) if (!a.Seen) n++;
            return n;
        }

        public static void MarkSeen(Session s)
        {
            bool changed = false;
            foreach (var a in History(s)) if (!a.Seen) { a.Seen = true; changed = true; }
            if (changed) Save(s);
        }

        public static void Clear(Session s)
        {
            History(s).Clear();
            Save(s);
        }

        static void Record(Session s, AlertEntry e)
        {
            var list = History(s);
            list.Insert(0, e);
            if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
            Save(s);
        }

        static void Save(Session s)
        {
            try
            {
                var arr = new List<object>();
                foreach (var a in History(s))
                    arr.Add(Json.O("time", a.Time.ToString("o"), "kind", a.Kind.ToString(), "title", a.Title, "text", a.Text,
                        "channel", a.ChannelId, "user", a.UserId, "seen", a.Seen));
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(PathFor(s), Json.Write(arr), Encoding.UTF8);
            }
            catch (Exception ex) { Program.Log("alerts save: " + ex.Message); }
            if (HistoryChanged != null) HistoryChanged(s);
        }
    }

    // Channels you pinned to Favorites, and channels you follow (alert on every message).
    public static class Favorites
    {
        public static event Action<Session> Changed;

        static List<string> Load(string key)
        {
            var list = new List<string>();
            foreach (var id in Settings.Get(key, "").Split('|')) if (id.Length > 0 && !list.Contains(id)) list.Add(id);
            return list;
        }

        static void Store(Session s, string key, List<string> ids)
        {
            Settings.Set(key, string.Join("|", ids));
            Settings.Save();
            if (Changed != null) Changed(s);
        }

        public static List<string> Pinned(Session s) { return Load("Pinned." + s.TokenKey); }
        public static bool IsPinned(Session s, string channelId) { return Pinned(s).Contains(channelId); }
        public static bool IsFollowed(Session s, string channelId) { return Load("Follow." + s.TokenKey).Contains(channelId); }

        public static void SetPinned(Session s, string channelId, bool on)
        {
            var list = Pinned(s);
            list.Remove(channelId);
            if (on) list.Add(channelId);
            Store(s, "Pinned." + s.TokenKey, list);
        }

        public static void SetFollowed(Session s, string channelId, bool on)
        {
            var list = Load("Follow." + s.TokenKey);
            list.Remove(channelId);
            if (on) list.Add(channelId);
            Store(s, "Follow." + s.TokenKey, list);
        }
    }

    // DM and group chat text, appended to "Chat log.txt" beside the auto-saved files. A
    // per-channel last-saved id keeps each message from being written twice.
    public static class Archive
    {
        static readonly string IndexPath = Path.Combine(Settings.Folder, "archive.idx");
        static Dictionary<string, string> last;

        public static bool Enabled { get { return Settings.GetBool("ArchiveChats", true); } }

        static void LoadIndex()
        {
            if (last != null) return;
            last = new Dictionary<string, string>();
            try
            {
                if (File.Exists(IndexPath))
                    foreach (var line in File.ReadAllLines(IndexPath))
                    {
                        var p = line.Split(' ');
                        if (p.Length == 2) last[p[0]] = p[1];
                    }
            }
            catch (Exception) { }
        }

        public static string LogPath(Session s, Channel c) { return Path.Combine(AutoSaver.FolderFor(s, c), "Chat log.txt"); }

        // Call for every message seen, oldest first. Returns true if it was written.
        public static bool Consider(Session s, JObj m)
        {
            if (!Enabled) return false;
            var c = s.FindChannel(m.Str("channel_id"));
            if (c == null || !c.IsPrivate) return false;
            LoadIndex();
            string id = m.Str("id"), prev;
            if (id == null || last.TryGetValue(c.Id, out prev) && Snowflake.Compare(id, prev) <= 0) return false;
            var author = m.Obj("author");
            string name = author != null ? s.NameOf(author.Str("id")) : "?";
            var sb = new StringBuilder();
            sb.Append('[').Append(Snowflake.Time(id).ToString("yyyy-MM-dd HH:mm")).Append("] ").Append(name).Append(": ");
            sb.Append((m.Str("content") ?? "").Replace("\r", "").Replace("\n", Environment.NewLine + "    "));
            var files = new List<string>();
            foreach (var a in m.Objs("attachments")) files.Add(a.Str("filename"));
            if (files.Count > 0) sb.Append(" [attachments: ").Append(string.Join(", ", files)).Append(']');
            if ((m.Str("content") ?? "").Length == 0 && files.Count == 0 && m.Arr("sticker_items") != null) sb.Append("[sticker]");
            try
            {
                string path = LogPath(s, c);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, sb.ToString() + Environment.NewLine, Encoding.UTF8);
                last[c.Id] = id;
                var lines = new List<string>();
                foreach (var kv in last) lines.Add(kv.Key + " " + kv.Value);
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllLines(IndexPath, lines);
                return true;
            }
            catch (Exception ex) { Program.Log("archive: " + ex.Message); return false; }
        }
    }
}
