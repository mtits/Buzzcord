using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // The smiley picker (insert into a message, or react to one): a grid of color emoji, then the
    // server's own emoji. One popup panel, so it's laid out the same everywhere (a ContextMenuStrip
    // ignores table layouts and stacked everything in one column).
    public class EmojiPicker : ToolStripDropDown
    {
        public static readonly string[] Smileys = {
            "\U0001F600", "\U0001F602", "\U0001F923", "\U0001F60A", "\U0001F60D", "\U0001F618", "\U0001F61C", "\U0001F60E",
            "\U0001F914", "\U0001F610", "\U0001F644", "\U0001F62D", "\U0001F621", "\U0001F631", "\U0001F634", "\U0001F973",
            "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64F", "\U0001F525", "❤️", "\U0001F494", "\U0001F480",
            "\U0001F440", "\U0001F389", "\U0001F4AF", "✅", "❌", "\U0001F3AE", "\U0001F37A", "\U0001F355" };

        // What a cell holds: a unicode emoji, or a server emoji (id + name).
        public class Item
        {
            public string Text, Id, Name;
            public string Tip { get { return Name != null ? ":" + Name + ":" : null; } }
        }

        const int Cell = 36, Glyph = 26, Cols = 8, Pad = 6;
        readonly List<Item> items = new List<Item>();
        readonly Grid grid;
        readonly Action<Item> picked;

        public EmojiPicker(IEnumerable<JObj> serverEmoji, bool allowAnimated, Action<Item> picked)
        {
            this.picked = picked;
            foreach (var e in Smileys) items.Add(new Item { Text = e });
            int unicode = items.Count;
            if (serverEmoji != null)
                foreach (var em in serverEmoji)
                {
                    if (!allowAnimated && em.Bool("animated")) continue;
                    if (em.Has("available") && !em.Bool("available")) continue;
                    items.Add(new Item { Id = em.Str("id"), Name = em.Str("name") });
                    if (items.Count - unicode >= 64) break;
                }
            grid = new Grid(this, unicode);
            Padding = Padding.Empty;
            var host = new ToolStripControlHost(grid) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = grid.Size };
            Items.Add(host);
        }

        void Pick(Item it)
        {
            Close();
            picked(it);
        }

        class Grid : Control
        {
            readonly EmojiPicker owner;
            readonly int unicode;
            readonly ToolTip tip = new ToolTip();
            int hover = -1, header;

            public Grid(EmojiPicker owner, int unicode)
            {
                this.owner = owner;
                this.unicode = unicode;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                header = owner.items.Count > unicode ? 22 : 0;
                int rows1 = (unicode + Cols - 1) / Cols, rows2 = (owner.items.Count - unicode + Cols - 1) / Cols;
                Size = new Size(Pad * 2 + Cols * Cell, Pad * 2 + (rows1 + rows2) * Cell + header);
                BackColor = Theme.Window;
                Disposed += (s, e) => tip.Dispose();
            }

            Rectangle CellRect(int i)
            {
                int row, col, y0 = Pad;
                if (i < unicode) { row = i / Cols; col = i % Cols; }
                else
                {
                    int j = i - unicode;
                    row = j / Cols; col = j % Cols;
                    y0 = Pad + ((unicode + Cols - 1) / Cols) * Cell + header;
                }
                return new Rectangle(Pad + col * Cell, y0 + row * Cell, Cell, Cell);
            }

            int HitTest(Point p)
            {
                for (int i = 0; i < owner.items.Count; i++) if (CellRect(i).Contains(p)) return i;
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int h = HitTest(e.Location);
                if (h == hover) return;
                hover = h;
                Invalidate();
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                string t = h >= 0 ? owner.items[h].Tip : null;
                if (t != null) tip.Show(t, this, e.X + 12, e.Y + 18, 1500); else tip.Hide(this);
            }

            protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                int h = HitTest(e.Location);
                if (h >= 0) owner.Pick(owner.items[h]);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Window);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (header > 0)
                {
                    int y = CellRect(unicode).Top - header + 4;
                    TextRenderer.DrawText(g, "THIS SERVER", new Font("Tahoma", 7f, FontStyle.Bold), new Point(Pad + 4, y), Theme.SubText);
                }
                for (int i = 0; i < owner.items.Count; i++)
                {
                    var r = CellRect(i);
                    if (i == hover)
                        using (var br = new SolidBrush(Theme.Hover))
                        using (var path = Rounded(Rectangle.Inflate(r, -2, -2), 8))
                            g.FillPath(br, path);
                    var it = owner.items[i];
                    var glyph = new Rectangle(r.X + (Cell - Glyph) / 2, r.Y + (Cell - Glyph) / 2, Glyph, Glyph);
                    if (i == hover) glyph.Inflate(2, 2);   // a little pop on hover
                    Image img = it.Text != null ? (Image)Emoji.Get(it.Text, 48)
                        : ImageCache.Peek("https://cdn.discordapp.com/emojis/" + it.Id + ".png?size=48");
                    if (img != null) g.DrawImage(img, glyph);
                    else if (it.Text != null)
                        using (var f = new Font("Segoe UI Emoji", 14f))
                            TextRenderer.DrawText(g, it.Text, f, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    else
                        ImageCache.Get("https://cdn.discordapp.com/emojis/" + it.Id + ".png?size=48", loaded => Invalidate());
                }
            }

            static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
            {
                var p = new System.Drawing.Drawing2D.GraphicsPath();
                int d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
