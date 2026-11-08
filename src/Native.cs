using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Buzzcord
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static void Flash(Form f)
        {
            if (f == null || f.IsDisposed || Form.ActiveForm == f) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = f.Handle,
                dwFlags = 3 | 12, // caption + tray, until the window comes to the front
                uCount = 0,
                dwTimeout = 0
            };
            FlashWindowEx(ref fi);
        }

        // The chat view is Internet Explorer's engine. Hosted in an app it defaults to an old,
        // software-rendered mode; these per-app switches give it GPU drawing and IE11 standards.
        // They apply to whichever exe hosts Buzzcord (powershell.exe while it runs unsigned).
        public static void TuneBrowserEngine()
        {
            try
            {
                string exe = System.IO.Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                const string root = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\";
                Set(root + "FEATURE_BROWSER_EMULATION", exe, 11001);
                Set(root + "FEATURE_GPU_RENDERING", exe, 1);
            }
            catch (Exception ex) { Program.Log("browser features: " + ex.Message); }
        }

        static void Set(string key, string name, int value)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
                if (!(k.GetValue(name) is int) || (int)k.GetValue(name) != value) k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class CTaskbarList { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint reserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string description);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
        }

        static ITaskbarList3 taskbar;

        // Small badge over a window's taskbar button; null icon removes it.
        public static void SetOverlay(Form f, System.Drawing.Icon icon, string description)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                if (taskbar == null)
                {
                    taskbar = (ITaskbarList3)new CTaskbarList();
                    taskbar.HrInit();
                }
                taskbar.SetOverlayIcon(f.Handle, icon != null ? icon.Handle : IntPtr.Zero, description);
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        // Dark caption bar on Windows 10 20H1+ / 11.
        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref v, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch (Exception) { }
        }

        // Dark scrollbars (and borders) for standard controls.
        public static void SetControlTheme(IntPtr hwnd, bool dark)
        {
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // Grey hint text shown in an empty text box.
        public static void SetCueBanner(TextBox box, string text)
        {
            SendMessage(box.Handle, 0x1501, new IntPtr(1), text); // EM_SETCUEBANNER
        }

        public static void TryPerMonitorDpi()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
        }
    }

    // The new-message "ding-dong", synthesised once so there's no sound file to ship.
    static class Sounds
    {
        static SoundPlayer ding, door;

        public static bool Enabled { get { return Settings.GetBool("Sounds", true); } }

        public static void Ding()
        {
            if (!Enabled) return;
            try
            {
                if (ding == null) ding = new SoundPlayer(new MemoryStream(Make(new[] { new[] { 1318.5, 0.0, 0.16 }, new[] { 987.8, 0.13, 0.32 } }))); // E6 then B5
                ding.Play();
            }
            catch (Exception) { }
        }

        // A friend signed in: a rising two-note "door" chime (G5 then C6), softer than the ding.
        public static void Door()
        {
            if (!Enabled) return;
            try
            {
                if (door == null) door = new SoundPlayer(new MemoryStream(Make(new[] { new[] { 784.0, 0.0, 0.18 }, new[] { 1046.5, 0.12, 0.34 } })));
                door.Play();
            }
            catch (Exception) { }
        }

        static SoundPlayer ring, joined, left;

        // An incoming call: a two-tone trill every two seconds, until StopRing.
        public static void Ring()
        {
            if (!Enabled) return;
            try
            {
                if (ring == null)
                {
                    var notes = new List<double[]>();
                    for (int i = 0; i < 8; i++) notes.Add(new[] { i % 2 == 0 ? 1046.5 : 1318.5, i * 0.07, 0.12 });
                    ring = new SoundPlayer(new MemoryStream(Make(notes.ToArray(), 2.0)));
                }
                ring.PlayLooping();
            }
            catch (Exception) { }
        }

        public static void StopRing() { try { if (ring != null) ring.Stop(); } catch (Exception) { } }

        // Joining and leaving a call: a rising or falling blip.
        public static void Joined() { Play(ref joined, new[] { new[] { 659.3, 0.0, 0.12 }, new[] { 987.8, 0.08, 0.2 } }); }
        public static void Left() { Play(ref left, new[] { new[] { 987.8, 0.0, 0.12 }, new[] { 659.3, 0.08, 0.2 } }); }

        static void Play(ref SoundPlayer player, double[][] notes)
        {
            if (!Enabled) return;
            try
            {
                if (player == null) player = new SoundPlayer(new MemoryStream(Make(notes)));
                player.Play();
            }
            catch (Exception) { }
        }

        static byte[] Make(double[][] notes, double seconds = 0.48)
        {
            const int rate = 22050;
            // notes: frequency, start (s), length (s)
            int total = (int)(rate * seconds);
            var samples = new short[total];
            foreach (var n in notes)
            {
                int start = (int)(n[1] * rate), len = (int)(n[2] * rate);
                for (int i = 0; i < len && start + i < total; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-t * 9) * Math.Min(1, i / 60.0);
                    double v = Math.Sin(2 * Math.PI * n[0] * t) * 0.6 + Math.Sin(4 * Math.PI * n[0] * t) * 0.15;
                    samples[start + i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, samples[start + i] + v * env * 9000));
                }
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + total * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(total * 2);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }
    }
}
