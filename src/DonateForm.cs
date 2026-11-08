using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Buzzcord
{
    // Help > Donate: PayPal for everyone, plus GCash and InstaPay bank QR codes for the Philippines.
    // The QR pictures are built into the exe (assets\donate).
    public class DonateForm : Form
    {
        static DonateForm open;

        public static void Open(IWin32Window owner)
        {
            if (open != null && !open.IsDisposed) { open.Activate(); return; }
            open = new DonateForm();
            open.Show(owner);
        }

        readonly PictureBox qr = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(240, 240), BackColor = Color.White, Visible = false };
        readonly Label hint = new Label { AutoSize = false, TextAlign = ContentAlignment.TopCenter, Visible = false };
        Button gcash, bank;

        public DonateForm()
        {
            Text = "Donate to Buzzcord";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 236);

            var header = new BufferedPanel { Dock = DockStyle.Top, Height = 64 };
            header.Paint += (s, e) =>
            {
                Icons.FillHeader(e.Graphics, header.ClientRectangle);
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var logo = Icons.AppBitmap(44)) e.Graphics.DrawImage(logo, 12, 10);
                using (var f = new Font("Arial", 15f, FontStyle.Bold | FontStyle.Italic))
                using (var br = new SolidBrush(Icons.Purple))
                    e.Graphics.DrawString("Buy me a coffee", f, br, 62, 8);
                using (var f = new Font("Tahoma", 8f))
                using (var br = new SolidBrush(Theme.Dim))
                    e.Graphics.DrawString("Thank you for keeping Buzzcord going!", f, br, 64, 38);
            };
            var text = new Label
            {
                Text = "Buzzcord is free, with no ads and no tracking. If it made your day a little better, a small tip " +
                       "keeps it going and helps pay for code signing, so Windows stops warning about it.",
                Location = new Point(16, 74), Size = new Size(348, 44)
            };
            var paypal = Choice("PayPal (International)", 16, 126);
            paypal.Click += (s, e) => Program.OpenUrl(Program.DonateUrl);
            gcash = Choice("GCash (Philippines)", 16, 160);
            gcash.Click += (s, e) => ShowQr(gcash, "gcash.png", "Scan with GCash, or any bank app that takes InstaPay QR.");
            bank = Choice("Bank transfer (Philippines)", 16, 194);
            bank.Click += (s, e) => ShowQr(bank, "bank.png", "Scan with your bank's app (InstaPay QR).");
            qr.Location = new Point(70, 232);
            hint.Location = new Point(16, 478);
            hint.Size = new Size(348, 30);
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Visible = false };
            close.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { text, paypal, gcash, bank, qr, hint, close });
            Controls.Add(header);
            CancelButton = close;
            FormClosed += (s, e) => { if (qr.Image != null) qr.Image.Dispose(); };
            Theme.Attach(this, null);
        }

        Button Choice(string label, int x, int y)
        {
            return new Button { Text = label, Location = new Point(x, y), Size = new Size(348, 28), Font = new Font("Tahoma", 8.25f, FontStyle.Bold), Cursor = Cursors.Hand };
        }

        string shown;

        void ShowQr(Button from, string name, string caption)
        {
            if (shown == name) { HideQr(); return; }   // a second click hides it again
            var img = LoadQr(name);
            if (img == null) { MessageBox.Show(this, "The QR code is missing from this copy of Buzzcord.", "Buzzcord"); return; }
            if (qr.Image != null) qr.Image.Dispose();
            qr.Image = img;
            qr.Visible = hint.Visible = true;
            hint.Text = caption;
            shown = name;
            ClientSize = new Size(380, 516);
            gcash.FlatStyle = from == gcash ? FlatStyle.Flat : FlatStyle.Standard;
            bank.FlatStyle = from == bank ? FlatStyle.Flat : FlatStyle.Standard;
        }

        void HideQr()
        {
            shown = null;
            qr.Visible = hint.Visible = false;
            gcash.FlatStyle = bank.FlatStyle = FlatStyle.Standard;
            ClientSize = new Size(380, 236);
        }

        public static Image LoadQr(string name)
        {
            var s = typeof(DonateForm).Assembly.GetManifestResourceStream("Buzzcord.Donate." + name);
            if (s == null) return null;
            using (s)
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return Image.FromStream(ms);
            }
        }
    }
}
