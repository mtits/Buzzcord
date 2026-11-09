using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Block and unblock, Remove from Recent, and Messenger > Blocked users. Discord keeps the block
    // list; it arrives with the other relationships (type 2) when Buzzcord connects. Discord never
    // says who blocked you. Mirrors mac/Sources/Buzzcord/Blocking.swift.
    public static class Blocking
    {
        public const string Explanation = "They won't be able to message or call you, and their messages in servers and group chats are hidden. If you're friends, this also removes them as a friend. They aren't told.";

        public static string Question(Session s, string userId)
        {
            return "Block " + s.NameOf(userId) + "?\n\n" + Explanation;
        }

        // Asks first. A failure is logged and shown with the reason.
        public static async void Block(IWin32Window owner, Session s, string userId, string location)
        {
            if (MessageBox.Show(owner, Question(s, userId), "Buzzcord", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            await Run(owner, s, "block", userId, () => s.Block(userId, location));
        }

        // True when it worked.
        public static Task<bool> Unblock(IWin32Window owner, Session s, string userId, string location)
        {
            return Run(owner, s, "unblock", userId, () => s.Unblock(userId, location));
        }

        static async Task<bool> Run(IWin32Window owner, Session s, string what, string userId, Func<Task> op)
        {
            Program.Log("relationship: " + what + " " + userId);
            Exception error = null;
            try { await op(); }
            catch (Exception ex) { error = ex; }
            if (error == null) return true;
            Program.Log("relationship: " + what + " " + userId + " failed: " + error);
            Show(owner, ErrorText(what, s.NameOf(userId), error));
            return false;
        }

        // Takes a one-to-one chat off Recent chats right away; puts it back and says why if Discord refuses.
        public static async void RemoveFromRecent(IWin32Window owner, Session s, Channel c)
        {
            Program.Log("recent: close " + c.Id);
            Exception error = null;
            try { await s.CloseDm(c); }
            catch (Exception ex) { error = ex; }
            if (error == null) return;
            Program.Log("recent: close " + c.Id + " failed: " + error);
            Show(owner, "Couldn't remove " + s.ChannelTitle(c) + " from Recent chats.\n\n" + Reason(error));
        }

        static void Show(IWin32Window owner, string text)
        {
            var c = owner as Control;
            if (c != null && c.IsDisposed) owner = null;
            MessageBox.Show(owner, text, "Buzzcord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static string ErrorText(string what, string name, Exception ex)
        {
            return "Couldn't " + what + " " + name + ".\n\n" + Reason(ex);
        }

        // Why Discord refused, in words.
        public static string Reason(Exception ex)
        {
            var api = ex as ApiException;
            if (api == null && ex is AggregateException) api = ex.GetBaseException() as ApiException;
            if (api != null && api.IsCaptcha) return "Discord asked for a captcha and it wasn't solved. Try again and finish the captcha.";
            if (api != null && api.Status == 401) return "Discord signed this account out. Sign in again.";
            return ex.GetBaseException().Message ?? "unknown error";
        }

        // Remove from Recent is offered for a one-to-one chat in Recent chats only: never a group
        // (closing one means leaving it), and not while it's pinned to Favorites.
        public static bool CanRemoveFromRecent(Session s, Channel c, bool inRecent)
        {
            return inRecent && c != null && c.IsDm && s.PrivateChannels.ContainsKey(c.Id) && !Favorites.IsPinned(s, c.Id);
        }
    }

    // Messenger > Blocked users: everyone this account blocked, by name, with Unblock.
    public class BlockedForm : Form
    {
        static readonly Dictionary<string, BlockedForm> open = new Dictionary<string, BlockedForm>();

        readonly Session s;
        readonly Label header = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        readonly FlowLayoutPanel rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        string shown;

        public static void Open(Session s)
        {
            BlockedForm f;
            if (!open.TryGetValue(s.TokenKey, out f) || f.IsDisposed)
            {
                f = new BlockedForm(s);
                string key = s.TokenKey;
                open[key] = f;
                f.FormClosed += (o, e) => open.Remove(key);
            }
            f.Show();
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.Activate();
        }

        BlockedForm(Session s)
        {
            this.s = s;
            Text = "Blocked Users" + (s.Me != null ? " - " + s.Me.DisplayName : "");
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            Size = new Size(380, 440);
            MinimumSize = new Size(300, 260);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            header.Font = new Font(Font, FontStyle.Bold);
            var footer = new Label
            {
                Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 6, 10, 6),
                Text = "Block someone from their profile or by right-clicking them in the buddy list. Unblocking doesn't make you friends again."
            };
            rows.Resize += (o, e) => { foreach (Control c in rows.Controls) c.Width = RowWidth; };
            Controls.Add(rows);
            Controls.Add(header);
            Controls.Add(footer);

            Action changed = () => { if (!IsDisposed) Rebuild(false); };
            s.ListChanged += changed;
            FormClosed += (o, e) => s.ListChanged -= changed;
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Action colors = () =>
            {
                BackColor = Theme.Panel;
                rows.BackColor = Theme.Window;
                header.ForeColor = Icons.Purple;
                footer.ForeColor = Theme.SubText;
            };
            colors();
            Theme.Attach(this, () => { colors(); Rebuild(true); });
            Theme.Apply(this);
            Rebuild(true);
        }

        int RowWidth { get { return Math.Max(200, rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2); } }

        void Rebuild(bool force)
        {
            var ids = s.BlockedUsers();
            var sig = new StringBuilder();
            foreach (var id in ids) sig.Append(id).Append('=').Append(s.NameOf(id)).Append(';');
            if (!force && sig.ToString() == shown) return;
            shown = sig.ToString();
            header.Text = ids.Count == 0 ? "You haven't blocked anyone." : "Blocked (" + ids.Count + ")";
            rows.SuspendLayout();
            while (rows.Controls.Count > 0) rows.Controls[0].Dispose();
            foreach (var id in ids) rows.Controls.Add(MakeRow(id));
            rows.ResumeLayout();
        }

        Control MakeRow(string id)
        {
            var u = s.GetUser(id);
            var row = new Panel { Width = RowWidth, Height = 42, Margin = new Padding(0), BackColor = Theme.Window };
            var pic = new PictureBox { Location = new Point(10, 6), Size = new Size(30, 30), SizeMode = PictureBoxSizeMode.StretchImage, BackColor = Icons.GroupBack };
            string url = u.AvatarUrl(64);
            pic.Image = ImageCache.Scaled(url, 30, () => { if (!pic.IsDisposed) pic.Image = ImageCache.Scaled(url, 30, null); });
            var name = new Label { Text = s.NameOf(id), Location = new Point(48, 6), AutoSize = false, Height = 15, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true };
            var user = new Label { Text = u.Username ?? "", Location = new Point(48, 22), AutoSize = false, Height = 15, ForeColor = Theme.SubText, AutoEllipsis = true };
            var unblock = new Button { Text = "Unblock", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            unblock.Click += async (o, e) =>
            {
                unblock.Enabled = false;
                // On success Discord's RELATIONSHIP_REMOVE takes the row away.
                if (!await Blocking.Unblock(this, s, id, "Blocked Users") && !unblock.IsDisposed) unblock.Enabled = true;
            };
            Action layout = () =>
            {
                unblock.Location = new Point(row.Width - unblock.Width - 10, (row.Height - unblock.Height) / 2);
                name.Width = user.Width = Math.Max(20, unblock.Left - 54);
            };
            row.Resize += (o, e) => layout();
            row.Controls.Add(pic);
            row.Controls.Add(name);
            row.Controls.Add(user);
            row.Controls.Add(unblock);
            layout();
            var menu = new ContextMenuStrip();
            menu.Items.Add("View profile", null, (o, e) => ProfileForm.Open(s, id, null));
            row.ContextMenuStrip = pic.ContextMenuStrip = name.ContextMenuStrip = user.ContextMenuStrip = menu;
            row.Paint += (o, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
            return row;
        }
    }
}
