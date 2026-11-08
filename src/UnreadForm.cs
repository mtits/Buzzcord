using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Everything behind the unread number on the tray icon and taskbar: one row per chat on every
    // account, with the unread messages themselves and Open / Mark as Read. It is built from
    // Session.UnreadEntries, the same list those numbers add up, so they always agree.
    public class UnreadForm : Form
    {
        static UnreadForm shared;

        class Detail
        {
            public List<string> Previews = new List<string>();
            public string Title, LastId, Problem;
            public int Count;   // unread count it was loaded for; reloaded when that changes
        }

        readonly AppContext app;
        readonly FlowLayoutPanel rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        readonly Label total = new Label { AutoSize = true, Location = new Point(10, 12) };
        readonly Button markAll = new Button { Text = "Mark All as Read", AutoSize = true };
        readonly Timer refresh = new Timer { Interval = 1500 };
        readonly Dictionary<string, Detail> details = new Dictionary<string, Detail>();
        readonly HashSet<string> loading = new HashSet<string>();
        string shownSignature;

        public static void Open(AppContext app)
        {
            if (shared == null || shared.IsDisposed) shared = new UnreadForm(app);
            shared.Rebuild(true);
            shared.Show();
            if (shared.WindowState == FormWindowState.Minimized) shared.WindowState = FormWindowState.Normal;
            shared.Activate();
        }

        UnreadForm(AppContext app)
        {
            this.app = app;
            Text = "Unread Messages";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            Size = new Size(480, 540);
            MinimumSize = new Size(360, 260);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            BackColor = Theme.Window;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Panel };
            markAll.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            footer.Controls.Add(total);
            footer.Controls.Add(markAll);
            footer.Resize += (s, e) => markAll.Location = new Point(footer.Width - markAll.Width - 10, 7);
            markAll.Click += (s, e) => MarkAll();

            rows.BackColor = Theme.Window;
            rows.Resize += (s, e) => { foreach (Control c in rows.Controls) c.Width = RowWidth; };
            Controls.Add(rows);
            Controls.Add(footer);
            refresh.Tick += (s, e) => Rebuild(false);
            refresh.Start();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Theme.Attach(this, () => { BackColor = rows.BackColor = Theme.Window; footer.BackColor = Theme.Panel; shownSignature = null; Rebuild(false); });
            Theme.Apply(this);
        }

        int RowWidth { get { return Math.Max(200, rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2); } } // never wide enough to need a sideways scrollbar

        static string Key(Session s, string channelId) { return s.TokenKey + "/" + channelId; }

        // Rebuilds the rows when what's unread (or what's loaded for it) changed.
        void Rebuild(bool force)
        {
            var sig = new StringBuilder();
            int sum = 0;
            foreach (var w in app.Windows)
                foreach (var e in w.Session.UnreadEntries())
                {
                    string k = Key(w.Session, e.Key);
                    Detail old;
                    if (details.TryGetValue(k, out old) && old.Count != e.Value) details.Remove(k);
                    sig.Append(k).Append(':').Append(e.Value).Append(details.ContainsKey(k) ? "+" : "").Append(';');
                    sum += e.Value;
                }
            string signature = sig.ToString();
            total.Text = sum == 1 ? "1 unread" : sum + " unread";
            markAll.Enabled = sum > 0;
            if (!force && signature == shownSignature) return;
            shownSignature = signature;

            rows.SuspendLayout();
            foreach (Control c in rows.Controls) c.Dispose();
            rows.Controls.Clear();
            bool many = app.Windows.Count > 1;
            foreach (var w in app.Windows)
            {
                var entries = w.Session.UnreadEntries();
                if (entries.Count == 0) continue;
                if (many)
                {
                    int n = 0;
                    foreach (var e in entries) n += e.Value;
                    rows.Controls.Add(new Label
                    {
                        Text = (w.Session.Me != null ? w.Session.Me.DisplayName : "Account") + " (" + n + ")",
                        Font = new Font("Tahoma", 8.25f, FontStyle.Bold), ForeColor = Theme.Accent, BackColor = Theme.GroupBack,
                        AutoSize = false, Width = RowWidth, Height = 22, Padding = new Padding(8, 4, 0, 0), Margin = new Padding(0)
                    });
                }
                foreach (var e in entries) rows.Controls.Add(MakeRow(w, e.Key, e.Value));
            }
            if (sum == 0)
                rows.Controls.Add(new Label { Text = "Nothing unread.", ForeColor = Theme.SubText, AutoSize = false, Width = RowWidth, Height = 60, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0) });
            rows.ResumeLayout();
            LoadMissing();
        }

        Control MakeRow(BuddyListForm w, string channelId, int count)
        {
            var s = w.Session;
            string key = Key(s, channelId);
            Detail d;
            details.TryGetValue(key, out d);
            var c = s.FindChannel(channelId);

            var row = new Panel { Width = RowWidth, Margin = new Padding(0), Padding = new Padding(10, 6, 10, 6), BackColor = Theme.Window };
            int y = 6;
            var title = new Label { Text = TitleOf(s, c, d), Font = new Font("Tahoma", 9f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true, Location = new Point(10, y), Height = 18 };
            var badge = new Label { Text = count.ToString(), Font = new Font("Tahoma", 7.5f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.FromArgb(0xD8, 0x2C, 0x2C), AutoSize = true, Padding = new Padding(3, 0, 3, 0) };
            var when = new Label { Text = When(c != null && c.LastMessageId != null ? c.LastMessageId : d != null ? d.LastId : null), ForeColor = Theme.SubText, AutoSize = true };
            row.Controls.AddRange(new Control[] { title, badge, when });
            y += 20;
            row.Controls.Add(new Label { Text = KindOf(s, c, count), ForeColor = Theme.SubText, AutoSize = true, Location = new Point(10, y) });
            y += 16;
            var lines = d == null ? new List<string> { "Loading..." } : d.Problem != null ? new List<string> { d.Problem } : d.Previews;
            foreach (var line in lines)
            {
                row.Controls.Add(new Label { Text = line, ForeColor = d == null || d.Problem != null ? Theme.SubText : Theme.Text, AutoSize = false, AutoEllipsis = true, Location = new Point(10, y), Height = 16, Width = RowWidth - 24, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, UseMnemonic = false });
                y += 16;
            }
            y += 4;
            int bx = 10;
            if (c != null)
            {
                var openButton = new Button { Text = "Open", AutoSize = true, Location = new Point(bx, y) };
                openButton.Click += (o, e) => { w.OpenChatById(channelId); };
                row.Controls.Add(openButton);
                bx += openButton.PreferredSize.Width + 6;
            }
            var read = new Button { Text = "Mark as Read", AutoSize = true, Location = new Point(bx, y) };
            read.Click += (o, e) => MarkRead(s, channelId);
            row.Controls.Add(read);
            row.Height = y + read.PreferredSize.Height + 8;

            Action layout = () =>
            {
                when.Location = new Point(row.Width - when.PreferredWidth - 10, 8);
                title.Width = Math.Max(40, Math.Min(title.PreferredWidth, when.Left - badge.PreferredSize.Width - 24));
                badge.Location = new Point(title.Right + 4, 9);
            };
            row.Resize += (o, e) => layout();
            layout();
            row.Paint += (o, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
            if (c != null)
            {
                EventHandler dbl = (o, e) => w.OpenChatById(channelId);
                row.DoubleClick += dbl;
                foreach (Control child in row.Controls) if (child is Label) child.DoubleClick += dbl;
            }
            return row;
        }

        static string TitleOf(Session s, Channel c, Detail d)
        {
            if (c != null)
            {
                if (c.IsPrivate) return s.ChannelTitle(c);
                var g = s.GuildOf(c);
                return "#" + c.Name + (g != null ? " — " + g.Name : "");
            }
            return d != null && d.Title != null ? d.Title : "A thread or channel";
        }

        static string KindOf(Session s, Channel c, int count)
        {
            string mentions = count == 1 ? "1 mention" : count + " mentions";
            if (c == null || !c.IsPrivate) return mentions;
            string what = c.IsGroup ? "Group chat" : "Direct message";
            return s.ReadStates.ContainsKey(c.Id) ? what : what + ", never opened";
        }

        static string When(string id)
        {
            if (id == null) return "";
            var d = DateTime.Now - Snowflake.Time(id);
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min ago";
            if (d.TotalDays < 1) return (int)d.TotalHours + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            if (d.TotalDays < 7) return (int)d.TotalDays + (d.TotalDays < 2 ? " day ago" : " days ago");
            return Snowflake.Time(id).ToString("MMM d");
        }

        void LoadMissing()
        {
            foreach (var w in app.Windows)
                foreach (var e in w.Session.UnreadEntries())
                {
                    string k = Key(w.Session, e.Key);
                    if (details.ContainsKey(k) || loading.Contains(k) || loading.Count >= 20) continue;
                    loading.Add(k);
                    LoadDetail(w.Session, e.Key, e.Value, k);
                }
        }

        // The unread messages themselves: the newest few in a DM or group, and in a server channel
        // the ones that mention you.
        async void LoadDetail(Session s, string channelId, int count, string key)
        {
            var d = new Detail { Count = count };
            try
            {
                if (s.FindChannel(channelId) == null)
                {
                    var j = await s.Api.Get("/channels/" + channelId) as JObj;
                    if (j != null)
                    {
                        Guild g = null;
                        if (j.Str("guild_id") != null) s.Guilds.TryGetValue(j.Str("guild_id"), out g);
                        d.Title = "#" + (j.Str("name") ?? "thread") + (g != null ? " — " + g.Name : "");
                        d.LastId = j.Str("last_message_id");
                    }
                }
                var c = s.FindChannel(channelId);
                string read;
                s.ReadStates.TryGetValue(channelId, out read);
                bool isPrivate = c != null && c.IsPrivate;
                string q = "/channels/" + channelId + "/messages?limit=" + (isPrivate ? Math.Min(5, Math.Max(count, 1)) : 50);
                if (!isPrivate && read != null) q += "&after=" + read;
                var msgs = new List<JObj>();
                var list = await s.Api.Get(q) as List<object>;
                if (list != null) foreach (var o in list) { var m = o as JObj; if (m != null) msgs.Add(m); }
                msgs.Sort((a, b) => Snowflake.Compare(a.Str("id"), b.Str("id")));   // oldest first
                if (read != null) msgs.RemoveAll(m => Snowflake.Compare(m.Str("id"), read) <= 0);
                if (!isPrivate)
                {
                    var mentioning = msgs.FindAll(m => s.MentionsMe(m));
                    if (mentioning.Count > 0) msgs = mentioning;
                }
                if (d.LastId == null && msgs.Count > 0) d.LastId = msgs[msgs.Count - 1].Str("id");
                for (int i = Math.Max(0, msgs.Count - 3); i < msgs.Count; i++)
                {
                    var m = msgs[i];
                    string text = m.Str("content") ?? "";
                    if (text.Length == 0)
                    {
                        var atts = m.Arr("attachments");
                        var embeds = m.Arr("embeds");
                        text = atts != null && atts.Count > 0 ? "(sent a file)" : embeds != null && embeds.Count > 0 ? "(a link or embed)" : "(no text)";
                    }
                    var author = m.Obj("author");
                    d.Previews.Add((author != null ? s.NameOf(author.Str("id")) : "?") + ": " + text.Replace("\r", " ").Replace("\n", " "));
                }
            }
            catch (Exception ex)
            {
                d.Problem = "Couldn't load the messages: " + ex.Message;
            }
            loading.Remove(key);
            if (IsDisposed) return;
            details[key] = d;
            Rebuild(false);
        }

        void MarkRead(Session s, string channelId)
        {
            string key = Key(s, channelId);
            var c = s.FindChannel(channelId);
            Detail d;
            details.TryGetValue(key, out d);
            if (c != null && c.LastMessageId != null) s.MarkRead(c);
            else if (d != null && d.LastId != null) s.MarkRead(channelId, d.LastId);
            details.Remove(key);
            Rebuild(false);
        }

        async void MarkAll()
        {
            if (MessageBox.Show(this, "Mark everything here as read, on every account? Discord's other apps will agree.", "Buzzcord",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            markAll.Enabled = false;
            var todo = new List<KeyValuePair<Session, string>>();
            foreach (var w in app.Windows)
                foreach (var e in w.Session.UnreadEntries()) todo.Add(new KeyValuePair<Session, string>(w.Session, e.Key));
            for (int i = 0; i < todo.Count; i++)
            {
                if (IsDisposed) return;
                MarkRead(todo[i].Key, todo[i].Value);
                // Discord's read markers are sent at most once a second per account.
                if (i < todo.Count - 1) await Task.Delay(1100);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refresh.Dispose();
            base.Dispose(disposing);
        }
    }
}
