using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // The message alert by the clock, with a reply box: Windows' tray balloons can't hold text or
    // buttons, so message alerts use this small pop-up instead (other alerts keep the balloon).
    // It never takes focus by itself; click into it to type. Reply answers in the chat (in a
    // server channel, as a reply to the alerted message), Mark as Read clears it, Open opens it.
    public class QuickReplyForm : Form
    {
        static readonly List<QuickReplyForm> open = new List<QuickReplyForm>();
        const int MaxOpen = 3;

        readonly BuddyListForm from;
        readonly string channelId, messageId, title;
        readonly TextBox box = new TextBox();
        readonly Button reply = new Button { Text = "Reply" };
        readonly Timer closeTimer = new Timer { Interval = 12000 };
        bool sending;

        public QuickReplyForm(BuddyListForm from, string title, string text, string channelId, string messageId, string account)
        {
            this.from = from;
            this.channelId = channelId;
            this.messageId = messageId;
            this.title = title;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(330, 150);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Theme.Window;
            Padding = new Padding(1);

            var header = new Panel { Dock = DockStyle.Top, Height = 26, Cursor = Cursors.Hand };
            header.Paint += (s, e) =>
            {
                Icons.FillHeader(e.Graphics, header.ClientRectangle);
                e.Graphics.DrawImage(Icons.Status("online", 16), 6, 5, 16, 16);
                var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
                using (var f = new Font("Tahoma", 8.25f, FontStyle.Bold))
                    e.Graphics.DrawString((account != null ? "[" + account + "] " : "") + title, f, Brushes.Black, new RectangleF(26, 6, header.Width - 52, 16), fmt);
            };
            header.Click += (s, e) => OpenChat();
            var close = new Label { Text = "×", Font = new Font("Arial", 11f, FontStyle.Bold), AutoSize = false, Size = new Size(22, 24), Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, BackColor = Color.Transparent };
            close.Click += (s, e) => Close();
            header.Controls.Add(close);

            var message = new Label
            {
                Text = string.IsNullOrEmpty(text) ? "(sent a file)" : text.Replace("\r", " ").Replace("\n", " "),
                Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), AutoEllipsis = true,
                ForeColor = Theme.Text, Cursor = Cursors.Hand
            };
            message.Click += (s, e) => OpenChat();

            box.Location = new Point(8, 76);
            box.Width = Width - 18;
            box.MaxLength = 2000;
            box.BackColor = Theme.Window;
            box.ForeColor = Theme.Text;
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendReply(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            };
            box.HandleCreated += (s, e) => Native.SetCueBanner(box, "Type a reply and press Enter");

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4, 2, 4, 2) };
            var openButton = new Button { Text = "Open", AutoSize = true };
            var read = new Button { Text = "Mark as Read", AutoSize = true };
            reply.AutoSize = true;
            reply.Click += (s, e) => SendReply();
            read.Click += (s, e) => { MarkRead(); Close(); };
            openButton.Click += (s, e) => OpenChat();
            buttons.Controls.AddRange(new Control[] { openButton, read, reply });

            Controls.Add(box);
            Controls.Add(buttons);
            Controls.Add(message);
            Controls.Add(header);
            Theme.Apply(this);

            // Stays while you're pointing at it or typing; otherwise goes after 12 seconds.
            closeTimer.Tick += (s, e) =>
            {
                if (sending || ContainsFocus || box.TextLength > 0 || Bounds.Contains(Cursor.Position)) return;
                Close();
            };
            closeTimer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        public static void Show(BuddyListForm from, string title, string text, string channelId, string messageId, string account)
        {
            // A new alert for the same chat replaces the old one, unless you're typing in it.
            foreach (var f in open.ToArray())
                if (f.from == from && f.channelId == channelId && f.box.TextLength == 0 && !f.sending) f.Close();
            while (open.Count >= MaxOpen)
            {
                var oldest = open.Find(f => f.box.TextLength == 0 && !f.sending) ?? open[0];
                oldest.Close();
            }
            var form = new QuickReplyForm(from, title, text, channelId, messageId, account);
            open.Add(form);
            form.FormClosed += (s, e) => { open.Remove(form); Arrange(); };
            Arrange();
            form.Show();
        }

        // Stacked upward from the bottom-right corner, by the clock.
        static void Arrange()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int y = wa.Bottom - 8;
            foreach (var f in open)
            {
                y -= f.Height;
                f.Location = new Point(wa.Right - f.Width - 8, y);
                y -= 6;
            }
        }

        Session Session { get { return from.Session; } }

        void OpenChat()
        {
            if (from.IsDisposed) return;
            from.OpenChatById(channelId);
            Close();
        }

        void MarkRead()
        {
            var c = Session.FindChannel(channelId);
            if (c != null) Session.MarkRead(c);
        }

        async void SendReply()
        {
            string text = box.Text.Trim();
            if (text.Length == 0) { box.Focus(); return; }
            if (sending) return;
            var c = Session.FindChannel(channelId);
            sending = true;
            box.Enabled = reply.Enabled = false;
            try
            {
                await Session.Api.SendMessage(channelId, text, Session.ReplyTargetFor(c, messageId), null);
                if (c != null) Session.MarkRead(c);
                Close();
            }
            catch (Exception ex)
            {
                Program.Log("quick reply: " + ex.Message);
                Close();
                // Nothing typed is lost: the alert says what failed and shows the text.
                MessageBox.Show("Your reply to " + title + " wasn't sent.\n\n" + ex.Message + "\n\nWhat you typed:\n" + text,
                    "Buzzcord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) closeTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
