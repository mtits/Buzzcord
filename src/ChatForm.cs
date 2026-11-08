using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // An IM window for one conversation: history on the left, the buddy (and what they're
    // playing) on the right, the typing box at the bottom.
    public class ChatForm : Form
    {
        const string BuzzText = "BUZZ!!!";

        readonly Session s;
        readonly Channel channel;
        ToolStripButton callButton;

        // Call / Join call / Hang up, depending on whether a call is going on here.
        void UpdateCallButton()
        {
            if (callButton == null) return;
            bool inCall = VoiceManager.IsIn(s, channel.Id);
            bool othersIn = s.VoiceMembers(channel.Id).Count > 0;
            callButton.Text = inCall ? "Hang up" : othersIn ? "Join call" : "Call";
            callButton.ForeColor = inCall ? Color.FromArgb(0xC8, 0x3A, 0x3A) : Color.FromArgb(0x2E, 0xA0, 0x43);
            callButton.ToolTipText = inCall ? "Leave the call" : othersIn ? "A call is going on here: join it" : "Start a voice call (rings them)";
        }
        readonly bool showInactive;
        readonly WebBrowser web = new WebBrowser();
        readonly TextBox input = new TextBox();
        readonly Button send = new Button { Text = "Send" };
        readonly BufferedPanel header = new BufferedPanel { Dock = DockStyle.Top, Height = 46 };
        readonly BufferedPanel side = new BufferedPanel { Dock = DockStyle.Right, Width = 158 };
        readonly ActivityCard sideCard = new ActivityCard { Dock = DockStyle.Bottom, Height = 0 };
        readonly Label banner = new Label { Dock = DockStyle.Top, Height = 0, BackColor = Color.FromArgb(0xFF, 0xF6, 0xCC), Padding = new Padding(4, 3, 4, 0) };
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly Dictionary<string, JObj> messages = new Dictionary<string, JObj>();
        readonly Dictionary<string, DateTime> typing = new Dictionary<string, DateTime>();
        readonly List<string> pendingFiles = new List<string>();
        readonly List<JObj> queued = new List<JObj>();   // messages that arrived before the page loaded
        readonly Timer timer = new Timer { Interval = 1000 };
        string oldestId, newestId, replyTo, editing;
        DateTime lastDay = DateTime.MinValue, lastTypingSent = DateTime.MinValue, lastCatchUp = DateTime.MinValue;
        bool pageReady, loadingOlder, noMoreOlder;

        MemberPanel members;

        public ChatForm(Session session, Channel channel, bool showInactive)
        {
            s = session;
            this.channel = channel;
            this.showInactive = showInactive;
            Text = s.ChannelTitle(channel) + " - Instant Message";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            Size = new Size(channel.IsDm ? 620 : 720, 480);
            MinimumSize = new Size(380, 300);
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            var rnd = new Random(channel.Id.GetHashCode());
            Location = new Point(wa.Left + 80 + rnd.Next(Math.Max(1, wa.Width - Width - 400)), wa.Top + 60 + rnd.Next(Math.Max(1, wa.Height - Height - 160)));
            KeyPreview = true;
            AllowDrop = true;

            BuildMenu();

            header.Paint += PaintHeader;

            web.Dock = DockStyle.Fill;
            web.ScriptErrorsSuppressed = true;
            web.AllowWebBrowserDrop = false;
            web.WebBrowserShortcutsEnabled = true;
            web.IsWebBrowserContextMenuEnabled = true;
            web.Navigating += WebNavigating;
            web.NewWindow += (o, e) => e.Cancel = true;
            web.DocumentCompleted += (o, e) =>
            {
                if (pageReady) return;
                pageReady = true;
                LoadHistory();
            };

            var webBorder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            webBorder.Controls.Add(web);

            // Bottom: toolbar + input + send.
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(0, 2, 0, 0) };
            var tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = Icons.Panel, RenderMode = ToolStripRenderMode.System };
            tools.Items.Add(Fmt("B", "**", FontStyle.Bold, "Bold"));
            tools.Items.Add(Fmt("I", "*", FontStyle.Italic, "Italic"));
            tools.Items.Add(Fmt("U", "__", FontStyle.Underline, "Underline"));
            tools.Items.Add(Fmt("S", "~~", FontStyle.Strikeout, "Strikethrough"));
            tools.Items.Add(Fmt("||", "||", FontStyle.Regular, "Spoiler"));
            tools.Items.Add(new ToolStripSeparator());
            var emoji = new ToolStripDropDownButton("☺") { ToolTipText = "Smileys", Font = new Font("Segoe UI Emoji", 10f) };
            BuildEmoji(emoji);
            tools.Items.Add(emoji);
            tools.Items.Add(new ToolStripButton("Send File", null, (o, e) => PickFiles()) { ToolTipText = "Send a picture, video or file" });
            if (channel.IsPrivate)
            {
                tools.Items.Add(new ToolStripButton("BUZZ!", null, (o, e) => SendText(BuzzText)) { ForeColor = Color.FromArgb(0xD0, 0x34, 0x34), Font = new Font("Arial", 8.25f, FontStyle.Bold | FontStyle.Italic), ToolTipText = "Shake their window (if they use Buzzcord)" });
                callButton = new ToolStripButton("Call") { Font = new Font("Tahoma", 8.25f, FontStyle.Bold) };
                callButton.Click += async (o, e) =>
                {
                    if (VoiceManager.IsIn(s, channel.Id)) { VoiceManager.Leave(); return; }
                    try { await VoiceManager.Join(s, channel); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
                };
                tools.Items.Add(callButton);
                Action callChanged = () => { if (!IsDisposed) UpdateCallButton(); };
                Action<string> channelCall = cid => { if (cid == channel.Id && !IsDisposed) UpdateCallButton(); };
                VoiceManager.Changed += callChanged;
                s.CallChanged += channelCall;
                s.VoiceStateChanged += channelCall;
                FormClosed += (o, e) => { VoiceManager.Changed -= callChanged; s.CallChanged -= channelCall; s.VoiceStateChanged -= channelCall; };
                UpdateCallButton();
            }

            input.Multiline = true;
            input.AcceptsReturn = false;
            input.ScrollBars = ScrollBars.Vertical;
            input.Font = new Font("Arial", 10f);
            input.Dock = DockStyle.Fill;
            input.MaxLength = 4000;
            input.KeyDown += InputKeyDown;
            input.TextChanged += (o, e) => SendTyping();
            input.AllowDrop = true;
            input.DragEnter += DragEnterFiles;
            input.DragDrop += DropFiles;

            send.Dock = DockStyle.Right;
            send.Width = 66;
            send.Font = new Font("Tahoma", 9f, FontStyle.Bold);
            send.Click += (o, e) => DoSend();

            var inputRow = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2) };
            var gap = new Panel { Dock = DockStyle.Right, Width = 4 };
            inputRow.Controls.Add(input);
            inputRow.Controls.Add(gap);
            inputRow.Controls.Add(send);
            bottom.Controls.Add(inputRow);
            bottom.Controls.Add(banner);
            bottom.Controls.Add(tools);
            banner.Click += (o, e) => ClearMode();
            banner.Cursor = Cursors.Hand;

            var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 4) };
            center.Controls.Add(webBorder);
            center.Controls.Add(bottom);

            if (!channel.IsDm)
            {
                // Server channels and group chats: who's here, on the right.
                members = new MemberPanel(s, channel) { Dock = DockStyle.Right, Width = 210, Visible = Settings.GetBool("ShowMembers", true) };
                Controls.Add(members);
                if (channel.GuildId != null)
                {
                    Shown += (o, e) => s.SubscribeMembers(channel);
                    FormClosed += (o, e) => s.UnsubscribeMembers(channel);
                }
            }
            else
            {
                side.Paint += PaintSide;
                side.MouseClick += SideClick;
                side.MouseMove += (o, e) =>
                {
                    bool over = false;
                    foreach (var hit in sideHits) if (hit.Key.Contains(e.Location)) over = true;
                    side.Cursor = over ? Cursors.Hand : Cursors.Default;
                };
                side.Controls.Add(sideCard);
                Controls.Add(side);
            }
            var strip = new StatusStrip { BackColor = Icons.Panel };
            strip.Items.Add(statusLabel);

            Controls.Add(center);
            Controls.Add(header);
            Controls.Add(strip);
            Controls.Add(MainMenuStrip);
            center.BringToFront();

            DragEnter += DragEnterFiles;
            DragDrop += DropFiles;

            if (!s.CanSend(channel))
            {
                input.Enabled = false;
                send.Enabled = false;
                statusLabel.Text = "You can't send messages in this channel.";
            }

            timer.Tick += (o, e) => TickTyping();
            timer.Start();
            Activated += (o, e) =>
            {
                s.MarkRead(channel);
                CatchUp();
            };
            Shown += (o, e) => { web.DocumentText = MessageHtml.Page; if (!showInactive) input.Focus(); };
            FormClosed += (o, e) => timer.Dispose();
            Theme.Attach(this, () => { Script("setTheme", Theme.ChatCss()); header.Invalidate(); side.Invalidate(); });
            UpdateSideCard();
        }

        protected override bool ShowWithoutActivation { get { return showInactive; } }

        void BuildMenu()
        {
            var menu = new MenuStrip { BackColor = Icons.Panel };
            var conv = new ToolStripMenuItem("&Conversation");
            conv.DropDownItems.Add("Send &file...", null, (o, e) => PickFiles());
            conv.DropDownItems.Add("Load &older messages", null, (o, e) => LoadOlder());
            conv.DropDownItems.Add(new ToolStripMenuItem("Search &messages...", null, (o, e) => new SearchForm(s, channel, this).Show()) { ShortcutKeys = Keys.Control | Keys.F });
            if (channel.IsPrivate)
                conv.DropDownItems.Add("Open &saved files", null, (o, e) =>
                {
                    string dir = AutoSaver.FolderFor(s, channel);
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                });
            var pin = new ToolStripMenuItem("&Pin to Favorites");
            pin.Click += (o, e) => Favorites.SetPinned(s, channel.Id, !Favorites.IsPinned(s, channel.Id));
            conv.DropDownItems.Add(pin);
            ToolStripMenuItem follow = null;
            if (!channel.IsDm)
            {
                follow = new ToolStripMenuItem("&Notify me about every message");
                follow.Click += (o, e) => Favorites.SetFollowed(s, channel.Id, !Favorites.IsFollowed(s, channel.Id));
                conv.DropDownItems.Add(follow);
            }
            conv.DropDownOpening += (o, e) =>
            {
                pin.Text = Favorites.IsPinned(s, channel.Id) ? "Un&pin from Favorites" : "&Pin to Favorites";
                if (follow != null) follow.Checked = Favorites.IsFollowed(s, channel.Id);
            };
            conv.DropDownItems.Add(new ToolStripSeparator());
            conv.DropDownItems.Add("&Close", null, (o, e) => Close());
            var view = new ToolStripMenuItem("&View");
            var top = new ToolStripMenuItem("Always on &top") { CheckOnClick = true };
            top.CheckedChanged += (o, e) => TopMost = top.Checked;
            view.DropDownItems.Add(top);
            if (!channel.IsDm)
            {
                var showMembers = new ToolStripMenuItem("Show &members") { CheckOnClick = true, Checked = Settings.GetBool("ShowMembers", true) };
                showMembers.CheckedChanged += (o, e) =>
                {
                    Settings.SetBool("ShowMembers", showMembers.Checked);
                    Settings.Save();
                    if (members != null) members.Visible = showMembers.Checked;
                };
                view.DropDownItems.Add(showMembers);
            }
            menu.Items.Add(conv);
            menu.Items.Add(view);
            MainMenuStrip = menu;
        }

        ToolStripButton Fmt(string label, string wrap, FontStyle style, string tip)
        {
            var b = new ToolStripButton(label) { Font = new Font("Times New Roman", 9.5f, style | FontStyle.Bold), ToolTipText = tip, AutoSize = false, Width = 24 };
            b.Click += (o, e) => Wrap(wrap);
            return b;
        }

        void Wrap(string w)
        {
            int start = input.SelectionStart, len = input.SelectionLength;
            input.SelectedText = w + input.SelectedText + w;
            input.SelectionStart = len == 0 ? start + w.Length : start;
            input.SelectionLength = len;
            input.Focus();
        }

        static readonly string[] Smileys = {
            "\U0001F600", "\U0001F602", "\U0001F923", "\U0001F60A", "\U0001F60D", "\U0001F618", "\U0001F61C", "\U0001F60E",
            "\U0001F914", "\U0001F610", "\U0001F644", "\U0001F62D", "\U0001F621", "\U0001F631", "\U0001F634", "\U0001F973",
            "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64F", "\U0001F525", "❤️", "\U0001F494", "\U0001F480",
            "\U0001F440", "\U0001F389", "\U0001F4AF", "✅", "❌", "\U0001F3AE", "\U0001F37A", "\U0001F355" };

        void BuildEmoji(ToolStripDropDownButton button)
        {
            var drop = new ToolStripDropDown { LayoutStyle = ToolStripLayoutStyle.Table };
            ((TableLayoutSettings)drop.LayoutSettings).ColumnCount = 8;
            foreach (var e in Smileys)
            {
                string value = e;
                var item = new ToolStripButton(e) { Font = new Font("Segoe UI Emoji", 13f), AutoSize = false, Size = new Size(32, 32) };
                item.Click += (o, a) => { drop.Close(); Insert(value); };
                drop.Items.Add(item);
            }
            var g = s.GuildOf(channel);
            if (g != null && g.Emojis.Count > 0)
            {
                // This server's own emoji, shown by name (images load lazily in the list).
                var more = new ToolStripMenuItem(g.Name + " emoji...");
                more.Click += (o, a) =>
                {
                    drop.Close();
                    var menu = new ContextMenuStrip { MaximumSize = new Size(260, 500) };
                    foreach (var em in g.Emojis)
                    {
                        if (em.Bool("animated") || em.Has("available") && !em.Bool("available")) continue;
                        string code = "<:" + em.Str("name") + ":" + em.Str("id") + ">";
                        var it = new ToolStripMenuItem(":" + em.Str("name") + ":", null, (o2, a2) => Insert(code));
                        string url = "https://cdn.discordapp.com/emojis/" + em.Str("id") + ".png?size=32";
                        ImageCache.Get(url, img => it.Image = img);
                        menu.Items.Add(it);
                    }
                    menu.Show(input, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
                };
                drop.Items.Add(more);
            }
            button.DropDown = drop;
            button.ShowDropDownArrow = false;
        }

        void Insert(string text)
        {
            input.SelectedText = text;
            input.Focus();
        }

        // ---- header / side ----

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Icons.FillHeader(g, header.ClientRectangle);
            int x = 10;
            string sub;
            if (channel.IsDm && channel.RecipientIds.Count > 0)
            {
                var p = s.PresenceOf(channel.RecipientIds[0]);
                g.DrawImage(Icons.Status(p.Status, 20), 10, 6, 20, 20);
                x = 36;
                sub = p.Summary ?? Icons.StatusLabel(p.Status);
            }
            else if (channel.IsGroup) sub = channel.RecipientIds.Count + 1 + " people";
            else sub = channel.Topic ?? "";
            using (var f = new Font("Arial", 11f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.Text)) g.DrawString(s.ChannelTitle(channel), f, br, x, 6);
            var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            using (var f = new Font("Tahoma", 8f))
                using (var br = new SolidBrush(Theme.Dim)) g.DrawString(sub, f, br, new RectangleF(10, 27, header.Width - 20, 15), fmt);
        }

        // Where each person was drawn in the side panel, so clicks open their profile.
        readonly List<KeyValuePair<Rectangle, string>> sideHits = new List<KeyValuePair<Rectangle, string>>();

        void PaintSide(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Icons.Panel);
            sideHits.Clear();
            if (channel.IsDm && channel.RecipientIds.Count > 0)
                DrawAvatar(g, s.GetUser(channel.RecipientIds[0]), new Rectangle(14, 8, 128, 128));
            else if (channel.IsGroup)
            {
                int y = 8;
                foreach (var id in channel.RecipientIds)
                {
                    if (y > side.Height - sideCard.Height - 40) break;
                    var u = s.GetUser(id);
                    DrawAvatar(g, u, new Rectangle(8, y, 24, 24));
                    g.DrawImage(Icons.Status(s.PresenceOf(id).Status, 12), new Rectangle(36, y + 6, 12, 12));
                    TextRenderer.DrawText(g, s.NameOf(id), Font, new Rectangle(50, y + 5, side.Width - 54, 15), Theme.Text,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    sideHits.Add(new KeyValuePair<Rectangle, string>(new Rectangle(4, y, side.Width - 8, 26), id));
                    y += 28;
                }
            }
            if (s.Me != null && sideCard.Height == 0)
            {
                int size = 72;
                int y = side.Height - size - 8;
                if (y > 150) DrawAvatar(g, s.Me, new Rectangle((side.Width - size) / 2, y, size, size));
            }
        }

        void DrawAvatar(Graphics g, User u, Rectangle r)
        {
            using (var br = new SolidBrush(Theme.Window)) g.FillRectangle(br, r);
            var img = ImageCache.Scaled(u.AvatarUrl(r.Width > 64 ? 256 : 64), r.Width, side.Invalidate);
            if (img != null) g.DrawImage(img, r);
            using (var pen = new Pen(Icons.PurpleLight)) g.DrawRectangle(pen, r);
            sideHits.Add(new KeyValuePair<Rectangle, string>(r, u.Id));
        }

        void SideClick(object sender, MouseEventArgs e)
        {
            foreach (var hit in sideHits)
                if (hit.Key.Contains(e.Location)) { ProfileForm.Open(s, hit.Value, channel.GuildId); return; }
        }

        void UpdateSideCard()
        {
            if (!channel.IsDm || channel.RecipientIds.Count == 0) return;
            var act = s.PresenceOf(channel.RecipientIds[0]).MainActivity;
            sideCard.Set(s.NameOf(channel.RecipientIds[0]), act);
            sideCard.Height = act != null ? 92 : 0;
            side.Width = act != null ? 260 : 158;
            side.Invalidate();
        }

        public void PresenceChanged(string userId)
        {
            if (channel.RecipientIds.Contains(userId))
            {
                header.Invalidate();
                UpdateSideCard();
                side.Invalidate();
            }
        }

        // ---- history ----

        async void LoadHistory()
        {
            statusLabel.Text = "Loading messages...";
            try
            {
                var list = await s.Api.Get("/channels/" + channel.Id + "/messages?limit=50") as List<object>;
                var msgs = new List<JObj>();
                foreach (var o in list) if (o is JObj) msgs.Add((JObj)o);
                msgs.Reverse();
                foreach (var m in msgs) Append(m);
                if (msgs.Count < 50) noMoreOlder = true;
                foreach (var m in queued) if (!messages.ContainsKey(m.Str("id"))) Append(m);
                queued.Clear();
                SetOlderLink();
                statusLabel.Text = msgs.Count > 0 ? "Last message " + Snowflake.Time(msgs[msgs.Count - 1].Str("id")).ToString("MMM d, h:mm tt") : "Say hi!";
                lastCatchUp = DateTime.UtcNow;
                if (ContainsFocus) s.MarkRead(channel);
            }
            catch (Exception ex) { statusLabel.Text = ex.Message; }
        }

        // Big servers don't always send every message live; when the window comes back into
        // focus, fetch anything newer than what's shown (at most twice a minute).
        async void CatchUp()
        {
            if (!pageReady || newestId == null || channel.IsPrivate || DateTime.UtcNow - lastCatchUp < TimeSpan.FromSeconds(30)) return;
            lastCatchUp = DateTime.UtcNow;
            try
            {
                var list = await s.Api.Get("/channels/" + channel.Id + "/messages?limit=50&after=" + newestId) as List<object>;
                var msgs = new List<JObj>();
                foreach (var o in list) if (o is JObj) msgs.Add((JObj)o);
                msgs.Sort((a, b) => Snowflake.Compare(a.Str("id"), b.Str("id")));
                foreach (var m in msgs) if (!messages.ContainsKey(m.Str("id"))) Append(m);
            }
            catch (Exception) { }
        }

        async void LoadOlder()
        {
            if (loadingOlder || noMoreOlder || oldestId == null) return;
            loadingOlder = true;
            Script("setOlder", "Loading...");
            try
            {
                var list = await s.Api.Get("/channels/" + channel.Id + "/messages?limit=50&before=" + oldestId) as List<object>;
                var msgs = new List<JObj>();
                foreach (var o in list) if (o is JObj) msgs.Add((JObj)o);
                if (msgs.Count < 50) noMoreOlder = true;
                // msgs arrive newest first; build one HTML block in chronological order.
                msgs.Reverse();
                var sb = new System.Text.StringBuilder();
                DateTime day = DateTime.MinValue;
                foreach (var m in msgs)
                {
                    messages[m.Str("id")] = m;
                    AutoSaver.Consider(s, m);
                    var t = Snowflake.Time(m.Str("id")).Date;
                    if (t != day) { sb.Append(MessageHtml.DayDivider(t)); day = t; }
                    sb.Append(MessageHtml.Render(s, m));
                }
                if (msgs.Count > 0) oldestId = msgs[0].Str("id");
                Script("prepend", sb.ToString());
            }
            catch (Exception ex) { statusLabel.Text = ex.Message; }
            loadingOlder = false;
            SetOlderLink();
        }

        void SetOlderLink()
        {
            Script("setOlder", noMoreOlder ? "<span class=\"t\">This is the start of the conversation.</span>" : "<a href=\"app://older/\">Load older messages</a>");
        }

        void Append(JObj m)
        {
            string id = m.Str("id");
            messages[id] = m;
            if (oldestId == null || Snowflake.Compare(id, oldestId) < 0) oldestId = id;
            if (newestId == null || Snowflake.Compare(id, newestId) > 0) newestId = id;
            AutoSaver.Consider(s, m);
            Archive.Consider(s, m);
            var day = Snowflake.Time(id).Date;
            string html = "";
            if (day != lastDay) { html = MessageHtml.DayDivider(day); lastDay = day; }
            Script("append", html + MessageHtml.Render(s, m));
        }

        public void Add(JObj m)
        {
            var author = m.Obj("author");
            if (author != null) typing.Remove(author.Str("id"));
            if (!pageReady) { queued.Add(m); return; }
            if (messages.ContainsKey(m.Str("id"))) return;
            Append(m);
            statusLabel.Text = "Last message " + DateTime.Now.ToString("h:mm tt");
            bool mine = author != null && s.Me != null && author.Str("id") == s.Me.Id;
            if (!mine && m.Str("content") == BuzzText) Shake();
            if (ContainsFocus) s.MarkRead(channel);
        }

        public void Updated(JObj m)
        {
            JObj old;
            if (!messages.TryGetValue(m.Str("id"), out old)) return;
            foreach (var kv in m) old[kv.Key] = kv.Value; // updates can be partial
            Script("replace", "m" + m.Str("id"), MessageHtml.Render(s, old));
        }

        public void ReactionChanged(string t, JObj d)
        {
            JObj m;
            if (s.Me == null || !messages.TryGetValue(d.Str("message_id") ?? "", out m)) return;
            Session.ApplyReaction(m, t, d, s.Me.Id);
            Script("replace", "m" + m.Str("id"), MessageHtml.Render(s, m));
        }

        // arg is "messageId/emojiKey": adds your reaction, or removes it if it's already yours.
        async void ToggleReaction(string arg)
        {
            int slash = arg.IndexOf('/');
            if (slash < 0) return;
            string mid = arg.Substring(0, slash), key = arg.Substring(slash + 1);
            JObj m;
            if (!messages.TryGetValue(mid, out m)) return;
            bool mine = false;
            foreach (var r in m.Objs("reactions"))
                if (MessageHtml.EmojiKey(r.Obj("emoji")) == key) mine = r.Bool("me");
            await React(mid, key, !mine);
        }

        async System.Threading.Tasks.Task React(string mid, string key, bool add)
        {
            string path = "/channels/" + channel.Id + "/messages/" + mid + "/reactions/" + Uri.EscapeDataString(key) + "/@me";
            try
            {
                if (add) await s.Api.Put(path + "?location=Message&type=0", null);
                else await s.Api.Delete(path + "?location=Message&burst=false");
            }
            catch (Exception ex) { statusLabel.Text = ex.Message; }
        }

        // The little smiley picker for "react", at the mouse.
        void PickReaction(string mid)
        {
            var menu = new ContextMenuStrip { LayoutStyle = ToolStripLayoutStyle.Table };
            ((TableLayoutSettings)menu.LayoutSettings).ColumnCount = 8;
            foreach (var e in Smileys)
            {
                string value = e;
                var item = new ToolStripButton(e) { Font = new Font("Segoe UI Emoji", 13f), AutoSize = false, Size = new Size(32, 32) };
                item.Click += async (o, a) => { menu.Close(); await React(mid, value, true); };
                menu.Items.Add(item);
            }
            var g = s.GuildOf(channel);
            if (g != null)
                foreach (var em in g.Emojis)
                {
                    if (em.Has("available") && !em.Bool("available")) continue;
                    string key = em.Str("name") + ":" + em.Str("id");
                    var item = new ToolStripButton { AutoSize = false, Size = new Size(32, 32), ToolTipText = ":" + em.Str("name") + ":", DisplayStyle = ToolStripItemDisplayStyle.Image };
                    ImageCache.Get("https://cdn.discordapp.com/emojis/" + em.Str("id") + ".png?size=32", img => item.Image = img);
                    item.Click += async (o, a) => { menu.Close(); await React(mid, key, true); };
                    menu.Items.Add(item);
                    if (menu.Items.Count >= 64) break;
                }
            menu.Show(Cursor.Position);
        }

        public void Deleted(string id)
        {
            if (messages.Remove(id)) Script("removeMsg", "m" + id);
        }

        void Script(string fn, params object[] args)
        {
            if (!pageReady || web.Document == null) return;
            try { web.Document.InvokeScript(fn, args); }
            catch (Exception ex) { Program.Log("script " + fn + ": " + ex.Message); }
        }

        void WebNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.ToString();
            if (url == "about:blank") return;
            e.Cancel = true;
            if (url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            {
                string rest = url.Substring(6);
                int slash = rest.IndexOf('/');
                string action = slash >= 0 ? rest.Substring(0, slash) : rest;
                string arg = slash >= 0 ? Uri.UnescapeDataString(rest.Substring(slash + 1)) : "";
                switch (action.ToLowerInvariant())
                {
                    case "reply": StartReply(arg); break;
                    case "edit": StartEdit(arg); break;
                    case "delete": DeleteMessage(arg); break;
                    case "older": LoadOlder(); break;
                    case "open": OpenExternal(arg); break;
                    case "profile": ProfileForm.Open(s, arg, channel.GuildId); break;
                    case "react": ToggleReaction(arg); break;
                    case "reactpick": PickReaction(arg); break;
                }
                return;
            }
            OpenExternal(url);
        }

        static void OpenExternal(string url)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Program.Log("open url: " + ex.Message); }
        }

        // ---- composing ----

        void StartReply(string id)
        {
            JObj m;
            if (!messages.TryGetValue(id, out m)) return;
            editing = null;
            replyTo = id;
            var a = m.Obj("author");
            ShowBanner("Replying to " + (a != null ? s.NameOf(a.Str("id")) : "?") + "  (click to cancel)");
            input.Focus();
        }

        void StartEdit(string id)
        {
            JObj m;
            if (!messages.TryGetValue(id, out m)) return;
            replyTo = null;
            editing = id;
            input.Text = m.Str("content") ?? "";
            input.SelectionStart = input.TextLength;
            ShowBanner("Editing your message  (click to cancel)");
            input.Focus();
        }

        async void DeleteMessage(string id)
        {
            if (MessageBox.Show(this, "Delete this message?", "Buzzcord", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { await s.Api.Delete("/channels/" + channel.Id + "/messages/" + id); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
        }

        void ShowBanner(string text)
        {
            banner.Text = text;
            banner.Height = 20;
        }

        void ClearMode()
        {
            if (editing != null) input.Text = "";
            replyTo = null;
            editing = null;
            pendingFiles.Clear();
            banner.Height = 0;
        }

        void UpdateFilesBanner()
        {
            if (pendingFiles.Count == 0) { if (replyTo == null && editing == null) banner.Height = 0; return; }
            var names = new List<string>();
            foreach (var f in pendingFiles) names.Add(Path.GetFileName(f));
            ShowBanner("Attached: " + string.Join(", ", names) + "  - press Send (click here to remove)");
        }

        void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                DoSend();
            }
            else if (e.KeyCode == Keys.Enter && e.Shift)
            {
                e.SuppressKeyPress = true;
                input.SelectedText = Environment.NewLine;
            }
            else if (e.KeyCode == Keys.Escape && (replyTo != null || editing != null || pendingFiles.Count > 0))
            {
                e.SuppressKeyPress = true;
                ClearMode();
            }
            else if (e.KeyCode == Keys.Up && input.TextLength == 0)
            {
                // Up arrow edits your last message, like Discord.
                string last = null;
                foreach (var kv in messages)
                {
                    var a = kv.Value.Obj("author");
                    if (a != null && s.Me != null && a.Str("id") == s.Me.Id && (last == null || Snowflake.Compare(kv.Key, last) > 0)) last = kv.Key;
                }
                if (last != null) { e.SuppressKeyPress = true; StartEdit(last); }
            }
            else if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage())
            {
                e.SuppressKeyPress = true;
                PasteImage();
            }
            else if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsFileDropList())
            {
                e.SuppressKeyPress = true;
                foreach (var f in Clipboard.GetFileDropList()) if (File.Exists(f)) pendingFiles.Add(f);
                UpdateFilesBanner();
            }
        }

        void PasteImage()
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                string dir = Path.Combine(Path.GetTempPath(), "Buzzcord");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "image-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                pendingFiles.Add(path);
                UpdateFilesBanner();
            }
            catch (Exception ex) { statusLabel.Text = "Couldn't paste: " + ex.Message; }
        }

        void PickFiles()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Send files", Filter = "All files|*.*|Pictures and videos|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.mp4;*.mov;*.webm" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                pendingFiles.AddRange(dlg.FileNames);
                UpdateFilesBanner();
                input.Focus();
            }
        }

        void DragEnterFiles(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void DropFiles(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            foreach (var f in files) if (File.Exists(f)) pendingFiles.Add(f);
            UpdateFilesBanner();
        }

        void SendTyping()
        {
            if (input.TextLength == 0 || editing != null || DateTime.UtcNow - lastTypingSent < TimeSpan.FromSeconds(8)) return;
            lastTypingSent = DateTime.UtcNow;
            s.Api.Post("/channels/" + channel.Id + "/typing", null).ContinueWith(t => { var ignored = t.Exception; });
        }

        void SendText(string text)
        {
            input.Text = text;
            DoSend();
        }

        async void DoSend()
        {
            string text = input.Text.TrimEnd();
            if (text.Length == 0 && pendingFiles.Count == 0) return;
            if (!send.Enabled) return;
            send.Enabled = false;
            input.ReadOnly = true;
            try
            {
                if (editing != null)
                {
                    await s.Api.Patch("/channels/" + channel.Id + "/messages/" + editing, Json.O("content", text));
                }
                else
                {
                    var uploaded = new List<JObj>();
                    for (int i = 0; i < pendingFiles.Count; i++)
                    {
                        string file = pendingFiles[i];
                        int n = i + 1, total = pendingFiles.Count;
                        string name = Path.GetFileName(file);
                        var ui = System.Threading.SynchronizationContext.Current;
                        int lastPct = -1;
                        uploaded.Add(await s.Api.UploadFile(channel.Id, file, i, p =>
                        {
                            int pct = (int)(p * 100);
                            if (pct == lastPct) return;
                            lastPct = pct;
                            ui.Post(_ => statusLabel.Text = "Uploading " + name + " (" + n + "/" + total + ") " + pct + "%", null);
                        }));
                    }
                    await s.Api.SendMessage(channel.Id, text, replyTo, uploaded);
                    statusLabel.Text = "Sent " + DateTime.Now.ToString("h:mm tt");
                }
                input.Text = "";
                ClearMode();
                lastTypingSent = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                string msg = ex is ApiException && ((ApiException)ex).Status == 413
                    ? "That file is too big for Discord (10 MB without Nitro)."
                    : ex.Message;
                statusLabel.Text = msg;
                MessageBox.Show(this, msg, "Couldn't send", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                send.Enabled = s.CanSend(channel);
                input.ReadOnly = false;
                input.Focus();
            }
        }

        // ---- typing indicator ----

        public void SomeoneTyping(string userId)
        {
            typing[userId] = DateTime.UtcNow;
            TickTyping();
        }

        void TickTyping()
        {
            foreach (var id in new List<string>(typing.Keys))
                if (DateTime.UtcNow - typing[id] > TimeSpan.FromSeconds(10)) typing.Remove(id);
            if (typing.Count == 0)
            {
                if (statusLabel.Text.EndsWith("typing...")) statusLabel.Text = "";
                return;
            }
            var names = new List<string>();
            foreach (var id in typing.Keys) names.Add(s.NameOf(id));
            statusLabel.Text = names.Count > 3 ? "Several people are typing..." : string.Join(", ", names) + (names.Count == 1 ? " is typing..." : " are typing...");
        }

        // ---- buzz ----

        void Shake()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            var origin = Location;
            var t = new Timer { Interval = 25 };
            int step = 0;
            t.Tick += (o, e) =>
            {
                step++;
                if (step > 16 || IsDisposed) { t.Dispose(); if (!IsDisposed) Location = origin; return; }
                Location = new Point(origin.X + (step % 2 == 0 ? 8 : -8), origin.Y + (step % 4 < 2 ? 4 : -4));
            };
            t.Start();
            Sounds.Ding();
        }
    }
}
