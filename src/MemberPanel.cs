using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // "Who's here" beside a server channel or group chat: Discord's member list for servers
    // (role groups, then Online/Offline, coloured names), or the group's members, online first.
    public class MemberPanel : Panel
    {
        class Row
        {
            public bool Header;
            public string Text, UserId, Status = "offline", Activity, Key;
            public int Color;
        }

        readonly Session s;
        readonly Channel channel;
        readonly RowList list = new RowList { Dock = DockStyle.Fill };
        readonly Label title = new Label { Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        readonly Timer debounce = new Timer { Interval = 300 };
        readonly DateTime opened = DateTime.UtcNow;
        static readonly Font fName = new Font("Tahoma", 8.25f), fSmall = new Font("Tahoma", 7.5f), fHeader = new Font("Tahoma", 7f, FontStyle.Bold);
        const TextFormatFlags Flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        public MemberPanel(Session s, Channel channel)
        {
            this.s = s;
            this.channel = channel;
            BackColor = Icons.Panel;
            Padding = new Padding(4, 4, 6, 4);
            title.Font = new Font("Tahoma", 8.25f, FontStyle.Bold);
            title.ForeColor = Icons.Purple;
            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(list);
            Controls.Add(border);
            Controls.Add(title);

            list.RowHeight = r => ((Row)r).Header ? 20 : 34;
            list.RowKey = r => ((Row)r).Key;
            list.DrawRow += (g, r, b, sel, hov) => Draw(g, (Row)r, b, sel, hov);
            list.RowClick += (r, e) =>
            {
                var row = (Row)r;
                if (row.UserId != null) ProfileForm.Open(s, row.UserId, channel.GuildId);
            };
            list.MouseMove += (o, e) =>
            {
                var row = list.RowAt(e.Location) as Row;
                list.Cursor = row != null && row.UserId != null ? Cursors.Hand : Cursors.Default;
            };

            debounce.Tick += (o, e) => { debounce.Stop(); Rebuild(); };
            Action<string> onList = gid => { if (gid == channel.GuildId) Later(); };
            Action<string> onPresence = uid => { if (channel.IsGroup && (channel.RecipientIds.Contains(uid) || s.Me != null && uid == s.Me.Id)) Later(); };
            s.MemberListChanged += onList;
            s.PresenceChanged += onPresence;
            Disposed += (o, e) => { s.MemberListChanged -= onList; s.PresenceChanged -= onPresence; debounce.Dispose(); };
            if (channel.GuildId != null)
            {
                // If Discord never answers, say so instead of "Loading..." forever.
                var giveUp = new Timer { Interval = 8000 };
                giveUp.Tick += (o, e) => { giveUp.Dispose(); if (!IsDisposed) Rebuild(); };
                giveUp.Start();
            }
            Rebuild();
        }

        void Later()
        {
            if (!debounce.Enabled) debounce.Start();
        }

        public void Rebuild()
        {
            var rows = new List<object>();
            if (channel.IsGroup) BuildGroup(rows);
            else BuildServer(rows);
            list.SetRows(rows);
        }

        void BuildGroup(List<object> rows)
        {
            var ids = new List<string>(channel.RecipientIds);
            if (s.Me != null && !ids.Contains(s.Me.Id)) ids.Add(s.Me.Id);
            var online = new List<string>();
            var offline = new List<string>();
            foreach (var id in ids) (s.PresenceOf(id).Status != "offline" ? online : offline).Add(id);
            Comparison<string> byName = (a, b) => string.Compare(s.NameOf(a), s.NameOf(b), StringComparison.CurrentCultureIgnoreCase);
            online.Sort(byName);
            offline.Sort(byName);
            title.Text = "Members — " + ids.Count;
            if (online.Count > 0) rows.Add(new Row { Header = true, Text = "ONLINE — " + online.Count, Key = "h-on" });
            foreach (var id in online) rows.Add(Member(id, null, 0));
            if (offline.Count > 0) rows.Add(new Row { Header = true, Text = "OFFLINE — " + offline.Count, Key = "h-off" });
            foreach (var id in offline) rows.Add(Member(id, null, 0));
        }

        Row Member(string id, string nick, int color)
        {
            var p = s.PresenceOf(id);
            return new Row { UserId = id, Text = nick ?? s.NameOf(id), Status = p.Status, Activity = p.Summary, Color = color, Key = "u" + id };
        }

        void BuildServer(List<object> rows)
        {
            var g = s.GuildOf(channel);
            var ml = s.MembersFor(channel);
            if (ml == null)
            {
                bool waited = DateTime.UtcNow - opened > TimeSpan.FromSeconds(7);
                title.Text = "Members";
                rows.Add(new Row { Header = true, Text = waited ? "DISCORD DIDN'T SEND THE LIST" : "LOADING MEMBERS...", Key = "loading" });
                return;
            }
            title.Text = ml.OnlineCount > 0 ? ml.OnlineCount + " online" + (ml.MemberCount > 0 ? " of " + ml.MemberCount : "") : "Members";
            int shown = 0;
            foreach (var item in ml.Items)
            {
                if (item == null) continue;
                var grp = item.Obj("group");
                if (grp != null)
                {
                    string gid = grp.Str("id");
                    string name = gid == "online" ? "Online" : gid == "offline" ? "Offline" : null;
                    Role r;
                    if (name == null) name = g != null && g.Roles.TryGetValue(gid ?? "", out r) ? r.Name : "Members";
                    rows.Add(new Row { Header = true, Text = (name ?? "").ToUpperInvariant() + " — " + grp.Long("count"), Key = "g" + gid });
                    continue;
                }
                var m = item.Obj("member");
                var u = m != null ? m.Obj("user") : null;
                if (u == null || u.Str("id") == null) continue;
                var row = Member(u.Str("id"), m.Str("nick"), g != null ? g.ColorFor(m.Arr("roles")) : 0);
                var pres = m.Obj("presence");
                if (pres != null && (s.Me == null || u.Str("id") != s.Me.Id))
                {
                    var p = Presence.From(pres);
                    row.Status = p.Status;
                    row.Activity = p.Summary;
                }
                rows.Add(row);
                shown++;
            }
            if (ml.OnlineCount > shown && shown >= 90)
                rows.Add(new Row { Header = true, Text = "AND " + (ml.OnlineCount - shown) + " MORE ONLINE", Key = "more" });
        }

        static void Blit(Graphics g, Image img, int x, int y) { g.DrawImage(img, new Rectangle(x, y, img.Width, img.Height)); }

        void Draw(Graphics g, Row r, Rectangle b, bool selected, bool hover)
        {
            if (r.Header)
            {
                using (var br = new SolidBrush(Theme.Window)) g.FillRectangle(br, b);
                TextRenderer.DrawText(g, r.Text, fHeader, new Rectangle(b.X + 6, b.Y + 6, b.Width - 10, 13), Theme.SubText, Flags);
                return;
            }
            using (var br = new SolidBrush(selected ? Theme.Selected : hover ? Theme.Hover : Theme.Window)) g.FillRectangle(br, b);
            int avatar = 26;
            var rect = new Rectangle(b.X + 4, b.Y + 4, avatar, avatar);
            var img = ImageCache.Scaled(s.GetUser(r.UserId).AvatarUrl(64), avatar, list.Invalidate);
            if (img != null) Blit(g, img, rect.X, rect.Y);
            else using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, rect);
            // Status smiley over the avatar's corner, like a presence dot.
            Blit(g, Icons.Status(r.Status, 12), rect.Right - 9, rect.Bottom - 9);
            bool offline = r.Status == "offline";
            Color name = r.Color != 0 ? Color.FromArgb(255, Color.FromArgb(r.Color)) : Theme.Text;
            if (offline) name = Theme.SubText;
            int x = rect.Right + 6, w = b.Right - x - 4;
            TextRenderer.DrawText(g, r.Text, fName, new Rectangle(x, b.Y + (r.Activity != null ? 3 : 9), w, 15), name, Flags);
            if (r.Activity != null)
                TextRenderer.DrawText(g, r.Activity, fSmall, new Rectangle(x, b.Y + 18, w, 14), Theme.SubText, Flags);
        }
    }
}
