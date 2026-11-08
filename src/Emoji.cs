using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Buzzcord
{
    // Color emoji. GDI and GDI+ only draw Segoe UI Emoji as black outlines; DirectWrite through
    // Direct2D draws the real color glyphs (Windows 8.1 and later). Each emoji is drawn once per
    // size into a transparent bitmap and cached. Returns null where color isn't available, and
    // callers fall back to plain text.
    public static class Emoji
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        static ID2D1Factory d2d;
        static IDWriteFactory dwrite;
        static bool broken;

        public static Bitmap Get(string emoji, int size)
        {
            if (string.IsNullOrEmpty(emoji) || broken) return null;
            string key = size + "|" + emoji;
            Bitmap b;
            if (cache.TryGetValue(key, out b)) return b;
            try { b = Render(emoji, size); }
            catch (Exception ex)
            {
                broken = true;   // don't retry on every paint
                Program.Log("emoji: " + ex.Message);
                b = null;
            }
            cache[key] = b;
            return b;
        }

        static Bitmap Render(string emoji, int size)
        {
            if (d2d == null)
            {
                var opts = new D2D1_FACTORY_OPTIONS();
                Guid iid = typeof(ID2D1Factory).GUID;
                object f;
                Check(D2D1CreateFactory(0 /* single-threaded */, ref iid, ref opts, out f));
                d2d = (ID2D1Factory)f;
                Guid wid = typeof(IDWriteFactory).GUID;
                object w;
                Check(DWriteCreateFactory(0 /* shared */, ref wid, out w));
                dwrite = (IDWriteFactory)w;
            }

            // A 32-bit premultiplied DIB that Direct2D draws into through a DC render target.
            var bmi = new BITMAPINFO { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = SelectObject(dc, dib);
            ID2D1DCRenderTarget rt = null;
            IDWriteTextFormat format = null;
            ID2D1SolidColorBrush brush = null;
            try
            {
                var props = new D2D1_RENDER_TARGET_PROPERTIES
                {
                    type = 0, pixelFormat = new D2D1_PIXEL_FORMAT { format = 87 /* B8G8R8A8_UNORM */, alphaMode = 1 /* premultiplied */ },
                    dpiX = 96, dpiY = 96, usage = 0, minLevel = 0
                };
                Check(d2d.CreateDCRenderTarget(ref props, out rt));
                var rc = new RECT { right = size, bottom = size };
                Check(rt.BindDC(dc, ref rc));
                // The em size is a little smaller than the box: emoji glyphs fill their em square.
                Check(dwrite.CreateTextFormat("Segoe UI Emoji", IntPtr.Zero, 400, 0, 5, size * 0.78f, "en-us", out format));
                format.SetTextAlignment(2);       // centered
                format.SetParagraphAlignment(2);  // centered
                var black = new D2D1_COLOR_F { a = 1 };
                var bp = new D2D1_BRUSH_PROPERTIES { opacity = 1, m11 = 1, m22 = 1 };
                Check(rt.CreateSolidColorBrush(ref black, ref bp, out brush));
                rt.BeginDraw();
                var clear = new D2D1_COLOR_F();
                rt.Clear(ref clear);
                rt.SetTextAntialiasMode(2);       // grayscale: ClearType can't blend onto transparency
                var box = new D2D1_RECT_F { right = size, bottom = size * 1.04f };
                rt.DrawText(emoji, (uint)emoji.Length, format, ref box, brush, 4 /* ENABLE_COLOR_FONT */, 0);
                ulong t1, t2;
                Check(rt.EndDraw(out t1, out t2));

                var result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                var data = result.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                var row = new byte[size * 4];
                for (int y = 0; y < size; y++)
                {
                    Marshal.Copy(bits + y * size * 4, row, 0, row.Length);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
                result.UnlockBits(data);
                return result;
            }
            finally
            {
                if (brush != null) Marshal.ReleaseComObject(brush);
                if (format != null) Marshal.ReleaseComObject(format);
                if (rt != null) Marshal.ReleaseComObject(rt);
                SelectObject(dc, old);
                DeleteDC(dc);
                DeleteObject(dib);
            }
        }

        // ---- emoji in text (the IE chat view draws them in black and white too) ----

        static readonly Dictionary<string, string> uris = new Dictionary<string, string>();

        // A PNG data URI of the emoji, drawn at twice the display size so it stays sharp when
        // Windows scales the page. Null if color emoji aren't available.
        public static string DataUri(string emoji, int px)
        {
            string key = px + "|" + emoji;
            string uri;
            if (uris.TryGetValue(key, out uri)) return uri;
            var b = Get(emoji, px * 2);
            if (b != null)
                using (var ms = new System.IO.MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    uri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            uris[key] = uri;
            return uri;
        }

        // Emoji that show as pictures without a variation selector (Emoji_Presentation in the BMP).
        const string BmpEmoji = "⌚⌛⏩⏪⏫⏬⏰⏳◽◾☔☕♈♉♊♋♌♍♎♏" +
            "♐♑♒♓♿⚓⚡⚪⚫⚽⚾⛄⛅⛎⛔⛪⛲⛳⛵⛺⛽✅" +
            "✊✋✨❌❎❓❔❕❗➕➖➗➰➿⬛⬜⭐⭕";

        static int CodePointAt(string s, int i)
        {
            return char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];
        }

        static bool IsPictograph(int cp) { return cp >= 0x1F000 && cp <= 0x1FAFF; }

        // Length of the emoji sequence starting at i (0 if none): a base emoji plus any variation
        // selector, skin tone, keycap or tag characters, joined by ZWJ to further emoji.
        public static int Match(string s, int i)
        {
            int start = i, cp = CodePointAt(s, i);
            int len = cp > 0xFFFF ? 2 : 1;
            bool regional = cp >= 0x1F1E6 && cp <= 0x1F1FF;
            bool next = i + len < s.Length;
            bool vs16 = next && s[i + len] == '️';
            if (regional)
            {
                // A flag is two regional indicators.
                if (i + 4 <= s.Length && CodePointAt(s, i + 2) >= 0x1F1E6 && CodePointAt(s, i + 2) <= 0x1F1FF) return 4;
                return 2;
            }
            if ((cp == '#' || cp == '*' || (cp >= '0' && cp <= '9')))
                return i + 2 < s.Length && s[i + 1] == '️' && s[i + 2] == '⃣' ? 3 : 0;   // keycaps only
            bool emoji = IsPictograph(cp) || BmpEmoji.IndexOf((char)cp) >= 0 && cp <= 0xFFFF
                || vs16 && (cp >= 0x2000 && cp <= 0x2BFF || cp == 0xA9 || cp == 0xAE || cp == 0x3030 || cp == 0x303D || cp == 0x3297 || cp == 0x3299);
            if (!emoji) return 0;
            i += len;
            while (i < s.Length)
            {
                int c = CodePointAt(s, i);
                if (c == 0xFE0F || c == 0x20E3 || (c >= 0x1F3FB && c <= 0x1F3FF) || (c >= 0xE0020 && c <= 0xE007F)) { i += c > 0xFFFF ? 2 : 1; continue; }
                if (c == 0x200D && i + 1 < s.Length)
                {
                    int n = CodePointAt(s, i + 1);
                    if (IsPictograph(n) || (n >= 0x2000 && n <= 0x2BFF)) { i += 1 + (n > 0xFFFF ? 2 : 1); continue; }
                }
                break;
            }
            return i - start;
        }

        // Replaces every emoji sequence in text with what replace() returns for it.
        public static string ReplaceAll(string text, Func<string, string> replace)
        {
            if (string.IsNullOrEmpty(text)) return text;
            System.Text.StringBuilder sb = null;
            int last = 0;
            for (int i = 0; i < text.Length; )
            {
                int n = Match(text, i);
                if (n == 0) { i++; continue; }
                if (sb == null) sb = new System.Text.StringBuilder();
                sb.Append(text, last, i - last).Append(replace(text.Substring(i, n)));
                i += n;
                last = i;
            }
            if (sb == null) return text;
            return sb.Append(text, last, text.Length - last).ToString();
        }

        // True when the text is nothing but emoji (and spaces), at most 27 of them: Discord shows
        // those big.
        public static bool OnlyEmoji(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int count = 0;
            for (int i = 0; i < text.Length; )
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                int n = Match(text, i);
                if (n == 0) return false;
                count++;
                i += n;
            }
            return count > 0 && count <= 27;
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        // ---- interop ----

        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, ref D2D1_FACTORY_OPTIONS opts, [MarshalAs(UnmanagedType.Interface)] out object factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object factory);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)] struct D2D1_FACTORY_OPTIONS { public int debugLevel; }
        [StructLayout(LayoutKind.Sequential)] struct D2D1_PIXEL_FORMAT { public int format; public int alphaMode; }
        [StructLayout(LayoutKind.Sequential)]
        struct D2D1_RENDER_TARGET_PROPERTIES { public int type; public D2D1_PIXEL_FORMAT pixelFormat; public float dpiX, dpiY; public int usage, minLevel; }
        [StructLayout(LayoutKind.Sequential)] struct D2D1_COLOR_F { public float r, g, b, a; }
        [StructLayout(LayoutKind.Sequential)] struct D2D1_RECT_F { public float left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct D2D1_BRUSH_PROPERTIES { public float opacity, m11, m12, m21, m22, dx, dy; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        // Only the methods used are typed; the rest hold their vtable slots in order.
        [ComImport, Guid("06152247-6f50-465a-9245-118bfd3b6007"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1Factory
        {
            void ReloadSystemMetrics(); void GetDesktopDpi(); void CreateRectangleGeometry(); void CreateRoundedRectangleGeometry();
            void CreateEllipseGeometry(); void CreateGeometryGroup(); void CreateTransformedGeometry(); void CreatePathGeometry();
            void CreateStrokeStyle(); void CreateDrawingStateBlock(); void CreateWicBitmapRenderTarget(); void CreateHwndRenderTarget();
            void CreateDxgiSurfaceRenderTarget();
            [PreserveSig] int CreateDCRenderTarget(ref D2D1_RENDER_TARGET_PROPERTIES props, out ID2D1DCRenderTarget target);
        }

        [ComImport, Guid("1c51bc64-de61-46fd-9899-63a5d8f03950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1DCRenderTarget
        {
            void GetFactory();
            void CreateBitmap(); void CreateBitmapFromWicBitmap(); void CreateSharedBitmap(); void CreateBitmapBrush();
            [PreserveSig] int CreateSolidColorBrush(ref D2D1_COLOR_F color, ref D2D1_BRUSH_PROPERTIES props, out ID2D1SolidColorBrush brush);
            void CreateGradientStopCollection(); void CreateLinearGradientBrush(); void CreateRadialGradientBrush(); void CreateCompatibleRenderTarget();
            void CreateLayer(); void CreateMesh(); void DrawLine(); void DrawRectangle(); void FillRectangle(); void DrawRoundedRectangle();
            void FillRoundedRectangle(); void DrawEllipse(); void FillEllipse(); void DrawGeometry(); void FillGeometry(); void FillMesh();
            void FillOpacityMask(); void DrawBitmap();
            [PreserveSig] void DrawText([MarshalAs(UnmanagedType.LPWStr)] string text, uint length, IDWriteTextFormat format, ref D2D1_RECT_F rect, ID2D1SolidColorBrush brush, int options, int measuring);
            void DrawTextLayout(); void DrawGlyphRun(); void SetTransform(); void GetTransform(); void SetAntialiasMode(); void GetAntialiasMode();
            [PreserveSig] void SetTextAntialiasMode(int mode);
            void GetTextAntialiasMode(); void SetTextRenderingParams(); void GetTextRenderingParams(); void SetTags(); void GetTags();
            void PushLayer(); void PopLayer(); void Flush(); void SaveDrawingState(); void RestoreDrawingState(); void PushAxisAlignedClip();
            void PopAxisAlignedClip();
            [PreserveSig] void Clear(ref D2D1_COLOR_F color);
            [PreserveSig] void BeginDraw();
            [PreserveSig] int EndDraw(out ulong tag1, out ulong tag2);
            void GetPixelFormat(); void SetDpi(); void GetDpi(); void GetSize(); void GetPixelSize(); void GetMaximumBitmapSize(); void IsSupported();
            [PreserveSig] int BindDC(IntPtr hdc, ref RECT rect);
        }

        [ComImport, Guid("2cd906a9-12e2-11dc-9fed-001143a055f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1SolidColorBrush { }

        [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteFactory
        {
            void GetSystemFontCollection(); void CreateCustomFontCollection(); void RegisterFontCollectionLoader(); void UnregisterFontCollectionLoader();
            void CreateFontFileReference(); void CreateCustomFontFileReference(); void CreateFontFace(); void CreateRenderingParams();
            void CreateMonitorRenderingParams(); void CreateCustomRenderingParams(); void RegisterFontFileLoader(); void UnregisterFontFileLoader();
            [PreserveSig] int CreateTextFormat([MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch,
                float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IDWriteTextFormat format);
        }

        [ComImport, Guid("9c906818-31d7-4fd3-a151-7c5e225db55a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteTextFormat
        {
            [PreserveSig] int SetTextAlignment(int alignment);
            [PreserveSig] int SetParagraphAlignment(int alignment);
        }
    }
}
