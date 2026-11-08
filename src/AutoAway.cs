using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Buzzcord
{
    // Yahoo-style Auto-Away: after a few minutes without using the PC, or as soon as Windows
    // locks, every Available account shows as Away; it goes back the moment you're back. Your
    // chosen status is never changed, so Busy and Invisible stay as they are.
    public static class AutoAway
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static readonly int[] Choices = { 0, 5, 10, 15, 30 };   // 0 = Off

        static readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
        static Func<IEnumerable<Session>> sessions;
        static bool locked;

        public static int Minutes
        {
            get { int m; return int.TryParse(Settings.Get("AutoAwayMinutes", "10"), out m) && m >= 0 ? m : 10; }
            set { Settings.Set("AutoAwayMinutes", value.ToString()); Settings.Save(); Check(); }
        }

        public static bool WhenLocked
        {
            get { return Settings.GetBool("AwayWhenLocked", true); }
            set { Settings.SetBool("AwayWhenLocked", value); Settings.Save(); Check(); }
        }

        public static void Start(Func<IEnumerable<Session>> source)
        {
            sessions = source;
            timer.Tick += (s, e) => Check();
            timer.Start();
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        public static void Stop()
        {
            timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }

        // Windows raises this on the app's UI thread when the workstation locks or unlocks.
        static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
            else return;
            Check();
        }

        // Seconds since the last key press, click or mouse move anywhere in this Windows session.
        public static double IdleSeconds
        {
            get
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (!GetLastInputInfo(ref info)) return 0;
                return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
            }
        }

        public static bool ShouldBeAway(double idleSeconds, bool isLocked, int minutes, bool whenLocked)
        {
            return isLocked && whenLocked || minutes > 0 && idleSeconds >= minutes * 60;
        }

        public static void Check()
        {
            if (sessions == null) return;
            bool away = ShouldBeAway(IdleSeconds, locked, Minutes, WhenLocked);
            foreach (var s in sessions()) s.SetAutoAway(away);
        }
    }
}
