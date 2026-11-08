using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Buzzcord
{
    // The strip at the bottom of the buddy list while that account is in a call: where you are,
    // who's there (lit up while talking), and Mute / Deafen / Leave.
    public class VoiceBar : BufferedPanel
    {
        readonly Session session;
        readonly Timer refresh = new Timer { Interval = 150 };
        readonly ToolTip tip = new ToolTip();
        readonly List<KeyValuePair<Rectangle, string>> hits = new List<KeyValuePair<Rectangle, string>>();   // area -> "mute", "deafen", "leave", "lock", or "u<id>"
        static readonly Font fTitle = new Font("Tahoma", 8.25f, FontStyle.Bold), fText = new Font("Tahoma", 7.5f), fButton = new Font("Tahoma", 7.5f, FontStyle.Bold);
        const TextFormatFlags Flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        string lastTip;

        public VoiceBar(Session session)
        {
            this.session = session;
            Dock = DockStyle.Bottom;
            Height = 0;
            Visible = false;
            refresh.Tick += (o, e) => Invalidate();
            Action changed = () => { if (!IsDisposed) Sync(); };
            VoiceManager.Changed += changed;
            Disposed += (o, e) => { VoiceManager.Changed -= changed; refresh.Dispose(); tip.Dispose(); };
            MouseClick += OnClick;
            MouseMove += OnMove;
            Sync();
        }

        VoiceCall Call
        {
            get
            {
                var c = VoiceManager.Current;
                return c != null && c.Session == session ? c : null;
            }
        }

        void Sync()
        {
            bool show = Call != null;
            Visible = show;
            Height = show ? 74 : 0;
            refresh.Enabled = show;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var b = ClientRectangle;
            using (var br = new LinearGradientBrush(b, Theme.Window, Theme.Panel, 90f)) g.FillRectangle(br, b);
            using (var pen = new Pen(Icons.PurpleLight)) g.DrawLine(pen, 0, 0, b.Width, 0);
            hits.Clear();
            var call = Call;
            if (call == null) return;

            bool ok = call.Connected && !call.Securing;
            var stateColor = ok ? Color.FromArgb(0x2E, 0xA0, 0x43) : call.Error != null ? Color.FromArgb(0xD0, 0x30, 0x30) : Color.FromArgb(0xC0, 0x8A, 0x10);
            DrawSpeaker(g, new Rectangle(8, 6, 14, 14), stateColor);
            string state = call.Error ?? (call.Securing ? "Securing the call..." : call.State) + (call.Connected && call.PingMs >= 0 ? "  ·  " + call.PingMs + " ms" : "");
            TextRenderer.DrawText(g, state, fTitle, new Rectangle(26, 6, b.Width - 60, 14), stateColor, Flags);
            if (call.Encrypted)
            {
                var lockRect = new Rectangle(b.Width - 24, 5, 16, 16);
                DrawLock(g, lockRect, Theme.SubText);
                hits.Add(new KeyValuePair<Rectangle, string>(lockRect, "lock"));
            }
            TextRenderer.DrawText(g, call.Title, fText, new Rectangle(26, 21, b.Width - 34, 13), Theme.SubText, Flags);

            // Buttons on the right, avatars on the left of the bottom row.
            int y = 40, bh = 26;
            int right = b.Width - 8;
            right = Button(g, ref right, y, bh, "Leave", "leave", Color.FromArgb(0xC8, 0x3A, 0x3A), Color.White);
            Button(g, ref right, y, bh, call.Deafened ? "Undeafen" : "Deafen", "deafen", call.Deafened ? Color.FromArgb(0xD0, 0x30, 0x30) : Theme.Window, call.Deafened ? Color.White : Theme.Text);
            Button(g, ref right, y, bh, call.Muted ? "Unmute" : "Mute", "mute", call.Muted ? Color.FromArgb(0xD0, 0x30, 0x30) : Theme.Window, call.Muted ? Color.White : Theme.Text);

            int x = 8, size = 24;
            foreach (var uid in call.Participants)
            {
                if (x + size > right - 4) break;
                var rect = new Rectangle(x, y + 1, size, size);
                var img = ImageCache.Scaled(session.GetUser(uid).AvatarUrl(64), size, Invalidate);
                using (var path = Circle(rect))
                {
                    if (img != null) { g.SetClip(path); g.DrawImage(img, rect); g.ResetClip(); }
                    else using (var br = new SolidBrush(Theme.Placeholder)) g.FillPath(br, path);
                }
                if (call.IsSpeaking(uid))
                    using (var pen = new Pen(Color.FromArgb(0x2E, 0xC0, 0x4F), 2.5f)) g.DrawEllipse(pen, rect.X - 1, rect.Y - 1, size + 2, size + 2);
                VoiceState vs;
                if (session.VoiceStates.TryGetValue(uid, out vs) && (vs.SelfMute || vs.Mute || vs.SelfDeaf || vs.Deaf))
                    using (var br = new SolidBrush(Color.FromArgb(0xD0, 0x30, 0x30))) g.FillEllipse(br, rect.Right - 8, rect.Bottom - 8, 9, 9);
                hits.Add(new KeyValuePair<Rectangle, string>(rect, "u" + uid));
                x += size + 5;
            }
        }

        int Button(Graphics g, ref int right, int y, int h, string text, string id, Color back, Color fore)
        {
            int w = TextRenderer.MeasureText(text, fButton).Width + 10;
            var r = new Rectangle(right - w, y, w, h);
            using (var path = Rounded(r, 4))
            {
                using (var br = new SolidBrush(back)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, fButton, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            hits.Add(new KeyValuePair<Rectangle, string>(r, id));
            right -= w + 4;
            return right;
        }

        string HitAt(Point p)
        {
            foreach (var h in hits) if (h.Key.Contains(p)) return h.Value;
            return null;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            var hit = HitAt(e.Location);
            Cursor = hit != null && hit != "lock" ? Cursors.Hand : Cursors.Default;
            var call = Call;
            string text = null;
            if (hit == "lock" && call != null) text = "End-to-end encrypted (DAVE).\nVoice privacy code: " + call.PrivacyCode + "\nIt matches the code everyone else in the call sees.";
            else if (hit != null && hit.StartsWith("u")) text = session.NameOf(hit.Substring(1));
            if (text != lastTip) { lastTip = text; tip.SetToolTip(this, text); }
        }

        void OnClick(object sender, MouseEventArgs e)
        {
            var call = Call;
            var hit = HitAt(e.Location);
            if (call == null || hit == null) return;
            if (hit == "mute") call.SetMuted(!call.Muted);
            else if (hit == "deafen") call.SetDeafened(!call.Deafened);
            else if (hit == "leave") call.Leave();
            else if (hit.StartsWith("u")) ShowUserMenu(call, hit.Substring(1), e.Location);
        }

        void ShowUserMenu(VoiceCall call, string uid, Point at)
        {
            var menu = new ContextMenuStrip();
            var profile = new ToolStripMenuItem("View profile", null, (o, e) => ProfileForm.Open(session, uid, call.GuildId));
            profile.Font = new Font(profile.Font, FontStyle.Bold);
            menu.Items.Add(profile);
            if (session.Me == null || uid != session.Me.Id) AddVolumeItem(menu, call, uid, session);
            menu.Show(this, at);
        }

        // A "Volume" slider inside a menu, 0-200%.
        public static void AddVolumeItem(ContextMenuStrip menu, VoiceCall call, string uid, Session session)
        {
            int current = Settings.GetInt("VoiceVolume." + uid, 100);
            var label = new ToolStripLabel("Volume: " + current + "%");
            var slider = new TrackBar { Minimum = 0, Maximum = 200, TickFrequency = 50, Value = current, Width = 160, AutoSize = false, Height = 28, BackColor = Theme.Window };
            slider.ValueChanged += (o, e) => { label.Text = "Volume: " + slider.Value + "%"; call.SetVolume(uid, slider.Value); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(label);
            menu.Items.Add(new ToolStripControlHost(slider));
        }

        static GraphicsPath Circle(Rectangle r)
        {
            var p = new GraphicsPath();
            p.AddEllipse(r);
            return p;
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // A little loudspeaker with two sound waves.
        public static void DrawSpeaker(Graphics g, Rectangle r, Color c)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = r.Width / 14f;
            using (var br = new SolidBrush(c))
                g.FillPolygon(br, new[]
                {
                    new PointF(r.X + 1 * s, r.Y + 5 * s), new PointF(r.X + 4 * s, r.Y + 5 * s), new PointF(r.X + 8 * s, r.Y + 1.5f * s),
                    new PointF(r.X + 8 * s, r.Y + 12.5f * s), new PointF(r.X + 4 * s, r.Y + 9 * s), new PointF(r.X + 1 * s, r.Y + 9 * s),
                });
            using (var pen = new Pen(c, Math.Max(1.2f, 1.3f * s)))
            {
                g.DrawArc(pen, r.X + 7 * s, r.Y + 4.5f * s, 4 * s, 5 * s, -60, 120);
                g.DrawArc(pen, r.X + 7.5f * s, r.Y + 2.5f * s, 6 * s, 9 * s, -60, 120);
            }
            g.SmoothingMode = old;
        }

        static void DrawLock(Graphics g, Rectangle r, Color c)
        {
            using (var pen = new Pen(c, 1.6f)) g.DrawArc(pen, r.X + 4, r.Y + 1, r.Width - 8, r.Height - 6, 180, 180);
            using (var br = new SolidBrush(c)) g.FillRectangle(br, r.X + 2, r.Y + r.Height / 2 - 1, r.Width - 4, r.Height / 2 + 1);
        }
    }
}
