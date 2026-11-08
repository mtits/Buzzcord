using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Sign in by scanning a QR code with the Discord phone app, or by pasting a token.
    public class LoginForm : Form
    {
        readonly PictureBox qr = new PictureBox { Size = new Size(232, 232), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
        readonly Label status = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        readonly Button newCode = new Button { Text = "New code", AutoSize = true };
        readonly LinkLabel useToken = new LinkLabel { Text = "Use a token instead", AutoSize = true };
        readonly TextBox tokenBox = new TextBox { UseSystemPasswordChar = true, Visible = false };
        readonly Button tokenOk = new Button { Text = "Sign in", Visible = false, AutoSize = true };
        RemoteAuth auth;

        public string Token { get; private set; }

        public LoginForm()
        {
            Text = "Buzzcord - Sign in";
            Icon = Icons.AppIcon(32);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(300, 470);
            BackColor = Icons.Panel;
            Font = new Font("Tahoma", 8.25f);

            var header = new Panel { Dock = DockStyle.Top, Height = 64 };
            header.Paint += (s, e) =>
            {
                Icons.FillHeader(e.Graphics, header.ClientRectangle);
                using (var logo = Icons.AppBitmap(44)) e.Graphics.DrawImage(logo, 12, 10);
                using (var f = new Font("Arial", 15f, FontStyle.Bold | FontStyle.Italic))
                using (var br = new SolidBrush(Icons.Purple))
                    e.Graphics.DrawString("Buzzcord", f, br, 62, 10);
                using (var f = new Font("Tahoma", 8f))
                    using (var br = new SolidBrush(Theme.Dim)) e.Graphics.DrawString("messenger for Discord", f, br, 64, 38);
            };
            Controls.Add(header);

            var help = new Label
            {
                Text = "On your phone, open Discord, tap your profile picture, then Scan QR Code, and point it at this code.",
                Location = new Point(16, 74), Size = new Size(268, 44)
            };
            Controls.Add(help);

            qr.Location = new Point(34, 120);
            qr.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(qr);

            status.Location = new Point(12, 358);
            status.Size = new Size(276, 34);
            Controls.Add(status);

            newCode.Location = new Point(110, 394);
            newCode.Click += (s, e) => StartQr();
            Controls.Add(newCode);

            useToken.Location = new Point(96, 432);
            useToken.LinkClicked += (s, e) => ShowTokenBox();
            Controls.Add(useToken);

            tokenBox.Location = new Point(16, 400);
            tokenBox.Width = 190;
            Controls.Add(tokenBox);
            tokenOk.Location = new Point(212, 398);
            tokenOk.Click += async (s, e) => await TryToken(tokenBox.Text.Trim().Trim('"'));
            Controls.Add(tokenOk);
            AcceptButton = tokenOk;

            Shown += (s, e) => StartQr();
            FormClosed += (s, e) => { if (auth != null) auth.Dispose(); };
            Theme.Attach(this, null);
        }

        void ShowTokenBox()
        {
            newCode.Visible = false;
            useToken.Visible = false;
            tokenBox.Visible = true;
            tokenOk.Visible = true;
            status.Text = "Paste your own token, then Sign in. Never paste one someone sent you.";
            tokenBox.Focus();
        }

        void StartQr()
        {
            if (auth != null) auth.Dispose();
            status.Text = "Getting a code from Discord...";
            status.ForeColor = Theme.Text;
            qr.Image = null;
            auth = new RemoteAuth();
            var me = auth;
            auth.QrReady += url => UI(() =>
            {
                if (me != auth) return;
                var code = QrCode.Encode(url);
                int scale = Math.Max(2, 224 / (code.Size + 8));
                qr.Image = code.ToBitmap(scale, 4);
                status.Text = "Waiting for you to scan...";
            });
            auth.Scanned += (name, avatar) => UI(() =>
            {
                if (me != auth) return;
                status.Text = "Hi " + name + "! Confirm the login on your phone.";
                ImageCache.Get(avatar, img =>
                {
                    var bmp = new Bitmap(128, 128);
                    using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, 128, 128);
                    qr.Image = bmp;
                });
            });
            auth.Succeeded += token => UI(async () => { if (me == auth) await TryToken(token); });
            auth.Failed += msg => UI(() =>
            {
                if (me != auth) return;
                status.Text = msg;
                status.ForeColor = Color.FromArgb(0xD0, 0x34, 0x34);
            });
            auth.Start();
        }

        async Task TryToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            status.Text = "Signing in...";
            status.ForeColor = Theme.Text;
            try
            {
                var me = await new Api(token).Get("/users/@me") as JObj;
                if (me == null || me.Str("id") == null) throw new Exception("That token didn't work.");
                Token = token;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                status.Text = ex is ApiException && ((ApiException)ex).Status == 401 ? "That token didn't work." : ex.Message;
                status.ForeColor = Color.FromArgb(0xD0, 0x34, 0x34);
            }
        }

        void UI(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }
    }
}
