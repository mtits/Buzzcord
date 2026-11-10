using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Buzzcord
{
    // "So we don't miss things": Discord's own mentions inbox (it remembers mentions from before
    // Buzzcord was open) and Buzzcord's history of every alert it raised.
    public class AlertsForm : Form
    {
        readonly Session s;
        readonly BuddyListForm owner;
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill };
        readonly ToolStripButton mentionsTab = new ToolStripButton("Mentions") { CheckOnClick = false };
        readonly ToolStripButton historyTab = new ToolStripButton("Notification history") { CheckOnClick = false };
        readonly ToolStripButton clear = new ToolStripButton("Clear history") { Alignment = ToolStripItemAlignment.Right };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray };
        bool pageReady, showingHistory, loading, noMore;
        string oldest;
        AlertEntry shownNewest;   // the newest history entry on screen
        bool shownAny;

        // Self-test renders: the mentions inbox (newest first, given the "before" id) instead of
        // asking Discord.
        public static Func<string, List<object>> MentionsForTest;
        public void ShowMentionsTab() { ShowMentions(); }
        public void LoadOlderMentions() { LoadMentions(); }
        public string PageText()
        {
            try { return web.Document != null && web.Document.Body != null ? web.Document.Body.InnerText ?? "" : ""; }
            catch (Exception) { return ""; }
        }

        public AlertsForm(Session s, BuddyListForm owner)
        {
            this.s = s;
            this.owner = owner;
            Text = "Mentions & notifications" + (s.Me != null ? " - " + s.Me.DisplayName : "");
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            ClientSize = new Size(540, 600);
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.FromControl(owner).WorkingArea;
            Location = new Point(Math.Max(wa.Left, owner.Left - Width - 8), owner.Top);

            var tabs = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 3, 6, 3) };
            mentionsTab.Click += (o, e) => ShowMentions();
            historyTab.Click += (o, e) => ShowHistory();
            clear.Click += (o, e) =>
            {
                if (MessageBox.Show(this, "Clear the notification history?", "Buzzcord", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                Notifier.Clear(s);
                ShowHistory();
            };
            tabs.Items.AddRange(new ToolStripItem[] { mentionsTab, new ToolStripSeparator(), historyTab, clear });

            web.ScriptErrorsSuppressed = true;
            web.AllowWebBrowserDrop = false;
            web.Navigating += Navigating;
            web.NewWindow += (o, e) => e.Cancel = true;
            web.DocumentCompleted += (o, e) =>
            {
                if (pageReady) return;
                pageReady = true;
                if (Notifier.Unseen(s) > 0) ShowHistory(); else ShowMentions();
            };
            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(web);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0) };
            host.Controls.Add(border);
            Controls.Add(host);
            Controls.Add(status);
            Controls.Add(tabs);

            Notifier.HistoryChanged += OnHistory;
            FormClosed += (o, e) => Notifier.HistoryChanged -= OnHistory;
            web.DocumentText = MessageHtml.Page;
            Theme.Attach(this, () => Script("setTheme", Theme.ChatCss()));
        }

        // When an alert comes in, add it at the bottom (not when they're only marked seen, so new
        // ones stay highlighted). The page only scrolls down if you were already at the bottom.
        void OnHistory(Session changed)
        {
            if (changed == s && showingHistory && !IsDisposed) HistoryChanged();
        }

        void SetTab(bool history)
        {
            showingHistory = history;
            mentionsTab.Checked = !history;
            historyTab.Checked = history;
            mentionsTab.Font = new Font(Font, history ? FontStyle.Regular : FontStyle.Bold);
            historyTab.Font = new Font(Font, history ? FontStyle.Bold : FontStyle.Regular);
            clear.Visible = history;
            Script("clearLog");
            Script("setOlder", "");
        }

        // ---- Discord's mentions inbox ----

        void ShowMentions()
        {
            SetTab(false);
            oldest = null;
            noMore = false;
            LoadMentions();
        }

        async void LoadMentions()
        {
            if (loading || noMore) return;
            loading = true;
            status.Text = "Loading mentions...";
            if (oldest != null) Script("setOlder", "Loading...");
            try
            {
                string path = "/users/@me/mentions?limit=25&roles=true&everyone=true" + (oldest != null ? "&before=" + oldest : "");
                List<object> list;
                if (MentionsForTest != null) list = MentionsForTest(oldest);
                else list = await s.Api.Get(path) as List<object>;
                if (showingHistory) return;
                var page = new List<JObj>();
                foreach (var o in list ?? new List<object>()) { var m = o as JObj; if (m != null) page.Add(m); }
                bool first = oldest == null;
                if (page.Count > 0) oldest = page[page.Count - 1].Str("id");
                if (page.Count < 25) noMore = true;
                // Discord sends newest first; the page shows them like a chat, newest at the bottom.
                // Older pages go in above, keeping your place. The link first, so the first page
                // still lands exactly at the bottom.
                Script("setOlder", noMore ? (first ? "" : "<span class=\"t\">No older mentions.</span>") : "<a href=\"app://more/\">Show older mentions</a>");
                Script(first ? "append" : "prepend", MentionsHtml(s, page));
                status.Text = oldest == null ? "No mentions. Nice and quiet." : "Mentions from Discord's inbox, newest at the bottom.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                if (!noMore && oldest != null) Script("setOlder", "<a href=\"app://more/\">Show older mentions</a>");
            }
            finally { loading = false; }
        }

        // One page of Discord's mentions inbox (newest first, as Discord sends it) as HTML, oldest
        // first. Public for the self-test.
        public static string MentionsHtml(Session s, List<JObj> page)
        {
            var sb = new StringBuilder();
            for (int i = page.Count - 1; i >= 0; i--)
            {
                var m = page[i];
                var c = s.FindChannel(m.Str("channel_id"));
                string where = c != null ? s.ChannelTitle(c) : "a channel you can't see";
                sb.Append("<div class=\"day\" style=\"text-align:left\">").Append(MessageHtml.E(where)).Append(" &middot; ")
                  .Append(MessageHtml.E(Snowflake.Time(m.Str("id")).ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture)));
                if (c != null) sb.Append(" &middot; <a href=\"app://channel/").Append(MessageHtml.E(c.Id)).Append("\">open chat</a>");
                sb.Append("</div>").Append(MessageHtml.Render(s, m));
            }
            return sb.ToString();
        }

        // ---- Buzzcord's notification history ----

        // The whole history, oldest at the top and newest at the bottom, opened at the bottom.
        void ShowHistory()
        {
            SetTab(true);
            var list = Notifier.History(s);
            shownNewest = list.Count > 0 ? list[0] : null;
            shownAny = list.Count > 0;
            Script("append", HistoryHtml(list));
            HistoryStatus();
            Notifier.MarkSeen(s);
        }

        // New alerts go at the bottom without redrawing the rest. Cleared: start over.
        void HistoryChanged()
        {
            var list = Notifier.History(s);
            if (list.Count == 0) { if (shownAny) ShowHistory(); return; }
            int at = shownNewest != null ? list.IndexOf(shownNewest) : -1;
            if (at < 0) { ShowHistory(); return; }
            if (at == 0) return;   // nothing new (only marked seen)
            shownNewest = list[0];
            shownAny = true;
            Script("append", HistoryHtml(list.GetRange(0, at)));
            HistoryStatus();
            Notifier.MarkSeen(s);
        }

        void HistoryStatus()
        {
            int n = Notifier.History(s).Count;
            status.Text = n == 0 ? "Nothing yet. Alerts Buzzcord shows you will be listed here." : n + " alert" + (n == 1 ? "" : "s") + ", newest at the bottom.";
        }

        // History entries as HTML. `entries` is newest first, as Notifier keeps (and saves) them;
        // the page shows them oldest first, like a chat log. Unseen ones are highlighted. Public for
        // the self-test.
        public static string HistoryHtml(List<AlertEntry> entries)
        {
            var sb = new StringBuilder();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var a = entries[i];
                sb.Append("<div class=\"m").Append(a.Seen ? "" : " hl").Append("\"><span class=\"t\">(")
                  .Append(MessageHtml.E(a.Time.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture))).Append(")</span> ")
                  .Append(KindLabel(a.Kind)).Append(" <b>").Append(MessageHtml.E(a.Title)).Append("</b>");
                if (!string.IsNullOrEmpty(a.Text)) sb.Append(": ").Append(MessageHtml.E(a.Text));
                if (a.ChannelId != null) sb.Append(" <a href=\"app://channel/").Append(MessageHtml.E(a.ChannelId)).Append("\">open</a>");
                else if (a.UserId != null) sb.Append(" <a href=\"app://profile/").Append(MessageHtml.E(a.UserId)).Append("\">profile</a>");
                sb.Append("</div>");
            }
            return sb.ToString();
        }

        static string KindLabel(AlertKind k)
        {
            switch (k)
            {
                case AlertKind.Mention: return "<span class=\"mention\">@</span>";
                case AlertKind.FriendRequest: return "<span class=\"pill\">friend request</span>";
                case AlertKind.FriendAccepted: return "<span class=\"pill\">new friend</span>";
                case AlertKind.SignIn: return "<span class=\"pill\">online</span>";
                case AlertKind.Followed: return "<span class=\"pill\">followed</span>";
                case AlertKind.Unfriended: return "<span class=\"pill\" style=\"color:#d03434\">unfriended</span>";
                default: return "<span class=\"pill\">message</span>";
            }
        }

        void Script(string fn, params object[] args)
        {
            if (!pageReady || web.Document == null) return;
            try { web.Document.InvokeScript(fn, args); }
            catch (Exception) { }
        }

        void Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.ToString();
            if (url == "about:blank") return;
            e.Cancel = true;
            if (url.StartsWith("app://more", StringComparison.OrdinalIgnoreCase)) { LoadMentions(); return; }
            if (url.StartsWith("app://channel/", StringComparison.OrdinalIgnoreCase)) { owner.OpenChatById(Uri.UnescapeDataString(url.Substring(14))); return; }
            if (url.StartsWith("app://profile/", StringComparison.OrdinalIgnoreCase)) { ProfileForm.Open(s, Uri.UnescapeDataString(url.Substring(14)), null); return; }
            if (url.StartsWith("app://open/", StringComparison.OrdinalIgnoreCase)) url = Uri.UnescapeDataString(url.Substring(11));
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Program.OpenUrl(url);
        }
    }

    // About box, with the Donate button.
    public class AboutForm : Form
    {
        public static void Open(IWin32Window owner)
        {
            using (var f = new AboutForm()) f.ShowDialog(owner);
        }

        AboutForm()
        {
            Text = "About Buzzcord";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(340, 280);

            var header = new BufferedPanel { Dock = DockStyle.Top, Height = 64 };
            header.Paint += (s, e) =>
            {
                Icons.FillHeader(e.Graphics, header.ClientRectangle);
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var logo = Icons.AppBitmap(44)) e.Graphics.DrawImage(logo, 12, 10);
                using (var f = new Font("Arial", 15f, FontStyle.Bold | FontStyle.Italic))
                using (var br = new SolidBrush(Icons.Purple))
                    e.Graphics.DrawString("Buzzcord", f, br, 62, 8);
                using (var f = new Font("Tahoma", 8f))
                using (var br = new SolidBrush(Theme.Dim))
                    e.Graphics.DrawString("version " + Program.Version, f, br, 64, 38);
            };
            var text = new Label
            {
                Text = "A lightweight, old-school messenger for Discord, by Marc Turtal.\n\n" +
                       "Unofficial: not made, endorsed or supported by Discord Inc. Not affiliated with Yahoo. " +
                       "Free and open source under the MIT License.\n\n" +
                       "If Buzzcord makes your day a little better, you can buy me a coffee.",
                Location = new Point(16, 76), Size = new Size(310, 120)
            };
            var site = new LinkLabel { Text = "app.marcturtal.com/buzzcord", AutoSize = true, Location = new Point(16, 204) };
            site.LinkClicked += (s, e) => Program.OpenUrl(Program.SiteUrl);
            var source = new LinkLabel { Text = "Source code", AutoSize = true, Location = new Point(200, 204) };
            source.LinkClicked += (s, e) => Program.OpenUrl(Program.SourceUrl);
            var donate = new Button { Text = "Donate ♥", AutoSize = true, Location = new Point(16, 236) };
            donate.Click += (s, e) => { Close(); DonateForm.Open(null); };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(250, 236) };
            Controls.AddRange(new Control[] { text, site, source, donate, close });
            Controls.Add(header);
            AcceptButton = close;
            CancelButton = close;
            Theme.Attach(this, null);
        }
    }
}
