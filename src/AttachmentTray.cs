using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Windows.Forms;

namespace Buzzcord
{
    // The strip above an IM window's message box: a thumbnail (pictures) or the file's icon, name,
    // size and an x for each attached file, a line with the total and this chat's limit, a warning
    // near or over the limit, upload progress while sending, and the note when a file wasn't
    // attached or Discord refused it. Mirrors mac/Sources/Buzzcord/AttachmentTray.swift.
    public class AttachmentTray : Panel
    {
        public static readonly Color Orange = Color.FromArgb(0xD0, 0x7A, 0x00);
        public static readonly Color Red = Color.FromArgb(0xD0, 0x34, 0x34);

        readonly Func<UploadContext> context;
        readonly List<string> files = new List<string>();
        readonly FlowLayoutPanel chips = new FlowLayoutPanel { Height = 46, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 2, 0, 0) };
        readonly Label summary = new Label { Height = 16, AutoEllipsis = true };
        readonly Label warning = new Label { Height = 16, AutoEllipsis = true };
        readonly Panel progressRow = new Panel { Height = 18 };
        readonly ProgressBar progress = new ProgressBar { Location = new Point(0, 2), Size = new Size(160, 13), Maximum = 1000 };
        readonly Label progressText = new Label { Location = new Point(168, 1), AutoSize = true };
        readonly Panel notePanel = new Panel();
        readonly Label note = new Label { Dock = DockStyle.Fill, ForeColor = Red };
        readonly Button noteClose = new Button { Dock = DockStyle.Right, Width = 20, Text = "×", FlatStyle = FlatStyle.Flat, TabStop = false };
        readonly ToolTip tips = new ToolTip();
        bool sending;
        readonly HashSet<Control> shown = new HashSet<Control>();   // which lines are on (Visible reads false while the window is hidden)

        public event Action HeightChanged;

        public AttachmentTray(Func<UploadContext> context)
        {
            this.context = context;
            Dock = DockStyle.Top;
            Height = 0;
            Padding = new Padding(0, 0, 0, 2);
            noteClose.FlatAppearance.BorderSize = 0;
            noteClose.Click += (o, e) => Note = null;
            tips.SetToolTip(noteClose, "Dismiss");
            notePanel.Controls.Add(note);
            notePanel.Controls.Add(noteClose);
            progressRow.Controls.Add(progress);
            progressRow.Controls.Add(progressText);
            Controls.Add(chips);
            Controls.Add(summary);
            Controls.Add(progressRow);
            Controls.Add(warning);
            Controls.Add(notePanel);
            Resize += (o, e) => LayoutNote();
            Rebuild();
        }

        // The attached files, in order.
        public List<string> Files { get { return files; } }

        public bool HasContent { get { return files.Count > 0 || note.Text.Length > 0; } }

        // A file wasn't attached, or Discord refused one. Red, with a beep; null clears it.
        public string Note
        {
            get { return note.Text.Length > 0 ? note.Text : null; }
            set
            {
                note.Text = value ?? "";
                if (value != null) SystemSounds.Beep.Play();
                Rebuild();
            }
        }

        // Adds what can be attached here and says why the rest wasn't. Returns how many were added.
        public int Add(IEnumerable<string> paths)
        {
            var list = new List<string>(paths);
            var candidates = new List<Uploads.Candidate>();
            foreach (var p in list) candidates.Add(Uploads.Candidate.FromPath(p));
            var r = Uploads.Check(files.Count, candidates, context());
            foreach (int i in r.Accepted) files.Add(list[i]);
            if (r.Notes.Count > 0) Note = string.Join("\n", r.Notes);
            else Rebuild();
            if (r.Accepted.Count > 0) Program.Log("attach: " + r.Accepted.Count + " file(s), " + r.Notes.Count + " refused");
            return r.Accepted.Count;
        }

        public void Remove(string path)
        {
            if (sending) return;
            files.Remove(path);
            Rebuild();
        }

        // Esc: everything goes, the note too.
        public void ClearAll()
        {
            files.Clear();
            note.Text = "";
            Rebuild();
        }

        // Sent: the files go, a note stays only if something else set one.
        public void ClearFiles()
        {
            files.Clear();
            Rebuild();
        }

        // The files as the limit sees them, with their current sizes.
        public List<Uploads.Candidate> Candidates()
        {
            var list = new List<Uploads.Candidate>();
            foreach (var f in files) list.Add(Uploads.Candidate.FromPath(f));
            return list;
        }

        // 0..1 while sending, null when done.
        public void SetProgress(double? p)
        {
            bool now = p != null;
            if (now != sending) { sending = now; Rebuild(); }
            if (p == null) return;
            int v = (int)Math.Max(0, Math.Min(1000, p.Value * 1000));
            progress.Value = v;
            progressText.Text = "Uploading... " + (v / 10) + "%";
        }

        // Rebuilds the chips and lines after a change.
        public void Rebuild()
        {
            var ctx = context();
            long limit = ctx.Limit;
            BackColor = chips.BackColor = Theme.Panel;
            summary.ForeColor = progressText.ForeColor = Theme.SubText;

            chips.SuspendLayout();
            while (chips.Controls.Count > 0) chips.Controls[0].Dispose();
            foreach (var f in files) chips.Controls.Add(Chip(f, limit));
            chips.ResumeLayout();

            var cands = Candidates();
            bool over = false;
            string warn = files.Count > 0 ? Uploads.TrayWarning(cands, ctx, out over) : null;
            summary.Text = files.Count > 0 ? Uploads.Summary(cands, ctx) : "";
            warning.Text = warn ?? "";
            warning.ForeColor = over ? Red : Orange;

            Show(chips, files.Count > 0);
            Show(summary, files.Count > 0 && !sending);
            Show(progressRow, files.Count > 0 && sending);
            Show(warning, warn != null && !sending);
            Show(notePanel, note.Text.Length > 0);
            LayoutNote();
        }

        void Show(Control c, bool on)
        {
            c.Visible = on;
            if (on) shown.Add(c); else shown.Remove(c);
        }

        void LayoutNote()
        {
            if (shown.Contains(notePanel))
            {
                int w = Math.Max(50, Width - noteClose.Width - 4);
                var size = TextRenderer.MeasureText(note.Text, note.Font, new Size(w, 0), TextFormatFlags.WordBreak);
                notePanel.Height = Math.Min(70, size.Height + 4);
            }
            // Top to bottom: the files, the total and limit (or progress), the warning, the note.
            int y = 0;
            foreach (var c in new Control[] { chips, summary, progressRow, warning, notePanel })
            {
                if (!shown.Contains(c)) continue;
                c.SetBounds(0, y, Math.Max(0, Width), c.Height);
                y += c.Height;
            }
            int h = HasContent ? y + Padding.Vertical : 0;
            if (h != Height)
            {
                Height = h;
                if (HeightChanged != null) HeightChanged();
            }
        }

        Control Chip(string path, long limit)
        {
            var c = Uploads.Candidate.FromPath(path);
            var chip = new Panel { Size = new Size(176, 40), Margin = new Padding(0, 0, 6, 0), BackColor = Theme.Window };
            chip.Paint += (o, e) => { using (var pen = new Pen(Icons.PurpleLight)) e.Graphics.DrawRectangle(pen, 0, 0, chip.Width - 1, chip.Height - 1); };
            var pic = new PictureBox { Location = new Point(3, 3), Size = new Size(34, 34), SizeMode = PictureBoxSizeMode.CenterImage, Image = Thumbnail(path, 34) };
            var name = new Label { Location = new Point(41, 4), Size = new Size(112, 15), Text = c.Name, AutoEllipsis = true, ForeColor = Theme.Text };
            var size = new Label { Location = new Point(41, 20), Size = new Size(112, 14), Text = Uploads.Size(c.Size), ForeColor = c.Size > limit ? Red : Theme.SubText, Font = new Font(Font.FontFamily, 7.5f) };
            var x = new Button { Location = new Point(154, 9), Size = new Size(20, 20), Text = "×", FlatStyle = FlatStyle.Flat, TabStop = false, Enabled = !sending, ForeColor = Theme.SubText };
            x.FlatAppearance.BorderSize = 0;
            x.Click += (o, e) => Remove(path);
            string tip = c.Name + ", " + Uploads.Size(c.Size);
            tips.SetToolTip(chip, tip);
            tips.SetToolTip(pic, tip);
            tips.SetToolTip(name, tip);
            tips.SetToolTip(x, "Remove " + c.Name);
            chip.Controls.Add(pic);
            chip.Controls.Add(name);
            chip.Controls.Add(size);
            chip.Controls.Add(x);
            chip.Disposed += (o, e) => { if (pic.Image != null) pic.Image.Dispose(); };
            return chip;
        }

        static readonly HashSet<string> pictureExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

        // A small picture of the file: the picture itself (cropped square), or the file's icon.
        public static Bitmap Thumbnail(string path, int px)
        {
            try
            {
                if (pictureExts.Contains(Path.GetExtension(path)) && new FileInfo(path).Length < 40 * UploadLimits.MB)
                {
                    using (var fs = File.OpenRead(path))
                    using (var src = Image.FromStream(fs, false, false))
                    {
                        var b = new Bitmap(px, px);
                        using (var g = Graphics.FromImage(b))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            int side = Math.Min(src.Width, src.Height);
                            g.DrawImage(src, new Rectangle(0, 0, px, px), new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                        }
                        return b;
                    }
                }
            }
            catch (Exception) { }
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path)) return icon != null ? icon.ToBitmap() : null;
            }
            catch (Exception) { return null; }
        }
    }
}
