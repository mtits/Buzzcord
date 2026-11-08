using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Buzzcord
{
    // Searches a conversation's history with Discord's own search (the same index the official
    // app uses), so it finds messages from years ago, not just what's loaded.
    public class SearchForm : Form
    {
        const int PageSize = 25;

        readonly Session s;
        readonly Channel channel;
        readonly TextBox box = new TextBox { Dock = DockStyle.Fill, Font = new Font("Arial", 10f) };
        readonly CheckBox wholeServer = new CheckBox { Text = "Whole server", AutoSize = true, Dock = DockStyle.Right };
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Padding = new Padding(6, 0, 0, 0) };
        string query;
        int offset, total;
        bool pageReady, busy;

        public SearchForm(Session s, Channel channel, Form owner)
        {
            this.s = s;
            this.channel = channel;
            Text = "Search " + s.ChannelTitle(channel);
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            ClientSize = new Size(520, 560);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(owner.Right - 40, owner.Top + 20);
            if (!Screen.FromControl(owner).WorkingArea.Contains(new Rectangle(Location, Size))) StartPosition = FormStartPosition.CenterScreen;

            var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 6, 6, 4) };
            var go = new Button { Text = "Search", Dock = DockStyle.Right, Width = 70 };
            go.Click += (o, e) => Start();
            top.Controls.Add(box);
            if (channel.GuildId != null) { top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 }); top.Controls.Add(wholeServer); }
            top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
            top.Controls.Add(go);
            AcceptButton = go;
            box.HandleCreated += (o, e) => Native.SetCueBanner(box, "Words to find...");

            web.ScriptErrorsSuppressed = true;
            web.AllowWebBrowserDrop = false;
            web.Navigating += Navigating;
            web.NewWindow += (o, e) => e.Cancel = true;
            web.DocumentCompleted += (o, e) => pageReady = true;
            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(web);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0) };
            host.Controls.Add(border);

            Controls.Add(host);
            Controls.Add(status);
            Controls.Add(top);
            web.DocumentText = MessageHtml.Page;
            Theme.Attach(this, () => Script("setTheme", Theme.ChatCss()));
            status.Text = "Type something and press Enter.";
        }

        void Start()
        {
            query = box.Text.Trim();
            if (query.Length == 0 || busy) return;
            offset = 0;
            Script("clearLog");
            Script("setOlder", "");
            Fetch();
        }

        async void Fetch()
        {
            busy = true;
            status.Text = "Searching...";
            try
            {
                string path = channel.GuildId != null
                    ? "/guilds/" + channel.GuildId + "/messages/search?content=" + Uri.EscapeDataString(query) + (wholeServer.Checked ? "" : "&channel_id=" + channel.Id)
                    : "/channels/" + channel.Id + "/messages/search?content=" + Uri.EscapeDataString(query);
                if (offset > 0) path += "&offset=" + offset;
                var r = await s.Api.Get(path) as JObj;
                var groups = r != null ? r.Arr("messages") : null;
                if (groups == null)
                {
                    // 202: Discord is still building the search index for this chat.
                    status.Text = "Discord is still indexing this chat. Try again in a few seconds.";
                    return;
                }
                total = (int)r.Long("total_results");
                var sb = new StringBuilder();
                foreach (var g in groups)
                {
                    var group = g as List<object>;
                    if (group == null) continue;
                    JObj hit = null;
                    foreach (var o in group)
                    {
                        var m = o as JObj;
                        if (m != null && (hit == null || m.Bool("hit"))) hit = m;
                    }
                    if (hit == null) continue;
                    var c = s.FindChannel(hit.Str("channel_id"));
                    string where = Snowflake.Time(hit.Str("id")).ToString("MMM d, yyyy");
                    if (c != null && c.Id != channel.Id) where += " in #" + c.Name;
                    sb.Append("<div class=\"day\" style=\"text-align:left\">").Append(MessageHtml.E(where)).Append("</div>");
                    sb.Append(MessageHtml.Render(s, hit));
                }
                offset += groups.Count;
                Script("append", sb.ToString());
                status.Text = total == 0 ? "Nothing found." : "Showing " + offset + " of " + total + " result" + (total == 1 ? "" : "s") + ".";
                Script("setOlder", "");
                if (offset < total) Script("append", "<div id=\"more\" style=\"text-align:center;margin:8px\"><a href=\"app://more/\">Show more results</a></div>");
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; }
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
            if (url.StartsWith("app://more", StringComparison.OrdinalIgnoreCase))
            {
                Script("removeMsg", "more");
                Fetch();
                return;
            }
            if (url.StartsWith("app://profile/", StringComparison.OrdinalIgnoreCase))
            {
                ProfileForm.Open(s, Uri.UnescapeDataString(url.Substring(14)), channel.GuildId);
                return;
            }
            if (url.StartsWith("app://open/", StringComparison.OrdinalIgnoreCase)) url = Uri.UnescapeDataString(url.Substring(11));
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }
    }
}
