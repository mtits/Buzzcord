using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Everything visual is drawn in code: the status smileys, the app icon, the window chrome
    // colours. No image files to ship.
    public static class Icons
    {
        // Window colours follow the theme (see Theme); the app icon always uses the brand purple.
        public static Color Purple { get { return Theme.Accent; } }
        public static Color PurpleLight { get { return Theme.AccentLight; } }
        public static Color HeaderTop { get { return Theme.HeaderTop; } }
        public static Color HeaderBottom { get { return Theme.HeaderBottom; } }
        public static Color GroupBack { get { return Theme.GroupBack; } }
        public static Color Panel { get { return Theme.Panel; } }
        static readonly Color BrandPurple = Color.FromArgb(0x5B, 0x2C, 0x8F), BrandPurpleLight = Color.FromArgb(0x9D, 0x7B, 0xC9);

        static readonly Dictionary<string, Bitmap> statusCache = new Dictionary<string, Bitmap>();

        public static Color StatusColor(string status)
        {
            switch (status)
            {
                case "online": return Color.FromArgb(0xFF, 0xD2, 0x1F);
                case "idle": return Color.FromArgb(0xF0, 0xA0, 0x30);
                case "dnd": return Color.FromArgb(0xE0, 0x3C, 0x3C);
                default: return Color.FromArgb(0xC8, 0xC8, 0xC8);
            }
        }

        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "online": return "Available";
                case "idle": return "Idle";
                case "dnd": return "Busy";
                case "invisible": return "Invisible";
                default: return "Offline";
            }
        }

        // The classic buddy smiley, tinted by status. Idle gets a little moon, busy a dash mouth.
        public static Bitmap Status(string status, int size)
        {
            string key = status + size;
            Bitmap b;
            if (statusCache.TryGetValue(key, out b)) return b;
            b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size;
                var face = new RectangleF(s * 0.06f, s * 0.06f, s * 0.86f, s * 0.86f);
                var fill = StatusColor(status == "invisible" ? "offline" : status);
                using (var br = new LinearGradientBrush(face, Shade.Light(fill, 0.6f), fill, 60f))
                    g.FillEllipse(br, face);
                using (var pen = new Pen(Shade.Dark(fill, 0.35f), Math.Max(1f, s / 16f)))
                {
                    g.DrawEllipse(pen, face);
                    float eyeW = s * 0.1f, eyeH = s * 0.16f;
                    using (var eye = new SolidBrush(Color.FromArgb(0x30, 0x20, 0x10)))
                    {
                        g.FillEllipse(eye, s * 0.31f, s * 0.28f, eyeW, eyeH);
                        g.FillEllipse(eye, s * 0.57f, s * 0.28f, eyeW, eyeH);
                    }
                    using (var mouth = new Pen(Color.FromArgb(0x30, 0x20, 0x10), Math.Max(1f, s / 13f)))
                    {
                        if (status == "dnd") g.DrawLine(mouth, s * 0.32f, s * 0.66f, s * 0.66f, s * 0.66f);
                        else if (status == "offline" || status == "invisible") g.DrawArc(mouth, s * 0.3f, s * 0.62f, s * 0.38f, s * 0.2f, 200, 140);
                        else g.DrawArc(mouth, s * 0.27f, s * 0.38f, s * 0.44f, s * 0.34f, 25, 130);
                    }
                }
                if (status == "idle")
                {
                    using (var moon = new SolidBrush(Color.FromArgb(0x4A, 0x3A, 0x8A)))
                    {
                        var r = new RectangleF(s * 0.55f, 0, s * 0.45f, s * 0.45f);
                        using (var path = new GraphicsPath())
                        {
                            path.AddEllipse(r);
                            var cut = new Region(path);
                            cut.Exclude(new RectangleF(r.X + r.Width * 0.35f, r.Y - r.Height * 0.15f, r.Width, r.Height));
                            g.FillRegion(moon, cut);
                        }
                    }
                }
            }
            statusCache[key] = b;
            return b;
        }

        public static Icon AppIcon(int size)
        {
            return AppIcon(size, false);
        }

        // badge: a red dot in the corner, for "something's unread" in the tray.
        public static Icon AppIcon(int size, bool badge)
        {
            using (var bmp = AppBitmap(size))
            {
                if (badge)
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        float d = size * 0.5f;
                        g.FillEllipse(Brushes.White, size - d - 1, -0.5f, d + 1, d + 1);
                        using (var red = new SolidBrush(Color.FromArgb(0xE0, 0x2A, 0x2A))) g.FillEllipse(red, size - d - 0.5f, 0, d, d);
                    }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // Taskbar overlay: a red disc with the unread count (9+ past nine).
        public static Icon CountBadge(int count)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var red = new SolidBrush(Color.FromArgb(0xE0, 0x2A, 0x2A))) g.FillEllipse(red, 0, 0, 15.5f, 15.5f);
                    string text = count > 9 ? "9+" : count.ToString();
                    using (var f = new Font("Tahoma", count > 9 ? 6f : 7.5f, FontStyle.Bold))
                    {
                        var sz = g.MeasureString(text, f);
                        g.DrawString(text, f, Brushes.White, (16 - sz.Width) / 2 + 0.5f, (16 - sz.Height) / 2 + 0.5f);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // Purple speech bubble with a yellow smiley: Buzzcord's own mark.
        public static Bitmap AppBitmap(int size)
        {
            var b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size;
                using (var path = new GraphicsPath())
                {
                    var r = new RectangleF(s * 0.03f, s * 0.05f, s * 0.94f, s * 0.74f);
                    float rad = s * 0.22f;
                    path.AddArc(r.X, r.Y, rad, rad, 180, 90);
                    path.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
                    path.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
                    path.AddLine(r.X + r.Width * 0.42f, r.Bottom, r.X + r.Width * 0.2f, s * 0.97f);
                    path.AddLine(r.X + r.Width * 0.22f, r.Bottom, r.X + rad / 2, r.Bottom);
                    path.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new RectangleF(0, 0, s, s), BrandPurpleLight, BrandPurple, 90f))
                        g.FillPath(br, path);
                }
                var face = Status("online", (int)(s * 0.56f)); // cached: don't dispose
                g.DrawImage(face, s * 0.22f, s * 0.12f, s * 0.56f, s * 0.56f);
            }
            return b;
        }

        public static byte[] IcoBytes()
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            var pngs = new List<byte[]>();
            foreach (int sz in sizes)
                using (var bmp = AppBitmap(sz))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
            return o.ToArray();
        }

        public static void FillHeader(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var br = new LinearGradientBrush(r, HeaderTop, HeaderBottom, 90f))
                g.FillRectangle(br, r);
            using (var pen = new Pen(Theme.HeaderLine))
                g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
    }

    static class Shade
    {
        public static Color Light(Color c, float amount)
        {
            return Color.FromArgb(c.A, c.R + (int)((255 - c.R) * amount), c.G + (int)((255 - c.G) * amount), c.B + (int)((255 - c.B) * amount));
        }

        public static Color Dark(Color c, float amount)
        {
            return Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));
        }
    }

    // Small images (avatars, game art) fetched once and kept for the session.
    public static class ImageCache
    {
        static readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        static readonly Dictionary<string, List<Action<Image>>> pending = new Dictionary<string, List<Action<Image>>>();

        // Test hook: supplies images without the network (the self-test's screenshots use it).
        public static Func<string, Image> Override;

        // Calls back on the UI thread; immediately if the image is already here.
        public static void Get(string url, Action<Image> done)
        {
            if (string.IsNullOrEmpty(url)) return;
            Image img;
            if (images.TryGetValue(url, out img)) { if (img != null) done(img); return; }
            if (Override != null && (img = Override(url)) != null) { images[url] = img; done(img); return; }
            List<Action<Image>> waiters;
            if (pending.TryGetValue(url, out waiters)) { waiters.Add(done); return; }
            pending[url] = new List<Action<Image>> { done };
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Api.GetBytes(url).ContinueWith(t =>
            {
                Image result = null;
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    try { result = Image.FromStream(new MemoryStream(t.Result)); }
                    catch (Exception) { }
                }
                images[url] = result;
                List<Action<Image>> list;
                if (pending.TryGetValue(url, out list))
                {
                    pending.Remove(url);
                    if (result != null) foreach (var a in list) a(result);
                }
            }, ui);
        }

        public static Image Peek(string url)
        {
            Image img;
            return url != null && images.TryGetValue(url, out img) ? img : null;
        }

        static readonly Dictionary<string, Bitmap> scaled = new Dictionary<string, Bitmap>();

        // The image pre-scaled to size x size in the fastest format to draw, so scrolling and
        // repainting never resample. Null until loaded; onLoaded fires once when it arrives.
        public static Bitmap Scaled(string url, int size, Action onLoaded)
        {
            if (string.IsNullOrEmpty(url) || size <= 0) return null;
            string key = url + "@" + size;
            Bitmap b;
            if (scaled.TryGetValue(key, out b)) return b;
            var src = Peek(url);
            if (src == null)
            {
                if (onLoaded != null) Get(url, i => onLoaded());
                return null;
            }
            b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            scaled[key] = b;
            return b;
        }
    }

    // A panel that repaints all of itself on resize, off-screen first: no smearing or flicker.
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }
}
