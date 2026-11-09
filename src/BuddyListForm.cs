using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Buzzcord
{
    // One messenger window per account: your status up top, friends, chats and servers below,
    // and the selected friend's rich presence at the bottom.
    public class BuddyListForm : Form
    {
        enum Kind { Header, Friend, Dm, Group, Guild, Category, Channel, Empty, Pending, AddAction, VoiceChannel, VoiceUser }

        class Row
        {
            public Kind Kind;
            public string Key, Id, Text;
            public int Indent, Height;
            public Channel Channel;
            public Guild Guild;
            public bool ShowGuild;   // search results show which server a channel is in
        }

        readonly TextBox search = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
        string filter = "";

        public readonly Session Session;
        readonly AppContext app;
        readonly RowList list = new RowList { Dock = DockStyle.Fill };
        readonly BufferedPanel header = new BufferedPanel { Dock = DockStyle.Top, Height = 78 };
        readonly HashSet<string> shownUsers = new HashSet<string>();
        static readonly Font fName = new Font("Tahoma", 8.25f), fNameBold = new Font("Tahoma", 8.25f, FontStyle.Bold),
            fSmall = new Font("Tahoma", 7.5f), fCategory = new Font("Tahoma", 6.75f, FontStyle.Bold),
            fLink = new Font("Tahoma", 7.5f, FontStyle.Underline);
        const TextFormatFlags TextFlags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
            | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding;
        const TextFormatFlags MeasureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        readonly ActivityCard card = new ActivityCard { Dock = DockStyle.Bottom, Height = 0 };
        readonly VoiceBar voiceBar;
        readonly ToolStripStatusLabel state = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly Timer rebuildTimer = new Timer { Interval = 400 };
        readonly Timer tick = new Timer { Interval = 1000 };
        readonly HashSet<string> collapsed = new HashSet<string>();
        readonly Dictionary<string, ChatForm> chats = new Dictionary<string, ChatForm>();
        readonly ContextMenuStrip statusMenu = new ContextMenuStrip();
        Rectangle statusLinkRect, customLinkRect, gameLinkRect;
        readonly Timer recheck = new Timer { Interval = 16000 };
        bool dirty;
        public bool Exiting;
        ToolStripButton mentionsButton;
        AlertsForm alerts;

        void ShowAlerts()
        {
            if (alerts == null || alerts.IsDisposed)
            {
                alerts = new AlertsForm(Session, this);
                alerts.Show();
            }
            else
            {
                if (alerts.WindowState == FormWindowState.Minimized) alerts.WindowState = FormWindowState.Normal;
                alerts.Activate();
            }
        }

        void UpdateMentionsButton()
        {
            if (mentionsButton == null) return;
            int n = Notifier.Unseen(Session);
            mentionsButton.Text = n > 0 ? "Mentions (" + n + ")" : "Mentions";
            mentionsButton.Font = new Font(mentionsButton.Font, n > 0 ? FontStyle.Bold : FontStyle.Regular);
        }

        public BuddyListForm(Session session, AppContext app)
        {
            Session = session;
            this.app = app;
            Text = "Buzzcord";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            MinimumSize = new Size(230, 360);
            StartPosition = FormStartPosition.Manual;
            LoadBounds();

            foreach (var k in (Settings.Get("Collapsed." + session.TokenKey, "Offline|guilds")).Split('|')) if (k.Length > 0) collapsed.Add(k);

            BuildMenu();
            header.Paint += PaintHeader;
            header.MouseClick += HeaderClick;
            header.MouseMove += (s, e) => header.Cursor = statusLinkRect.Contains(e.Location) || customLinkRect.Contains(e.Location) || gameLinkRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            recheck.Tick += (s, e) => { recheck.Stop(); header.Invalidate(); };
            Disposed += (s, e) => recheck.Dispose();

            list.RowHeight = r => ((Row)r).Height;
            list.RowKey = r => ((Row)r).Key;
            list.DrawRow += (g, r, b, sel, hov) => DrawRow(g, (Row)r, b, sel, hov);
            list.RowClick += (r, e) =>
            {
                var row = (Row)r;
                if (e.Button == MouseButtons.Right) { ShowRowMenu(row, e.Location); return; }
                if (row.Kind == Kind.Header || row.Kind == Kind.Guild) Toggle(row);
            };
            // Accept / Ignore / Cancel and "Add a Friend" are hit-tested before the row is selected:
            // selecting can scroll the list or resize the activity card, which used to move the links
            // away from the click so nothing happened. The action runs after the mouse-down is done,
            // and the second click of a double-click is ignored (no second confirmation).
            list.RowAction = (r, e) =>
            {
                var row = (Row)r;
                string action = ActionAt(row, list.BoundsOf(row), e.Location);
                if (action == null) return false;
                if (e.Clicks == 1)
                    BeginInvoke((Action)(() =>
                    {
                        if (action == "add") AddFriend(filter);
                        else PendingAction(row.Id, action);
                    }));
                return true;
            };
            list.MouseMove += (s, e) =>
            {
                var row = list.RowAt(e.Location) as Row;
                list.Cursor = row != null && ActionAt(row, list.BoundsOf(row), e.Location) != null ? Cursors.Hand : Cursors.Default;
            };
            list.RowActivate += r => Activate((Row)r);
            list.SelectionChanged += UpdateCard;

            var strip = new StatusStrip { SizingGrip = true, BackColor = Icons.Panel };
            strip.Items.Add(state);

            // Search box over the list; filters friends, chats and channels as you type.
            search.Font = new Font("Tahoma", 9f);
            search.TextChanged += (s, e) => { filter = search.Text.Trim(); Rebuild(); };
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { search.Text = ""; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter)
                {
                    // Down moves into the results; Enter opens the first one.
                    e.SuppressKeyPress = true;
                    var first = list.SelectFirst(r => ((Row)r).Kind != Kind.Header && ((Row)r).Kind != Kind.Category);
                    if (e.KeyCode == Keys.Enter && first != null) Activate((Row)first);
                    else list.Focus();
                }
            };
            search.HandleCreated += (s, e) => Native.SetCueBanner(search, "Search friends, chats, servers...");
            var searchGap = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Icons.Panel };

            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 0), BackColor = Icons.Panel };
            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(list);
            listHost.Controls.Add(border);
            listHost.Controls.Add(searchGap);
            listHost.Controls.Add(search);

            // Bottom toolbar, like the old messenger's.
            var bar = new ToolStrip { Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden, BackColor = Icons.Panel, RenderMode = ToolStripRenderMode.System, Padding = new Padding(4, 2, 4, 2) };
            bar.Items.Add(new ToolStripButton("Add a Friend", AddIcon(), (s, e) => AddFriend(null)) { ToolTipText = "Send a friend request by username" });
            bar.Items.Add(new ToolStripButton("Send IM", null, (s, e) => Activate(list.SelectedRow as Row)) { ToolTipText = "Message the selected friend or chat" });
            mentionsButton = new ToolStripButton("Mentions", null, (s, e) => ShowAlerts()) { ToolTipText = "Your mentions and missed notifications (Ctrl+M)" };
            bar.Items.Add(mentionsButton);

            voiceBar = new VoiceBar(session);
            Controls.Add(listHost);
            Controls.Add(card);
            Controls.Add(voiceBar);
            Controls.Add(bar);
            Controls.Add(header);
            Controls.Add(strip);
            Controls.Add(MainMenuStrip);

            rebuildTimer.Tick += (s, e) => { rebuildTimer.Stop(); if (dirty) Rebuild(); };
            tick.Tick += (s, e) => { if (card.Height > 0) card.Invalidate(); };
            tick.Start();

            Session.Ready += () => { Text = "Buzzcord - " + Session.Me.DisplayName; Rebuild(); header.Invalidate(); };
            Session.ListChanged += MarkDirty;
            Session.OpenChatHandler = c => OpenChat(c, true);
            Session.PresenceChanged += id =>
            {
                // Server members come and go constantly; only people in this list matter here.
                if (shownUsers.Contains(id) || Session.Relationships.ContainsKey(id)) MarkDirty();
                if (Session.Me != null && id == Session.Me.Id) header.Invalidate();
                foreach (var c in chats.Values) c.PresenceChanged(id);
                UpdateCard();
            };
            Session.FriendRequest += id =>
            {
                collapsed.Remove("Requests");
                Notifier.Alert(this, AlertKind.FriendRequest, "Friend request", Session.GetUser(id).DisplayName + " wants to be your friend.", null, id, true);
            };
            Session.FriendAccepted += id => Notifier.Alert(this, AlertKind.FriendAccepted, "Friend request accepted", Session.GetUser(id).DisplayName + " is now your friend.", null, id, true);
            Session.FriendSignedIn += id =>
            {
                if (Notifier.SignInAlerts) Notifier.Alert(this, AlertKind.SignIn, Session.NameOf(id) + " is online", Session.NameOf(id) + " just signed in.", null, id, true);
            };
            Session.Unfriended += id =>
            {
                var u = Session.GetUser(id);
                string name = u.DisplayName + (u.Username != null && u.Username != u.DisplayName ? " (" + u.Username + ")" : "");
                Notifier.Alert(this, AlertKind.Unfriended, name + " is no longer your friend", "They removed you as a friend, or blocked you.", null, id, true);
            };            Session.ReactionChanged += (t, d) => { ChatForm c; if (chats.TryGetValue(d.Str("channel_id") ?? "", out c)) c.ReactionChanged(t, d); };
            Favorites.Changed += s => { if (s == Session) MarkDirty(); };
            Action reorder = () => { if (!IsDisposed) Rebuild(); };
            GroupOrder.Changed += reorder;
            FormClosed += (s, e) => GroupOrder.Changed -= reorder;
            Notifier.HistoryChanged += s => { if (s == Session) UpdateMentionsButton(); };
            Session.StateChanged += st => state.Text = st;
            Session.MessageCreated += OnMessage;
            Session.MessageUpdated += m => { ChatForm c; if (chats.TryGetValue(m.Str("channel_id"), out c)) c.Updated(m); };
            Session.MessageDeleted += (cid, mid) => { ChatForm c; if (chats.TryGetValue(cid, out c)) c.Deleted(mid); };
            Session.Typing += (cid, uid) => { ChatForm c; if (chats.TryGetValue(cid, out c)) c.SomeoneTyping(uid); };
            Session.SignedOut += () =>
            {
                MessageBox.Show(this, "Discord signed this account out (the token stopped working). Sign in again with Messenger > Add account.", "Buzzcord");
                app.RemoveAccount(this, "Discord rejected the token");
            };
            PresenceManager.Instance.Changed += () => header.Invalidate();
            Session.VoiceStateChanged += cid => MarkDirty();
            Action voiceChanged = () => { if (!IsDisposed) { MarkDirty(); list.Invalidate(); } };
            VoiceManager.Changed += voiceChanged;
            FormClosed += (s, e) => VoiceManager.Changed -= voiceChanged;
            Session.IncomingCall += OnIncomingCall;
            state.Text = Session.ConnectionState;
            Theme.Attach(this, () => { list.Invalidate(); header.Invalidate(); card.Invalidate(); voiceBar.Invalidate(); });
            Rebuild();
        }

        void LoadBounds()
        {
            var key = "Window." + Session.TokenKey;
            var parts = Settings.Get(key, "").Split(',');
            int x, y, w, h;
            if (parts.Length == 4 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) && int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h))
            {
                var r = new Rectangle(x, y, w, h);
                foreach (var scr in Screen.AllScreens)
                    if (scr.WorkingArea.IntersectsWith(r)) { Bounds = r; return; }
            }
            var wa = Screen.PrimaryScreen.WorkingArea;
            int offset = app.WindowCount * 270;
            Bounds = new Rectangle(wa.Right - 290 - offset, wa.Top + 40, 270, Math.Min(640, wa.Height - 80));
        }

        void SaveBounds()
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : base.RestoreBounds;
            Settings.Set("Window." + Session.TokenKey, r.X + "," + r.Y + "," + r.Width + "," + r.Height);
            Settings.Set("Collapsed." + Session.TokenKey, string.Join("|", collapsed));
            Settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveBounds();
            if (!Exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // Like the old messengers: closing the list keeps you signed in, in the tray.
                e.Cancel = true;
                Hide();
                app.HintTray();
                return;
            }
            foreach (var c in new List<ChatForm>(chats.Values)) c.Close();
            if (VoiceManager.Current != null && VoiceManager.Current.Session == Session) VoiceManager.Current.Leave();
            base.OnFormClosing(e);
        }

        // ---- calls ----

        void OnIncomingCall(string channelId)
        {
            if (VoiceManager.IsIn(Session, channelId)) return;
            Channel c;
            if (!Session.PrivateChannels.TryGetValue(channelId, out c)) return;
            // Busy: only the history. Quiet while gaming: a tray balloon. Otherwise the ringing window.
            var d = Notifier.DecideFor(Session);
            if (d.Sound) IncomingCallForm.Open(this, Session, channelId, true);
            else Notifier.Alert(this, AlertKind.Call, "Incoming call", Session.ChannelTitle(c) + " is calling you. Open the chat to answer.", channelId, c.IsDm && c.RecipientIds.Count > 0 ? c.RecipientIds[0] : null, d.Balloon);
        }

        async void JoinVoice(Channel c)
        {
            try { await VoiceManager.Join(Session, c); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
        }

        // ---- menu ----

        void BuildMenu()
        {
            var menu = new MenuStrip { BackColor = Icons.Panel };
            var messenger = new ToolStripMenuItem("&Messenger");
            messenger.DropDownItems.Add(new ToolStripMenuItem("Add a &Friend...", AddIcon(), (s, e) => AddFriend(null)) { ShortcutKeys = Keys.Control | Keys.N });
            messenger.DropDownItems.Add(new ToolStripMenuItem("&Search", null, (s, e) => { search.Focus(); search.SelectAll(); }) { ShortcutKeys = Keys.Control | Keys.F });
            messenger.DropDownItems.Add(new ToolStripMenuItem("&Mentions && notifications", null, (s, e) => ShowAlerts()) { ShortcutKeys = Keys.Control | Keys.M });
            messenger.DropDownItems.Add(new ToolStripMenuItem("&Unread messages", null, (s, e) => UnreadForm.Open(app)) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.U });
            messenger.DropDownItems.Add(new ToolStripSeparator());
            messenger.DropDownItems.Add("&Add account...", null, (s, e) => app.AddAccount());
            messenger.DropDownItems.Add("Open &saved files", null, (s, e) => OpenFolder(System.IO.Path.Combine(Settings.SaveFolder, AutoSaver.Clean(Session.Me != null ? Session.Me.Username : ""))));
            messenger.DropDownItems.Add(new ToolStripSeparator());
            messenger.DropDownItems.Add("Sign &out this account", null, (s, e) =>
            {
                if (MessageBox.Show(this, "Sign out " + (Session.Me != null ? Session.Me.DisplayName : "this account") + " and forget it on this PC?", "Buzzcord", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    app.RemoveAccount(this, "signed out from the menu");
            });
            messenger.DropDownItems.Add("E&xit Buzzcord", null, (s, e) => app.ExitAll());

            var status = new ToolStripMenuItem("&Status");
            foreach (var st in new[] { "online", "idle", "dnd", "invisible" })
            {
                string value = st;
                var item = new ToolStripMenuItem(Icons.StatusLabel(st), Icons.Status(st, 16), (s, e) => Session.SetStatus(value));
                status.DropDownItems.Add(item);
                statusMenu.Items.Add(new ToolStripMenuItem(Icons.StatusLabel(st), Icons.Status(st, 16), (s, e) => Session.SetStatus(value)));
            }
            status.DropDownItems.Add(new ToolStripSeparator());
            status.DropDownItems.Add("Set &custom status...", null, (s, e) => EditCustomStatus());
            var share = new ToolStripMenuItem("Share my &game activity") { CheckOnClick = true };
            share.CheckedChanged += (s, e) => { if (share.Checked != PresenceManager.Instance.Shares(Session)) PresenceManager.Instance.SetShares(Session, share.Checked); };
            status.DropDownItems.Add(share);
            status.DropDownItems.Add("My &games on my profile...", null, (s, e) => { if (Session.Me != null) ProfileForm.Open(Session, Session.Me.Id, null); });
            status.DropDownOpening += (s, e) =>
            {
                share.Checked = PresenceManager.Instance.Shares(Session);
                for (int i = 0; i < 4; i++)
                    ((ToolStripMenuItem)status.DropDownItems[i]).Checked = new[] { "online", "idle", "dnd", "invisible" }[i] == Session.Status;
            };

            var options = new ToolStripMenuItem("&Options");
            var theme = new ToolStripMenuItem("&Theme");
            foreach (var mode in new[] { "system", "light", "dark" })
            {
                string value = mode;
                theme.DropDownItems.Add(new ToolStripMenuItem(mode == "system" ? "Match Windows" : mode == "light" ? "Light (classic)" : "Dark", null, (s, e) => Theme.SetMode(value)));
            }
            theme.DropDownOpening += (s, e) =>
            {
                for (int i = 0; i < 3; i++) ((ToolStripMenuItem)theme.DropDownItems[i]).Checked = new[] { "system", "light", "dark" }[i] == Theme.Mode;
            };
            options.DropDownItems.Add(theme);
            options.DropDownItems.Add("&Arrange groups...", null, (s, e) => ArrangeGroupsForm.Open(this));
            options.DropDownItems.Add("&Voice settings...", null, (s, e) => { using (var f = new VoiceSettingsForm()) f.ShowDialog(this); });
            options.DropDownItems.Add(AutoAwayMenu());
            options.DropDownItems.Add("Buddy list &shortcut...", null, (s, e) => { using (var f = new HotkeyForm()) f.ShowDialog(this); });
            options.DropDownItems.Add(new ToolStripSeparator());
            options.DropDownItems.Add(Toggle("Pop up a window for new messages", "PopupNew", true));
            options.DropDownItems.Add(Toggle("Quiet while gaming (no pop-ups or sounds)", "QuietWhileGaming", true));
            options.DropDownItems.Add(Toggle("Tell me when friends sign in", "SignInAlerts", true));
            options.DropDownItems.Add(Toggle("Play sounds", "Sounds", true));
            options.DropDownItems.Add(new ToolStripSeparator());
            options.DropDownItems.Add(Toggle("Auto-save files from DMs", "AutoSaveDMs", true));
            options.DropDownItems.Add(Toggle("Auto-save files from group chats", "AutoSaveGroups", true));
            options.DropDownItems.Add(Toggle("Auto-save full-size profile pictures", "AutoSaveAvatars", true));
            options.DropDownItems.Add(Toggle("Archive DM and group chat text", "ArchiveChats", true));
            options.DropDownItems.Add("Change save folder...", null, (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = Settings.SaveFolder, Description = "Where should Buzzcord save files from DMs?" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) { Settings.Set("SaveFolder", dlg.SelectedPath); Settings.Save(); }
            });
            options.DropDownItems.Add(new ToolStripSeparator());
            options.DropDownItems.Add(Toggle("Detect running games", "DetectGames", true, on => { PresenceManager.Instance.Detector.Enabled = on; PresenceManager.Instance.Detector.Scan(); }));
            options.DropDownItems.Add(Toggle("Start with Windows", null, AppContext.StartsWithWindows, on => AppContext.SetStartWithWindows(on)));
            options.DropDownItems.Add(Toggle("Update Buzzcord automatically", "AutoUpdate", true));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("Rich presence status...", null, (s, e) => MessageBox.Show(this, app.RpcStatus(), "Buzzcord"));
            help.DropDownItems.Add("Open log folder", null, (s, e) => OpenFolder(Settings.Folder));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add("Check for updates...", null, (s, e) => app.CheckForUpdates(true));
            help.DropDownItems.Add("Donate...", null, (s, e) => DonateForm.Open(this));
            help.DropDownItems.Add("About Buzzcord", null, (s, e) => AboutForm.Open(this));

            menu.Items.AddRange(new ToolStripItem[] { messenger, status, options, help });
            MainMenuStrip = menu;
        }

        // Options > Auto-Away: Off, 5, 10, 15 or 30 idle minutes, and whether locking Windows counts.
        static ToolStripMenuItem AutoAwayMenu()
        {
            var menu = new ToolStripMenuItem("Auto-&Away");
            foreach (int m in AutoAway.Choices)
            {
                int value = m;
                menu.DropDownItems.Add(new ToolStripMenuItem(m == 0 ? "Off" : "After " + m + " minutes idle", null, (s, e) => AutoAway.Minutes = value));
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            var locked = new ToolStripMenuItem("Away when Windows is locked");
            locked.Click += (s, e) => AutoAway.WhenLocked = !AutoAway.WhenLocked;
            menu.DropDownItems.Add(locked);
            menu.DropDownOpening += (s, e) =>
            {
                for (int i = 0; i < AutoAway.Choices.Length; i++)
                    ((ToolStripMenuItem)menu.DropDownItems[i]).Checked = AutoAway.Choices[i] == AutoAway.Minutes;
                locked.Checked = AutoAway.WhenLocked;
            };
            return menu;
        }

        ToolStripMenuItem Toggle(string text, string key, bool def, Action<bool> changed = null)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = key != null ? Settings.GetBool(key, def) : def };
            item.CheckedChanged += (s, e) =>
            {
                if (key != null) { Settings.SetBool(key, item.Checked); Settings.Save(); }
                if (changed != null) changed(item.Checked);
            };
            return item;
        }

        static void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("open folder: " + ex.Message); }
        }

        void EditCustomStatus()
        {
            string text = Prompt.Show(this, "Custom status", "What's on your mind? (leave empty to clear)", Session.CustomStatusText ?? "");
            if (text == null) return;
            Session.CustomStatusText = text.Trim().Length == 0 ? null : text.Trim();
            Session.PushPresence();
            Session.Api.Patch("/users/@me/settings", Json.O("custom_status", Session.CustomStatusText == null ? null : Json.O("text", Session.CustomStatusText)))
                .ContinueWith(t => { var ignored = t.Exception; });
            header.Invalidate();
        }

        // ---- header ----

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Icons.FillHeader(g, header.ClientRectangle);
            var me = Session.Me;
            var avatarRect = new Rectangle(8, 9, 58, 58);
            using (var br = new SolidBrush(Theme.Window)) g.FillRectangle(br, avatarRect);
            if (me != null)
            {
                var img = ImageCache.Scaled(me.AvatarUrl(128), avatarRect.Width, header.Invalidate);
                if (img != null) Blit(g, img, avatarRect.X, avatarRect.Y);
            }
            using (var pen = new Pen(Color.FromArgb(0xB0, 0x90, 0x20))) g.DrawRectangle(pen, avatarRect);

            int x = 74;
            using (var bold = new Font("Arial", 10f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.Text)) g.DrawString(me != null ? me.DisplayName : "Signing in...", bold, br, x, 7);

            // Auto-Away shows here without changing the status you picked.
            bool away = Session.AutoAwayOn && Session.Status == "online";
            g.DrawImage(Icons.Status(Session.ShownStatus, 14), x, 27, 14, 14);
            string label = (away ? "Away (idle)" : Icons.StatusLabel(Session.Status)) + " ▾";
            using (var link = new Font("Tahoma", 8.25f, FontStyle.Underline))
            {
                var sz = g.MeasureString(label, link);
                using (var br = new SolidBrush(Theme.Link)) g.DrawString(label, link, br, x + 16, 27);
                statusLinkRect = new Rectangle(x, 25, (int)sz.Width + 18, 18);
            }

            string custom = string.IsNullOrEmpty(Session.CustomStatusText) ? "[set a status message]" : Session.CustomStatusText;
            using (var f = new Font("Tahoma", 8f, FontStyle.Italic))
            {
                var rect = new RectangleF(x, 43, header.Width - x - 6, 15);
                using (var br = new SolidBrush(string.IsNullOrEmpty(Session.CustomStatusText) ? Theme.SubText : Theme.Dim)) g.DrawString(custom, f, br, rect, Ellipsis());
                customLinkRect = Rectangle.Round(rect);
            }

            var acts = PresenceManager.Instance.ActivitiesFor(Session);
            string sharing = !PresenceManager.Instance.Shares(Session) ? "Game activity: hidden"
                : acts.Count > 0 ? Presence.Describe(acts[0]) : null;
            bool hiddenByDiscord = acts.Count > 0 && (Session.DiscordSharesGames == false || Session.FriendsMissGame);
            if (hiddenByDiscord) sharing += " (friends can't see it)";
            gameLinkRect = Rectangle.Empty;
            if (sharing != null)
                using (var f = new Font("Tahoma", 8f, hiddenByDiscord ? FontStyle.Underline : FontStyle.Regular))
                using (var br = new SolidBrush(hiddenByDiscord ? Color.FromArgb(0xD0, 0x30, 0x30) : Icons.Purple))
                {
                    var rect = new RectangleF(x, 58, header.Width - x - 6, 15);
                    g.DrawString(sharing, f, br, rect, Ellipsis());
                    if (acts.Count > 0) gameLinkRect = Rectangle.Round(rect);
                }
            // FriendsMissGame only turns true 15 s after a push; look again then.
            if (acts.Count > 0 && !hiddenByDiscord && Session.Broadcast != null && !recheck.Enabled) recheck.Start();
        }

        void ExplainHiddenGame()
        {
            string text = Session.DiscordSharesGames == false
                ? "Discord's own \"Share your detected activities with others\" setting is off for this account, so Discord removes your game before friends see it.\n\nTurn it on now?"
                : "Buzzcord sent your game to Discord, but Discord isn't showing it to others.\n\n"
                  + "The usual cause is the account's Activity Privacy setting (\"Share your detected activities with others\"). "
                  + "Another Discord app signed in to this account (phone or the official app) can also take over what friends see.\n\n"
                  + "Turn the sharing setting on now?";
            if (MessageBox.Show(this, text, "Friends can't see your game", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            Session.ShareGamesOnDiscord().ContinueWith(t =>
            {
                if (t.Exception == null) return;
                Program.Log("show_current_game: " + t.Exception.GetBaseException().Message);
                BeginInvoke((Action)(() => MessageBox.Show(this,
                    "Discord didn't accept the change. Turn it on in the Discord app or website instead: User Settings > Activity Privacy > \"Share your detected activities with others\".",
                    "Buzzcord")));
            });
        }

        static StringFormat Ellipsis()
        {
            return new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        }

        void HeaderClick(object sender, MouseEventArgs e)
        {
            if (new Rectangle(8, 9, 58, 58).Contains(e.Location) && Session.Me != null) ProfileForm.Open(Session, Session.Me.Id, null);
            else if (statusLinkRect.Contains(e.Location)) statusMenu.Show(header, new Point(statusLinkRect.Left, statusLinkRect.Bottom));
            else if (customLinkRect.Contains(e.Location)) EditCustomStatus();
            else if (gameLinkRect.Contains(e.Location))
            {
                bool hidden = Session.DiscordSharesGames == false || Session.FriendsMissGame;
                if (hidden && e.Button == MouseButtons.Left) ExplainHiddenGame();
                else CurrentGameMenu().Show(header, new Point(gameLinkRect.Left, gameLinkRect.Bottom));
            }
        }

        // What you're playing now: put it on your profile's Board.
        ContextMenuStrip CurrentGameMenu()
        {
            var menu = new ContextMenuStrip();
            foreach (var a in PresenceManager.Instance.ActivitiesFor(Session))
            {
                string app = a.Str("application_id");
                if (app == null || a.Long("type") != 0) continue;
                menu.Items.Add(GameBoard.AddMenu(this, Session, app, a.Str("name"), t => state.Text = t));
            }
            if (Session.DiscordSharesGames == false || Session.FriendsMissGame)
                menu.Items.Add("Why can't friends see it?", null, (s, e) => ExplainHiddenGame());
            if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("This game has no Discord page") { Enabled = false });
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            return menu;
        }

        // ---- list ----

        void MarkDirty()
        {
            dirty = true;
            if (!rebuildTimer.Enabled) rebuildTimer.Start();
        }

        void Rebuild()
        {
            dirty = false;
            var rows = new List<Row>();
            var s = Session;
            if (!s.IsReady)
            {
                rows.Add(new Row { Kind = Kind.Empty, Text = "Signing in...", Height = 30 });
                Fill(rows);
                return;
            }

            if (filter.Length > 0)
            {
                BuildSearch(rows);
                Fill(rows);
                return;
            }

            // Each group builds its own rows; they're added in the order chosen in Options >
            // Arrange groups (GroupOrder).
            var sections = new Dictionary<string, Action>();

            // Friend requests: theirs first, then the ones you sent.
            var incoming = new List<string>(s.RelationshipsOfType(3));
            var outgoing = new List<string>(s.RelationshipsOfType(4));
            sections["Requests"] = () =>
            {
                if (incoming.Count + outgoing.Count == 0) return;
                AddGroup(rows, "Requests", "Friend requests" + (incoming.Count > 0 ? " (" + incoming.Count + " new)" : " (" + outgoing.Count + " sent)"), () =>
                {
                    foreach (var id in incoming) rows.Add(new Row { Kind = Kind.Pending, Id = id, Key = "p" + id, Height = 36, Text = "in" });
                    foreach (var id in outgoing) rows.Add(new Row { Kind = Kind.Pending, Id = id, Key = "p" + id, Height = 36, Text = "out" });
                });
            };

            // Favorites: channels and chats you pinned, in the order you pinned them.
            var pinned = new List<Channel>();
            foreach (var id in Favorites.Pinned(s)) { var c = s.FindChannel(id); if (c != null) pinned.Add(c); }
            sections["Favorites"] = () =>
            {
                if (pinned.Count == 0) return;
                AddGroup(rows, "Favorites", "Favorites (" + pinned.Count + ")", () =>
                {
                    foreach (var c in pinned)
                    {
                        if (c.IsDm) rows.Add(new Row { Kind = Kind.Dm, Channel = c, Id = c.RecipientIds.Count > 0 ? c.RecipientIds[0] : null, Key = "fav" + c.Id, Height = 36 });
                        else if (c.IsGroup) rows.Add(new Row { Kind = Kind.Group, Channel = c, Key = "fav" + c.Id, Height = 24 });
                        else rows.Add(new Row { Kind = Kind.Channel, Channel = c, Guild = s.GuildOf(c), Key = "fav" + c.Id, Height = 20, ShowGuild = true });
                    }
                });
            };

            // Friends, online first.
            var friends = new List<string>();
            foreach (var kv in s.Relationships) if (kv.Value == 1) friends.Add(kv.Key);
            var online = new List<string>();
            var offline = new List<string>();
            foreach (var id in friends) (s.PresenceOf(id).Status != "offline" ? online : offline).Add(id);
            Comparison<string> byName = (a, b) => string.Compare(s.NameOf(a), s.NameOf(b), StringComparison.CurrentCultureIgnoreCase);
            online.Sort(byName);
            offline.Sort(byName);
            sections["Friends"] = () => AddGroup(rows, "Friends", "Friends (" + online.Count + "/" + friends.Count + ")", () =>
            {
                foreach (var id in online) rows.Add(new Row { Kind = Kind.Friend, Id = id, Key = "f" + id, Height = 36, Indent = 0 });
            });
            sections["Offline"] = () => AddGroup(rows, "Offline", "Offline friends (" + offline.Count + ")", () =>
            {
                foreach (var id in offline) rows.Add(new Row { Kind = Kind.Friend, Id = id, Key = "f" + id, Height = 36, Indent = 0 });
            });

            // Recent conversations.
            var dms = new List<Channel>();
            var groups = new List<Channel>();
            foreach (var c in s.PrivateChannels.Values) (c.IsGroup ? groups : dms).Add(c);
            Comparison<Channel> recent = (a, b) => Snowflake.Compare(b.LastMessageId ?? b.Id, a.LastMessageId ?? a.Id);
            dms.Sort(recent);
            groups.Sort(recent);
            int unreadDms = 0;
            foreach (var c in dms) if (s.IsUnread(c)) unreadDms++;
            sections["Recent"] = () => AddGroup(rows, "Recent", "Recent chats" + (unreadDms > 0 ? " (" + unreadDms + " new)" : ""), () =>
            {
                for (int i = 0; i < dms.Count && i < 25; i++)
                    rows.Add(new Row { Kind = Kind.Dm, Channel = dms[i], Id = dms[i].RecipientIds.Count > 0 ? dms[i].RecipientIds[0] : null, Key = "c" + dms[i].Id, Height = 36 });
            });
            sections["Groups"] = () =>
            {
                if (groups.Count == 0) return;
                AddGroup(rows, "Groups", "Group chats (" + groups.Count + ")", () =>
                {
                    foreach (var c in groups) rows.Add(new Row { Kind = Kind.Group, Channel = c, Key = "c" + c.Id, Height = 24 });
                });
            };

            // Servers and their channels.
            sections["Servers"] = () => AddGroup(rows, "Servers", "Servers (" + s.GuildOrder.Count + ")", () =>
            {
                foreach (var gid in s.GuildOrder)
                {
                    Guild g;
                    if (!s.Guilds.TryGetValue(gid, out g)) continue;
                    bool open = !collapsed.Contains("g" + gid) && !(collapsed.Contains("guilds") && !collapsed.Contains("open:" + gid));
                    rows.Add(new Row { Kind = Kind.Guild, Guild = g, Key = "g" + gid, Height = 24, Text = g.Name });
                    if (!open) continue;
                    var visible = new List<Channel>();
                    foreach (var c in g.Channels.Values) if ((c.IsText || c.IsVoice) && g.CanView(c, s.Me.Id)) visible.Add(c);
                    var cats = new Dictionary<string, Channel>();
                    foreach (var c in g.Channels.Values) if (c.IsCategory) cats[c.Id] = c;
                    visible.Sort((a, b) =>
                    {
                        Channel ca, cb;
                        int pa = a.ParentId != null && cats.TryGetValue(a.ParentId, out ca) ? ca.Position : -1;
                        int pb = b.ParentId != null && cats.TryGetValue(b.ParentId, out cb) ? cb.Position : -1;
                        if (pa != pb) return pa.CompareTo(pb);
                        if (a.ParentId != b.ParentId) return string.CompareOrdinal(a.ParentId ?? "", b.ParentId ?? "");
                        return a.Position != b.Position ? a.Position.CompareTo(b.Position) : Snowflake.Compare(a.Id, b.Id);
                    });
                    string lastParent = "\0";
                    foreach (var c in visible)
                    {
                        Channel cat;
                        if (c.ParentId != lastParent && c.ParentId != null && cats.TryGetValue(c.ParentId, out cat))
                            rows.Add(new Row { Kind = Kind.Category, Text = (cat.Name ?? "").ToUpperInvariant(), Height = 17, Indent = 14 });
                        lastParent = c.ParentId;
                        if (c.IsVoice) AddVoiceChannel(rows, c, g, 14, false);
                        else rows.Add(new Row { Kind = Kind.Channel, Channel = c, Guild = g, Key = "c" + c.Id, Height = 20, Indent = 14 });
                    }
                }
            });

            foreach (var key in GroupOrder.Current)
            {
                Action add;
                if (sections.TryGetValue(key, out add)) add();
            }
            Fill(rows);
        }

        // Everything whose name contains the search text, flat: friends, chats, servers, channels.
        void BuildSearch(List<Row> rows)
        {
            var s = Session;
            string q = filter;
            Func<string, bool> hit = x => x != null && x.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
            var users = new List<string>();
            foreach (var kv in s.Relationships)
            {
                if (kv.Value == 2) continue; // blocked
                var u = s.GetUser(kv.Key);
                if (hit(s.NameOf(kv.Key)) || hit(u.Username) || hit(u.GlobalName)) users.Add(kv.Key);
            }
            foreach (var c in s.PrivateChannels.Values)
                if (c.IsDm && c.RecipientIds.Count == 1 && !users.Contains(c.RecipientIds[0]))
                {
                    var u = s.GetUser(c.RecipientIds[0]);
                    if (hit(s.NameOf(u.Id)) || hit(u.Username)) users.Add(u.Id);
                }
            users.Sort((a, b) => string.Compare(s.NameOf(a), s.NameOf(b), StringComparison.CurrentCultureIgnoreCase));
            var groups = new List<Channel>();
            foreach (var c in s.PrivateChannels.Values) if (c.IsGroup && hit(s.ChannelTitle(c))) groups.Add(c);
            var guilds = new List<Guild>();
            var channels = new List<Row>();
            foreach (var gid in s.GuildOrder)
            {
                Guild g;
                if (!s.Guilds.TryGetValue(gid, out g)) continue;
                if (hit(g.Name)) guilds.Add(g);
                foreach (var c in g.Channels.Values)
                    if (c.IsText && hit(c.Name) && g.CanView(c, s.Me.Id))
                        channels.Add(new Row { Kind = Kind.Channel, Channel = c, Guild = g, Key = "c" + c.Id, Height = 20, ShowGuild = true });
                    else if (c.IsVoice && hit(c.Name) && g.CanView(c, s.Me.Id))
                        AddVoiceChannel(channels, c, g, 0, true);
            }

            int total = users.Count + groups.Count + guilds.Count + channels.Count;
            rows.Add(new Row { Kind = Kind.Header, Key = "hSearch", Text = total + " result" + (total == 1 ? "" : "s") + " for \"" + q + "\"", Height = 21 });
            foreach (var id in users)
            {
                int rel;
                s.Relationships.TryGetValue(id, out rel);
                if (rel == 3 || rel == 4) rows.Add(new Row { Kind = Kind.Pending, Id = id, Key = "p" + id, Height = 36, Text = rel == 3 ? "in" : "out" });
                else rows.Add(new Row { Kind = Kind.Friend, Id = id, Key = "f" + id, Height = 36 });
            }
            foreach (var c in groups) rows.Add(new Row { Kind = Kind.Group, Channel = c, Key = "c" + c.Id, Height = 24 });
            foreach (var g in guilds) rows.Add(new Row { Kind = Kind.Guild, Guild = g, Key = "g" + g.Id, Height = 24, Text = g.Name });
            rows.AddRange(channels);
            if (q.Length >= 2 && q.IndexOf(' ') < 0)
                rows.Add(new Row { Kind = Kind.AddAction, Key = "add", Height = 24, Text = "Add \"" + q + "\" as a friend..." });
        }

        // A voice channel, then the people in it.
        void AddVoiceChannel(List<Row> rows, Channel c, Guild g, int indent, bool showGuild)
        {
            rows.Add(new Row { Kind = Kind.VoiceChannel, Channel = c, Guild = g, Key = "v" + c.Id, Height = 20, Indent = indent, ShowGuild = showGuild });
            var members = Session.VoiceMembers(c.Id);
            members.Sort((a, b) => string.Compare(Session.NameOf(a), Session.NameOf(b), StringComparison.CurrentCultureIgnoreCase));
            foreach (var uid in members)
                rows.Add(new Row { Kind = Kind.VoiceUser, Id = uid, Channel = c, Guild = g, Key = "vu" + c.Id + "/" + uid, Height = 20, Indent = indent + 16 });
        }

        void AddGroup(List<Row> rows, string key, string text, Action children)
        {
            rows.Add(new Row { Kind = Kind.Header, Key = "h" + key, Text = text, Height = 21 });
            if (!collapsed.Contains(key)) children();
        }

        void Fill(List<Row> rows)
        {
            shownUsers.Clear();
            foreach (var r in rows) if (r.Id != null) shownUsers.Add(r.Id);
            list.SetRows(new List<object>(rows));
            UpdateCard();
        }

        void Toggle(Row r)
        {
            if (r.Kind == Kind.Header)
            {
                string key = r.Key.Substring(1);
                if (!collapsed.Remove(key)) collapsed.Add(key);
            }
            else if (r.Kind == Kind.Guild && filter.Length > 0)
            {
                // From search results: open that server in the normal list.
                collapsed.Remove("g" + r.Guild.Id);
                collapsed.Add("open:" + r.Guild.Id);
                search.Text = "";
                return;
            }
            else if (r.Kind == Kind.Guild)
            {
                // Servers start collapsed ("guilds"); "open:<id>" remembers which were opened.
                string gid = r.Guild.Id;
                bool isOpen = !collapsed.Contains("g" + gid) && !(collapsed.Contains("guilds") && !collapsed.Contains("open:" + gid));
                if (isOpen) { collapsed.Add("g" + gid); collapsed.Remove("open:" + gid); }
                else { collapsed.Remove("g" + gid); collapsed.Add("open:" + gid); }
            }
            Rebuild();
        }

        async void Activate(Row r)
        {
            if (r == null) return;
            try
            {
                switch (r.Kind)
                {
                    case Kind.Friend:
                        OpenChat(await Session.OpenDm(r.Id), true);
                        break;
                    case Kind.Dm:
                    case Kind.Group:
                    case Kind.Channel:
                        OpenChat(r.Channel, true);
                        break;
                    case Kind.Pending:
                        ProfileForm.Open(Session, r.Id, null);
                        break;
                    case Kind.VoiceChannel:
                        JoinVoice(r.Channel);
                        break;
                    case Kind.VoiceUser:
                        ProfileForm.Open(Session, r.Id, r.Channel.GuildId);
                        break;
                    case Kind.AddAction:
                        AddFriend(filter);
                        break;
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
        }

        void ShowRowMenu(Row r, Point at)
        {
            if (r == null) return;
            var menu = new ContextMenuStrip();
            AddFavoriteItems(menu, r);
            switch (r.Kind)
            {
                case Kind.Friend:
                case Kind.Dm:
                    menu.Items.Add("Send message", null, (s, e) => Activate(r));
                    if (r.Id != null)
                    {
                        string uid = r.Id;
                        var profile = new ToolStripMenuItem("View profile", null, (s, e) => ProfileForm.Open(Session, uid, null));
                        profile.Font = new Font(profile.Font, FontStyle.Bold);
                        menu.Items.Insert(0, profile);
                        var u = Session.GetUser(r.Id);
                        menu.Items.Add("Copy username (" + u.Username + ")", null, (s, e) => Clipboard.SetText(u.Username ?? ""));
                    }
                    if (r.Channel != null) menu.Items.Add("Mark as read", null, (s, e) => Session.MarkRead(r.Channel));
                    if (r.Channel != null) menu.Items.Add("Open saved files", null, (s, e) => OpenFolder(AutoSaver.FolderFor(Session, r.Channel)));
                    int rel;
                    if (r.Id != null && Session.Relationships.TryGetValue(r.Id, out rel) && rel == 1)
                    {
                        string fid = r.Id;
                        menu.Items.Add(new ToolStripSeparator());
                        menu.Items.Add("Remove friend...", null, async (s, e) =>
                        {
                            if (MessageBox.Show(this, "Remove " + Session.NameOf(fid) + " from your friends?", "Buzzcord", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                            try { await Session.RemoveRelationship(fid, "Friends"); }
                            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
                        });
                    }
                    break;
                case Kind.Pending:
                    {
                        string pid = r.Id;
                        var profile = new ToolStripMenuItem("View profile", null, (s, e) => ProfileForm.Open(Session, pid, null));
                        profile.Font = new Font(profile.Font, FontStyle.Bold);
                        menu.Items.Add(profile);
                        if (r.Text == "in")
                        {
                            menu.Items.Add("Accept", null, (s, e) => PendingAction(pid, "accept"));
                            menu.Items.Add("Ignore", null, (s, e) => PendingAction(pid, "ignore"));
                        }
                        else menu.Items.Add("Cancel request", null, (s, e) => PendingAction(pid, "cancel"));
                        menu.Items.Add("Send message", null, async (s, e) =>
                        {
                            try { OpenChat(await Session.OpenDm(pid), true); }
                            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
                        });
                        break;
                    }
                case Kind.Group:
                case Kind.Channel:
                    menu.Items.Add("Open", null, (s, e) => Activate(r));
                    menu.Items.Add("Mark as read", null, (s, e) => Session.MarkRead(r.Channel));
                    if (r.Kind == Kind.Group) menu.Items.Add("Open saved files", null, (s, e) => OpenFolder(AutoSaver.FolderFor(Session, r.Channel)));
                    break;
                case Kind.Guild:
                    menu.Items.Add("Mark server as read", null, (s, e) => { foreach (var c in r.Guild.Channels.Values) if (c.IsText) Session.MarkRead(c); });
                    break;
                case Kind.VoiceChannel:
                    if (VoiceManager.IsIn(Session, r.Channel.Id)) menu.Items.Add("Leave voice", null, (s, e) => VoiceManager.Leave());
                    else
                    {
                        var join = new ToolStripMenuItem("Join voice", null, (s, e) => JoinVoice(r.Channel));
                        join.Font = new Font(join.Font, FontStyle.Bold);
                        menu.Items.Add(join);
                    }
                    menu.Items.Add("Voice settings...", null, (s, e) => { using (var f = new VoiceSettingsForm()) f.ShowDialog(this); });
                    break;
                case Kind.VoiceUser:
                    {
                        string uid = r.Id;
                        var profile = new ToolStripMenuItem("View profile", null, (s, e) => ProfileForm.Open(Session, uid, r.Channel.GuildId));
                        profile.Font = new Font(profile.Font, FontStyle.Bold);
                        menu.Items.Add(profile);
                        if (VoiceManager.IsIn(Session, r.Channel.Id) && uid != Session.Me.Id)
                            VoiceBar.AddVolumeItem(menu, VoiceManager.Current, uid, Session);
                        break;
                    }
                default:
                    return;
            }
            menu.Show(list, at);
        }

        // Pin / follow items at the top of a row's menu (chats and channels only).
        void AddFavoriteItems(ContextMenuStrip menu, Row r)
        {
            Channel c = r.Channel;
            if (c == null && r.Kind == Kind.Friend && r.Id != null)
                foreach (var pc in Session.PrivateChannels.Values)
                    if (pc.IsDm && pc.RecipientIds.Count == 1 && pc.RecipientIds[0] == r.Id) { c = pc; break; }
            if (c == null) return;
            string cid = c.Id;
            bool pinned = Favorites.IsPinned(Session, cid);
            menu.Items.Add(pinned ? "Unpin from Favorites" : "Pin to Favorites", null, (s, e) => Favorites.SetPinned(Session, cid, !pinned));
            if (!c.IsDm)
            {
                bool followed = Favorites.IsFollowed(Session, cid);
                menu.Items.Add(new ToolStripMenuItem("Notify me about every message", null, (s, e) => Favorites.SetFollowed(Session, cid, !followed)) { Checked = followed });
            }
            menu.Items.Add(new ToolStripSeparator());
        }

        // ---- drawing rows ----

        static Color SelectedBack { get { return Theme.Selected; } }
        static Color HoverBack { get { return Theme.Hover; } }
        static Color Playing { get { return Theme.Playing; } }
        static Color HeaderEnd { get { return Theme.GroupEnd; } }

        static void DrawText(Graphics g, string s, Font f, Color c, int x, int y, int w, int h)
        {
            if (w > 0) TextRenderer.DrawText(g, s, f, new Rectangle(x, y, w, h), c, TextFlags);
        }

        // DrawImageUnscaled would rescale by the bitmap's DPI on high-DPI screens; this draws 1:1.
        static void Blit(Graphics g, Image img, int x, int y) { g.DrawImage(img, new Rectangle(x, y, img.Width, img.Height)); }

        void DrawRow(Graphics g, Row r, Rectangle b, bool selected, bool hover)
        {
            if (r.Kind == Kind.Header)
            {
                using (var br = new LinearGradientBrush(b, Icons.GroupBack, HeaderEnd, 90f)) g.FillRectangle(br, b);
                string arrow = collapsed.Contains(r.Key.Substring(1)) ? "▸" : "▾";
                DrawText(g, arrow + " " + r.Text, fNameBold, Icons.Purple, b.X + 4, b.Y + 4, b.Width - 8, 15);
                return;
            }

            using (var back = new SolidBrush(selected ? SelectedBack : hover ? HoverBack : Theme.Window)) g.FillRectangle(back, b);
            int x = b.X + 6 + r.Indent;
            var s = Session;

            switch (r.Kind)
            {
                case Kind.Empty:
                    DrawText(g, r.Text, fName, Theme.SubText, x, b.Y + 8, b.Width - 12, 15);
                    break;

                case Kind.Friend:
                case Kind.Dm:
                    {
                        var p = r.Id != null ? s.PresenceOf(r.Id) : new Presence();
                        bool unread = r.Channel != null ? s.IsUnread(r.Channel) : UnreadDmWith(r.Id);
                        Blit(g, Icons.Status(p.Status, 16), x, b.Y + 10);
                        int textX = x + 21;
                        int avatar = b.Height - 8;
                        int textW = b.Right - textX - avatar - 10;
                        string name = r.Id != null ? s.NameOf(r.Id) : s.ChannelTitle(r.Channel);
                        int mentions = r.Channel != null ? s.MentionCount(r.Channel.Id) : 0;
                        if (mentions > 0) name += " (" + mentions + ")";
                        DrawText(g, name, unread ? fNameBold : fName, unread ? Icons.Purple : Theme.Text, textX, b.Y + 3, textW, 15);
                        string line = p.Summary;
                        if (line != null)
                            DrawText(g, line, fSmall, p.MainActivity != null ? Playing : Theme.SubText, textX, b.Y + 19, textW, 14);
                        if (r.Id != null)
                        {
                            var rect = new Rectangle(b.Right - avatar - 6, b.Y + 4, avatar, avatar);
                            var img = ImageCache.Scaled(s.GetUser(r.Id).AvatarUrl(64), avatar, list.Invalidate);
                            if (img != null) Blit(g, img, rect.X, rect.Y);
                            else using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, rect);
                            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, rect);
                        }
                        break;
                    }

                case Kind.Group:
                    {
                        bool unread = s.IsUnread(r.Channel);
                        // Two little buddies for a group.
                        Blit(g, Icons.Status("online", 12), x, b.Y + 4);
                        Blit(g, Icons.Status("online", 12), x + 6, b.Y + 9);
                        DrawText(g, s.ChannelTitle(r.Channel), unread ? fNameBold : fName, unread ? Icons.Purple : Theme.Text, x + 22, b.Y + 5, b.Right - x - 26, 15);
                        break;
                    }

                case Kind.Guild:
                    {
                        var rect = new Rectangle(x, b.Y + 3, 18, 18);
                        var img = ImageCache.Scaled(r.Guild.IconUrl(32), 18, list.Invalidate);
                        if (img != null) Blit(g, img, rect.X, rect.Y);
                        else
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            using (var br = new SolidBrush(Icons.PurpleLight)) g.FillEllipse(br, rect);
                            g.SmoothingMode = SmoothingMode.Default;
                        }
                        bool muted = s.IsGuildMuted(r.Guild.Id);
                        int mentions = 0;
                        foreach (var c in r.Guild.Channels.Values)
                            if (c.IsText) mentions += s.MentionCount(c.Id);
                        bool open = !collapsed.Contains("g" + r.Guild.Id) && !(collapsed.Contains("guilds") && !collapsed.Contains("open:" + r.Guild.Id));
                        string text = (open ? "▾ " : "▸ ") + r.Guild.Name + (mentions > 0 ? " (" + mentions + ")" : "");
                        DrawText(g, text, mentions > 0 ? fNameBold : fName, muted ? Theme.SubText : Theme.Text, x + 22, b.Y + 5, b.Right - x - 26, 15);
                        break;
                    }

                case Kind.Category:
                    DrawText(g, r.Text, fCategory, Theme.SubText, x, b.Y + 3, b.Right - x - 4, 13);
                    break;

                case Kind.Pending:
                    {
                        Blit(g, Icons.Status(r.Text == "in" ? "online" : "offline", 16), x, b.Y + 10);
                        int textX = x + 21;
                        int avatar = b.Height - 8;
                        int textW = b.Right - textX - avatar - 10;
                        DrawText(g, s.NameOf(r.Id), r.Text == "in" ? fNameBold : fName, r.Text == "in" ? Icons.Purple : Theme.Text, textX, b.Y + 3, textW, 15);
                        if (r.Text == "out") DrawText(g, "Request sent", fSmall, Theme.SubText, textX, b.Y + 19, textW, 14);
                        foreach (var link in PendingLinks(r, b))
                            DrawText(g, link.Value == "accept" ? "Accept" : link.Value == "ignore" ? "Ignore" : "Cancel", fLink,
                                link.Value == "accept" ? Playing : Theme.Link, link.Key.X, link.Key.Y, link.Key.Width + 6, link.Key.Height);
                        var rect = new Rectangle(b.Right - avatar - 6, b.Y + 4, avatar, avatar);
                        var img = ImageCache.Scaled(s.GetUser(r.Id).AvatarUrl(64), avatar, list.Invalidate);
                        if (img != null) Blit(g, img, rect.X, rect.Y);
                        else using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, rect);
                        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, rect);
                        break;
                    }

                case Kind.AddAction:
                    Blit(g, AddIcon(), x, b.Y + 4);
                    DrawText(g, r.Text, fLink, Theme.Link, x + 22, b.Y + 5, b.Right - x - 26, 15);
                    break;

                case Kind.VoiceChannel:
                    {
                        bool here = VoiceManager.IsIn(s, r.Channel.Id);
                        var color = here ? Color.FromArgb(0x2E, 0xA0, 0x43) : Theme.Dim;
                        VoiceBar.DrawSpeaker(g, new Rectangle(x + 2, b.Y + 4, 12, 12), color);
                        string label = r.Channel.Name + (r.ShowGuild && r.Guild != null ? "  — " + r.Guild.Name : "");
                        DrawText(g, label, here ? fNameBold : fName, here ? color : Theme.Dim, x + 18, b.Y + 3, b.Right - x - 22, 15);
                        break;
                    }

                case Kind.VoiceUser:
                    {
                        var call = VoiceManager.Current;
                        bool talking = call != null && VoiceManager.IsIn(s, r.Channel.Id) && call.IsSpeaking(r.Id);
                        var rect = new Rectangle(x, b.Y + 2, 16, 16);
                        var img = ImageCache.Scaled(s.GetUser(r.Id).AvatarUrl(64), 16, list.Invalidate);
                        if (img != null) Blit(g, img, rect.X, rect.Y);
                        else using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, rect);
                        if (talking) using (var pen = new Pen(Color.FromArgb(0x2E, 0xC0, 0x4F), 2)) g.DrawRectangle(pen, rect.X - 1, rect.Y - 1, rect.Width + 1, rect.Height + 1);
                        VoiceState vs;
                        string flags = "";
                        if (s.VoiceStates.TryGetValue(r.Id, out vs))
                            flags = vs.SelfDeaf || vs.Deaf ? "  (deafened)" : vs.SelfMute || vs.Mute ? "  (muted)" : vs.SelfStream ? "  (live)" : "";
                        DrawText(g, s.NameOf(r.Id), talking ? fNameBold : fName, Theme.Text, x + 21, b.Y + 3, b.Right - x - 25, 15);
                        if (flags.Length > 0)
                        {
                            int w = TextRenderer.MeasureText(s.NameOf(r.Id), talking ? fNameBold : fName, Size.Empty, MeasureFlags).Width;
                            DrawText(g, flags, fSmall, Theme.SubText, x + 21 + w, b.Y + 4, b.Right - x - 25 - w, 14);
                        }
                        break;
                    }

                case Kind.Channel:
                    {
                        bool unread = s.IsUnread(r.Channel);
                        int mentions = s.MentionCount(r.Channel.Id);
                        bool muted = s.IsMuted(r.Channel);
                        string label = "# " + r.Channel.Name + (mentions > 0 ? "  (" + mentions + ")" : "");
                        if (r.ShowGuild && r.Guild != null) label += "  — " + r.Guild.Name;
                        if (Favorites.IsFollowed(s, r.Channel.Id)) label += "  •"; // followed: alerts on every message
                        DrawText(g, label, unread ? fNameBold : fName,
                            muted ? Theme.SubText : unread ? Theme.Text : Theme.Dim, x + 4, b.Y + 3, b.Right - x - 8, 15);
                        break;
                    }
            }
        }

        // ---- friend requests ----

        // Clickable links on a request row: Accept/Ignore for theirs, Cancel for yours.
        static List<KeyValuePair<Rectangle, string>> PendingLinks(Row r, Rectangle b)
        {
            var links = new List<KeyValuePair<Rectangle, string>>();
            int x = b.X + 6 + r.Indent + 21, y = b.Y + 19;
            var names = r.Text == "in" ? new[] { "accept", "ignore" } : new[] { "cancel" };
            if (r.Text == "out") x += TextRenderer.MeasureText("Request sent", fSmall, Size.Empty, MeasureFlags).Width + 8;
            foreach (var n in names)
            {
                var size = TextRenderer.MeasureText(n == "accept" ? "Accept" : n == "ignore" ? "Ignore" : "Cancel", fLink, Size.Empty, MeasureFlags);
                links.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x, y, size.Width, 14), n));
                x += size.Width + 12;
            }
            return links;
        }

        // What a click at p does on this row: "accept", "ignore", "cancel", "add", or null.
        static string ActionAt(Row r, Rectangle bounds, Point p)
        {
            if (r.Kind == Kind.AddAction) return "add";
            if (r.Kind != Kind.Pending) return null;
            foreach (var link in PendingLinks(r, bounds))
                if (link.Key.Contains(p)) return link.Value;
            return null;
        }

        async void PendingAction(string userId, string action)
        {
            string name = Session.NameOf(userId);
            try
            {
                if (action == "accept")
                {
                    await Session.AddOrAcceptFriend(userId, "Friends");
                    state.Text = "You and " + name + " are now friends";
                }
                else
                {
                    string q = action == "ignore" ? "Ignore " + name + "'s friend request?" : "Cancel your friend request to " + name + "?";
                    if (MessageBox.Show(this, q, "Buzzcord", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                    await Session.RemoveRelationship(userId, "Friends");
                    state.Text = action == "ignore" ? "Ignored " + name + "'s friend request" : "Cancelled your friend request to " + name;
                }
            }
            catch (Exception ex)
            {
                Program.Log("friend request " + action + " " + userId + ": " + ex);
                MessageBox.Show(this, FriendActionError(action, name, ex), "Buzzcord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // The message for a failed Accept / Ignore / Cancel. Public for the self-test.
        public static string FriendActionError(string action, string name, Exception ex)
        {
            string what = action == "accept" ? "accept " + name + "'s friend request"
                : action == "ignore" ? "ignore " + name + "'s friend request"
                : "cancel your friend request to " + name;
            var api = ex as ApiException;
            string why = api != null && api.IsCaptcha ? "Discord asked for a captcha and it wasn't solved. Try again and finish the captcha."
                : api != null && api.Status == 401 ? "Discord signed this account out. Sign in again."
                : (ex.GetBaseException().Message ?? "unknown error");
            return "Couldn't " + what + ".\n\n" + why;
        }

        public async void AddFriend(string prefill)
        {
            string name = Prompt.Show(this, "Add a Friend", "Their Discord username (not their display name):", prefill ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await Session.SendFriendRequest(name);
                state.Text = "Friend request sent to " + name.Trim();
                if (search.Text.Length > 0) search.Text = "";
            }
            catch (ApiException ex)
            {
                long code = ex.Body != null ? ex.Body.Long("code") : 0;
                string msg = code == 80004 ? "There's no one called \"" + name.Trim() + "\" on Discord. Check the spelling: it's their username, not their display name."
                    : code == 80007 ? "You're already friends with " + name.Trim() + "."
                    : code == 80000 || code == 80001 ? name.Trim() + " isn't accepting friend requests from you (their privacy settings)."
                    : ex.Message;
                MessageBox.Show(this, msg, "Add a Friend");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Add a Friend"); }
        }

        static Bitmap addIcon;

        // A little smiley with a green plus, for "Add a Friend".
        static Bitmap AddIcon()
        {
            if (addIcon != null) return addIcon;
            addIcon = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(addIcon))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(Icons.Status("online", 14), new Rectangle(0, 0, 14, 14));
                using (var br = new SolidBrush(Color.FromArgb(0x2E, 0x9D, 0x32)))
                {
                    g.FillEllipse(br, 8, 8, 8, 8);
                }
                using (var pen = new Pen(Color.White, 1.6f))
                {
                    g.DrawLine(pen, 12, 9.5f, 12, 14.5f);
                    g.DrawLine(pen, 9.5f, 12, 14.5f, 12);
                }
            }
            return addIcon;
        }

        bool UnreadDmWith(string userId)
        {
            foreach (var c in Session.PrivateChannels.Values)
                if (c.IsDm && c.RecipientIds.Count == 1 && c.RecipientIds[0] == userId) return Session.IsUnread(c);
            return false;
        }

        void UpdateCard()
        {
            var r = list.SelectedRow as Row;
            JObj activity = null;
            string who = null;
            if (r != null && r.Id != null && (r.Kind == Kind.Friend || r.Kind == Kind.Dm))
            {
                activity = Session.PresenceOf(r.Id).MainActivity;
                who = Session.NameOf(r.Id);
            }
            card.Set(who, activity);
            card.Height = activity != null ? 92 : 0;
        }

        // ---- messages ----

        void OnMessage(JObj m)
        {
            AutoSaver.Consider(Session, m);
            Archive.Consider(Session, m);
            string cid = m.Str("channel_id");
            var author = m.Obj("author");
            bool mine = author != null && Session.Me != null && author.Str("id") == Session.Me.Id;
            var c = Session.FindChannel(cid);
            ChatForm chat;
            chats.TryGetValue(cid, out chat);
            if (chat != null) chat.Add(m);
            if (mine || c == null) return;

            // What kind of alert this is, if any.
            AlertKind kind;
            string from = author != null ? Session.NameOf(author.Str("id")) : "Someone";
            string title;
            if (c.IsPrivate)
            {
                if (Session.IsMuted(c)) return;
                kind = AlertKind.Message;
                title = c.IsGroup ? from + " in " + Session.ChannelTitle(c) : from;
            }
            else if (Session.ShouldNotifyMention(m)) { kind = AlertKind.Mention; title = from + " in " + Session.ChannelTitle(c); }
            else if (Favorites.IsFollowed(Session, cid)) { kind = AlertKind.Followed; title = from + " in " + Session.ChannelTitle(c); }
            else return;
            string text = m.Str("content");
            if (string.IsNullOrEmpty(text) && m.Arr("attachments") != null && m.Arr("attachments").Count > 0) text = "(sent a file)";

            if (chat != null)
            {
                // The conversation is already open: flash it, ding if it's not in front.
                if (chat.ContainsFocus) return;
                Native.Flash(chat);
                Notifier.Alert(this, kind, title, text, cid, null, false);
                return;
            }
            var decision = Notifier.DecideFor(Session);
            if (c.IsPrivate && decision.Popup)
            {
                OpenChat(c, false);   // the classic pop-up IM window, without stealing focus
                Notifier.Alert(this, kind, title, text, cid, null, false);
            }
            else Notifier.Alert(this, kind, title, text, cid, null, true, m.Str("id"));
        }

        public void OpenChatById(string channelId)
        {
            if (!Visible) Show();
            var c = Session.FindChannel(channelId);
            if (c != null) OpenChat(c, true);
        }

        // Unread DMs/groups plus mentions, for the tray and taskbar badges: the rows of the
        // Unread Messages window added up.
        public int UnreadCount()
        {
            return Session.BadgeCount();
        }

        int badgeShown = -1;

        public void SetTaskbarBadge(int count)
        {
            if (count == badgeShown || !IsHandleCreated) return;
            badgeShown = count;
            if (count <= 0) { Native.SetOverlay(this, null, null); return; }
            using (var icon = Icons.CountBadge(count)) Native.SetOverlay(this, icon, count + " unread");
            if (mentionsButton != null) UpdateMentionsButton();
        }

        public void OpenChat(Channel c, bool activate)
        {
            if (c == null) return;
            ChatForm chat;
            if (chats.TryGetValue(c.Id, out chat) && !chat.IsDisposed)
            {
                if (chat.WindowState == FormWindowState.Minimized) chat.WindowState = FormWindowState.Normal;
                if (activate) chat.Activate();
                else Native.Flash(chat);
                return;
            }
            chat = new ChatForm(Session, c, !activate);
            chats[c.Id] = chat;
            chat.FormClosed += (s, e) => { ChatForm cur; if (chats.TryGetValue(c.Id, out cur) && cur == chat) chats.Remove(c.Id); };
            chat.Show();
            if (!activate) Native.Flash(chat);
        }

        public void CloseChats()
        {
            foreach (var c in new List<ChatForm>(chats.Values)) c.Close();
        }
    }

    // Rich presence card: game art, what they're doing, and for how long.
    public class ActivityCard : Control
    {
        string who;
        JObj activity;
        string iconUrl;

        public ActivityCard()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Icons.Panel;
        }

        public void Set(string who, JObj activity)
        {
            this.who = who;
            this.activity = activity;
            iconUrl = null;
            if (activity != null && LargeUrl() == null)
            {
                var me = activity;
                var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
                PresenceManager.AppIconUrl(activity.Str("application_id")).ContinueWith(t =>
                {
                    if (activity == me && t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) { iconUrl = t.Result; Invalidate(); }
                }, ui);
            }
            Invalidate();
        }

        string LargeUrl()
        {
            var assets = activity != null ? activity.Obj("assets") : null;
            return assets != null ? PresenceManager.AssetUrl(activity.Str("application_id"), assets.Str("large_image")) : null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (activity == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var frame = new Rectangle(6, 4, Width - 12, Height - 8);
            using (var br = new SolidBrush(Theme.Window)) g.FillRectangle(br, frame);
            using (var pen = new Pen(Icons.PurpleLight)) g.DrawRectangle(pen, frame);

            var art = new Rectangle(12, 12, 64, 64);
            string large = LargeUrl() ?? iconUrl;
            var img = ImageCache.Peek(large);
            if (img != null) g.DrawImage(img, art);
            else
            {
                using (var br = new SolidBrush(Icons.GroupBack)) g.FillRectangle(br, art);
                using (var ef = new Font("Segoe UI Emoji", 22f)) using (var br = new SolidBrush(Theme.SubText)) g.DrawString("\U0001F3AE", ef, br, art.X + 10, art.Y + 12);
                if (large != null) ImageCache.Get(large, i => Invalidate());
            }
            var assets = activity.Obj("assets");
            string small = assets != null ? PresenceManager.AssetUrl(activity.Str("application_id"), assets.Str("small_image")) : null;
            if (small != null)
            {
                var simg = ImageCache.Peek(small);
                var sr = new Rectangle(art.Right - 20, art.Bottom - 20, 24, 24);
                if (simg != null)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(sr);
                        using (var br = new SolidBrush(Theme.Window)) g.FillEllipse(br, Rectangle.Inflate(sr, 2, 2));
                        var clip = g.Clip;
                        g.SetClip(path);
                        g.DrawImage(simg, sr);
                        g.Clip = clip;
                    }
                }
                else ImageCache.Get(small, i => Invalidate());
            }

            int x = art.Right + 8;
            float w = frame.Right - x - 4;
            var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            using (var bold = new Font("Tahoma", 8.25f, FontStyle.Bold))
            using (var normal = new Font("Tahoma", 7.75f))
            using (var purple = new SolidBrush(Icons.Purple))
            {
                g.DrawString(Presence.Describe(activity), bold, purple, new RectangleF(x, 10, w, 15), fmt);
                float y = 26;
                foreach (var line in Lines())
                {
                    using (var br = new SolidBrush(Theme.Text)) g.DrawString(line, normal, br, new RectangleF(x, y, w, 14), fmt);
                    y += 14;
                    if (y > 70) break;
                }
            }
        }

        IEnumerable<string> Lines()
        {
            bool spotify = activity.Str("name") == "Spotify" && activity.Long("type") == 2;
            if (!spotify && activity.Str("details") != null) yield return activity.Str("details");
            string state = activity.Str("state");
            var party = activity.Obj("party");
            var size = party != null ? party.Arr("size") : null;
            if (size != null && size.Count == 2) state = (state ?? "") + " (" + size[0] + " of " + size[1] + ")";
            if (spotify)
            {
                var assets = activity.Obj("assets");
                if (assets != null && assets.Str("large_text") != null) yield return "on " + assets.Str("large_text");
            }
            else if (state != null) yield return state;
            var ts = activity.Obj("timestamps");
            if (ts != null)
            {
                long now = PresenceManager.NowMs();
                if (ts.Has("end") && ts.Long("end") > now) yield return Clock(ts.Long("end") - now) + " left";
                else if (ts.Has("start") && ts.Long("start") > 0) yield return Clock(now - ts.Long("start")) + " elapsed";
            }
        }

        static string Clock(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }
    }

    static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(320, 96), Font = new Font("Tahoma", 8.25f), ShowInTaskbar = false
            })
            {
                var l = new Label { Text = label, Location = new Point(10, 10), AutoSize = true };
                var t = new TextBox { Text = value, Location = new Point(10, 30), Width = 300 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(154, 62) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(235, 62) };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok;
                Theme.Apply(f);
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }
    }
}
