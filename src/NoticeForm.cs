using System;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // Shown once, before the first sign-in: what Buzzcord is, the account risk, and how to stay
    // safe with tokens. Start with Windows is chosen here instead of being turned on silently.
    public class NoticeForm : Form
    {
        readonly CheckBox startup = new CheckBox { Text = "Start Buzzcord with Windows", Checked = true, AutoSize = true };

        public bool StartWithWindows { get { return startup.Checked; } }

        // True if the user accepted; remembers it so the notice only shows once.
        public static bool Accept(IWin32Window owner)
        {
            if (Settings.GetBool("NoticeAccepted", false)) return true;
            using (var f = new NoticeForm())
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                Settings.SetBool("NoticeAccepted", true);
                Settings.Save();
                AppContext.SetStartWithWindows(f.StartWithWindows);
                return true;
            }
        }

        public NoticeForm()
        {
            Text = "Welcome to Buzzcord";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 400);

            var header = new BufferedPanel { Dock = DockStyle.Top, Height = 64 };
            header.Paint += (s, e) =>
            {
                Icons.FillHeader(e.Graphics, header.ClientRectangle);
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var logo = Icons.AppBitmap(44)) e.Graphics.DrawImage(logo, 12, 10);
                using (var f = new Font("Arial", 15f, FontStyle.Bold | FontStyle.Italic))
                using (var br = new SolidBrush(Icons.Purple))
                    e.Graphics.DrawString("Before you sign in", f, br, 62, 8);
                using (var f = new Font("Tahoma", 8f))
                using (var br = new SolidBrush(Theme.Dim))
                    e.Graphics.DrawString("Please read this once.", f, br, 64, 38);
            };

            var text = new Label
            {
                Text =
                    "Buzzcord is an unofficial Discord client. It isn't made, endorsed or supported by Discord Inc.\n\n" +
                    "Discord's Terms of Service don't allow third-party clients on user accounts. Buzzcord behaves like " +
                    "Discord's own web app and never automates anything, but Discord could still limit or ban accounts " +
                    "that use it. Use it at your own risk.\n\n" +
                    "Keeping your account safe:\n" +
                    "• Sign in with the QR code. Buzzcord never asks for your password.\n" +
                    "• Never paste a token, script or \"login code\" someone sends you. That's how accounts get stolen.\n" +
                    "• Your sign-in stays on this PC, encrypted for your Windows user. Buzzcord connects only to Discord " +
                    "and the services Discord's own app uses (hCaptcha, Spotify album art). No ads, no tracking.\n" +
                    "• Only download Buzzcord from app.marcturtal.com or github.com/mtits/Buzzcord. The real exe is signed by Marc Turtal.",
                Location = new Point(16, 76), Size = new Size(390, 250)
            };
            startup.Location = new Point(16, 332);
            var ok = new Button { Text = "I understand", DialogResult = DialogResult.OK, AutoSize = true, Location = new Point(236, 362) };
            var quit = new Button { Text = "Quit", DialogResult = DialogResult.Cancel, Location = new Point(330, 362) };
            Controls.AddRange(new Control[] { text, startup, ok, quit });
            Controls.Add(header);
            AcceptButton = ok;
            CancelButton = quit;
            Theme.Attach(this, null);
        }
    }
}
