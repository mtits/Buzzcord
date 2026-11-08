using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Buzzcord")]
[assembly: AssemblyProduct("Buzzcord")]
[assembly: AssemblyDescription("A lightweight, old-school messenger for Discord")]
[assembly: AssemblyCompany("Marc Turtal")]
[assembly: AssemblyCopyright("Â© 2026 Marc Turtal")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: AssemblyFileVersion("1.3.1.0")]
[assembly: AssemblyInformationalVersion("1.3.1")]

namespace Buzzcord
{
    static class Program
    {
        public const string DonateUrl = "https://paypal.me/abstinencelolph";
        public const string SiteUrl = "https://app.marcturtal.com/buzzcord/";
        public const string SourceUrl = "https://github.com/mtits/Buzzcord";
        // From the exe's own version (AssemblyVersion below), e.g. "1.3.0".
        public static string Version { get { return typeof(Program).Assembly.GetName().Version.ToString(3); } }

        public static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log("open url: " + ex.Message); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            // Buzzcord is loaded from bytes, so .NET can't find the WebView2 DLLs beside it on its own.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(Shortcuts.BinDir, name + ".dll");
                if (name.StartsWith("Microsoft.Web.WebView2")) return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                // Concentus (Opus codec) isn't code-signed: it lives in voice\, out of Windows' way,
                // and is loaded from bytes, so Smart App Control never sees a DLL load.
                string voice = Path.Combine(Shortcuts.BinDir, "voice", name + ".dll");
                if (name == "Concentus" && File.Exists(voice)) return Assembly.Load(File.ReadAllBytes(voice));
                return null;
            };

            string export = Arg(args, "--export-icon");
            if (export != null)
            {
                File.WriteAllBytes(export, Icons.IcoBytes());
                return 0;
            }
            string selfTest = Arg(args, "--selftest");
            if (selfTest != null)
            {
                // Must happen before anything touches Settings: tests get their own data folder.
                Environment.SetEnvironmentVariable("BUZZCORD_DATA", Path.Combine(Path.GetTempPath(), "Buzzcord-selftest"));
                return SelfTest.Run(selfTest, args);
            }

            Native.TryPerMonitorDpi();
            Native.TuneBrowserEngine();
            Shortcuts.SetAppIdentity();
            Application.EnableVisualStyles();
            try { Application.SetCompatibleTextRenderingDefault(false); }
            catch (InvalidOperationException) { } // already set when hosted in another process

            bool firstInstance;
            // A copy with its own data folder (BUZZCORD_DATA, used for testing) is a separate instance.
            string data = Environment.GetEnvironmentVariable("BUZZCORD_DATA");
            string lockName = @"Local\Buzzcord.SingleInstance" + (data != null ? "." + (data.ToLowerInvariant().GetHashCode() & 0x7fffffff) : "");
            var mutex = new Mutex(true, lockName, out firstInstance);
            // Just updated: the old copy is still closing, so wait for it to let go.
            for (int i = 0; !firstInstance && Has(args, "--updated") && i < 40; i++)
            {
                mutex.Dispose();
                Thread.Sleep(500);
                mutex = new Mutex(true, lockName, out firstInstance);
            }
            // An update downloaded earlier goes in now, before anything opens: let go of the lock,
            // swap the files, start the new copy (which waits for this one to finish) and leave.
            if (firstInstance && !Has(args, "--updated") && Updater.CanInstall && Updater.Staged != null && Settings.GetBool("AutoUpdate", true))
            {
                try
                {
                    mutex.Dispose();
                    Updater.InstallAndRestart(Has(args, "--background"));
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("update install: " + ex);
                    Settings.Set("UpdateStaged", "");
                    Settings.Save();
                    mutex = new Mutex(true, lockName, out firstInstance);
                }
            }
            using (mutex)
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Buzzcord is already running. Find it in the notification area by the clock.", "Buzzcord");
                    return 0;
                }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log("unhandled: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("fatal: " + e.ExceptionObject);
                TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); };
                Application.Run(new AppContext(Has(args, "--background")));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static readonly object logGate = new object();

        public static void Log(string text)
        {
            try
            {
                lock (logGate)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    string path = Path.Combine(Settings.Folder, "buzzcord.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }

        static bool Has(string[] args, string name)
        {
            foreach (var a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    // Owns the tray icon, the rich presence pipe and one buddy list per account. The app keeps
    // running in the tray until Exit.
    public class AppContext : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly List<BuddyListForm> windows = new List<BuddyListForm>();
        readonly RpcServer rpc = new RpcServer();
        readonly System.Windows.Forms.Timer pipeTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        readonly Control invoker = new Control();
        readonly System.Windows.Forms.Timer badgeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        bool hinted;

        public int WindowCount { get { return windows.Count; } }
        public List<BuddyListForm> Windows { get { return windows; } }

        public AppContext(bool background) : this(background, false) { }

        // testOnly: no tray icon, pipe or accounts; just enough for the self-test to host windows.
        public AppContext(bool background, bool testOnly)
        {
            if (testOnly) return;
            invoker.CreateControl();
            // Async continuations need the WinForms context before the first form exists.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            PresenceManager.Instance = new PresenceManager();
            var ui = SynchronizationContext.Current;
            Captcha.Solve = challenge => CaptchaForm.Ask(challenge, ui);
            PresenceManager.Instance.Detector.Enabled = Settings.GetBool("DetectGames", true);

            Theme.Init();
            Notifier.App = this;
            tray.Icon = Icons.AppIcon(16);
            tray.Text = "Buzzcord";
            tray.Visible = true;
            tray.DoubleClick += (s, e) => ShowAll();
            tray.BalloonTipClicked += (s, e) => BalloonClicked();
            badgeTimer.Tick += (s, e) => UpdateBadge();
            badgeTimer.Start();
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                foreach (var w in windows)
                {
                    var win = w;
                    string name = win.Session.Me != null ? win.Session.Me.DisplayName : "Signing in...";
                    menu.Items.Add(name, Icons.Status(win.Session.Status, 16), (a, b) => ShowWindow(win));
                }
                menu.Items.Add(new ToolStripSeparator());
                var hk = GlobalHotkey.Saved;
                menu.Items.Add(new ToolStripMenuItem("Show buddy lists", null, (a, b) => ShowAll()) { ShortcutKeyDisplayString = hk != null ? hk.ToString() : null });
                menu.Items.Add(new ToolStripMenuItem("Unread messages", null, (a, b) => UnreadForm.Open(this)) { ShortcutKeyDisplayString = "Ctrl+Shift+U" });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Add account...", null, (a, b) => AddAccount());
                menu.Items.Add("Rich presence status", null, (a, b) => MessageBox.Show(RpcStatus(), "Buzzcord"));
                menu.Items.Add("Donate \u2665", null, (a, b) => DonateForm.Open(null));
                if (Updater.Staged != null && Updater.CanInstall)
                    menu.Items.Add(new ToolStripMenuItem("Restart to update to " + Updater.StagedVersion, null, (a, b) => InstallUpdate(false)) { Font = new Font(menu.Font, FontStyle.Bold) });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (a, b) => ExitAll());
                e.Cancel = false;
            };
            tray.ContextMenuStrip = menu;

            Shortcuts.Ensure();
            // Start with Windows is chosen in the first-run notice (or Options); refresh the command
            // so it matches how Buzzcord launches now (signed exe, or the PowerShell route).
            if (Settings.GetBool("StartWithWindows", false)) SetStartWithWindows(true);

            rpc.Start();
            pipeTimer.Tick += (s, e) => EnsurePipe();
            pipeTimer.Start();

            AutoAway.Start(() => windows.ConvertAll(w => w.Session));
            GlobalHotkey.Instance = new GlobalHotkey(ShowAll);
            if (!GlobalHotkey.Instance.Apply())
                tray.ShowBalloonTip(6000, "Buddy list shortcut is off", GlobalHotkey.Instance.Problem, ToolTipIcon.Warning);

            // Updates: first look 20 s after start, then every 6 hours.
            updateTimer.Tick += (s, e) => { updateTimer.Interval = 6 * 3600 * 1000; CheckForUpdates(false); };
            updateTimer.Start();
            string from = Settings.Get("UpdatedFrom", "");
            if (from.Length > 0)
            {
                Settings.Set("UpdatedFrom", "");
                Settings.Save();
                Task.Run(() => Updater.CleanupOld(Shortcuts.BinDir));
                BalloonAction(6000, "Buzzcord is up to date", "Updated to " + Program.Version + ". Click to see what's new.", () => Program.OpenUrl(Updater.ReleasesUrl + "/latest"));
            }

            Start(background);
        }

        async void Start(bool background)
        {
            // Read the web app's current build number before connecting (once a day).
            await Task.Run(() => ClientInfo.LoadBuildNumber());
            PresenceManager.Instance.Detector.Start();
            var tokens = Accounts.Load();
            if (tokens.Count == 0)
            {
                if (!NoticeForm.Accept(null) || !AddAccount()) { ExitAll(); return; }
            }
            else
                foreach (var t in tokens) Open(t, !background);
        }

        BuddyListForm Open(string token, bool show)
        {
            var session = new Session(token);
            PresenceManager.Instance.Sessions.Add(session);
            var w = new BuddyListForm(session, this);
            windows.Add(w);
            session.Start();
            if (show) w.Show();
            return w;
        }

        public bool AddAccount()
        {
            using (var login = new LoginForm())
            {
                if (login.ShowDialog() != DialogResult.OK || login.Token == null) return false;
                foreach (var w in windows)
                    if (w.Session.Token == login.Token) { ShowWindow(w); return true; }
                var win = Open(login.Token, true);
                PersistAccounts();
                ShowWindow(win);
                return true;
            }
        }

        // The accounts file always mirrors the accounts that are signed in right now.
        void PersistAccounts()
        {
            var tokens = new List<string>();
            foreach (var w in windows) tokens.Add(w.Session.Token);
            Accounts.Save(tokens);
        }

        public void RemoveAccount(BuddyListForm w, string reason)
        {
            Program.Log("accounts: removing " + (w.Session.Me != null ? w.Session.Me.Username : "?") + " (" + reason + ")");
            w.Session.Stop();
            PresenceManager.Instance.Sessions.Remove(w.Session);
            windows.Remove(w);
            PersistAccounts();
            w.Exiting = true;
            w.Close();
            if (windows.Count == 0 && !AddAccount()) ExitAll();
        }

        public void ExitAll()
        {
            badgeTimer.Stop();
            pipeTimer.Stop();
            updateTimer.Stop();
            foreach (var w in new List<BuddyListForm>(windows))
            {
                w.Session.Stop();
                w.Exiting = true;
                w.Close();
            }
            rpc.Stop();
            AutoAway.Stop();
            if (GlobalHotkey.Instance != null) GlobalHotkey.Instance.Dispose();
            tray.Visible = false;
            tray.Dispose();
            Settings.Save();
            ExitThread();
        }

        // Every buddy list to the front: tray double-click and the global shortcut.
        public void ShowAll()
        {
            foreach (var w in windows) ShowWindow(w);
        }

        static void ShowWindow(BuddyListForm w)
        {
            w.Show();
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
        }

        public void HintTray()
        {
            if (hinted) return;
            hinted = true;
            tray.ShowBalloonTip(4000, "Buzzcord is still running", "You're still signed in. Double-click the tray icon to bring your buddy list back; right-click it to exit.", ToolTipIcon.Info);
        }

        // What the last balloon was about, so clicking it opens that chat (or profile).
        BuddyListForm balloonFrom;
        string balloonChannel, balloonUser;

        public void Balloon(BuddyListForm from, string title, string text, string channelId, string userId)
        {
            if (string.IsNullOrEmpty(text)) text = "(sent a file)";
            if (text.Length > 200) text = text.Substring(0, 200) + "...";
            string prefix = windows.Count > 1 && from.Session.Me != null ? "[" + from.Session.Me.DisplayName + "] " : "";
            balloonAction = null;
            balloonFrom = from;
            balloonChannel = channelId;
            balloonUser = userId;
            tray.ShowBalloonTip(5000, prefix + title, text, ToolTipIcon.None);
        }

        public void QuickReply(BuddyListForm from, string title, string text, string channelId, string messageId)
        {
            string account = windows.Count > 1 && from.Session.Me != null ? from.Session.Me.DisplayName : null;
            QuickReplyForm.Show(from, title, text, channelId, messageId, account);
        }

        Action balloonAction;

        // A tray balloon that runs an action when clicked (updates, donations).
        public void BalloonAction(int ms, string title, string text, Action clicked)
        {
            balloonFrom = null;
            balloonAction = clicked;
            tray.ShowBalloonTip(ms, title, text, ToolTipIcon.Info);
        }

        // ---- updates ----

        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        bool checking;

        // manual: from Help > Check for updates, which always answers. The timer stays quiet unless
        // there's news, and only runs while automatic updates are on.
        public async void CheckForUpdates(bool manual)
        {
            if (checking || (!manual && !Settings.GetBool("AutoUpdate", true))) return;
            checking = true;
            try
            {
                if (Updater.Staged != null) { UpdateReady(manual, null); return; }
                var r = await Updater.Check();
                if (r == null)
                {
                    if (manual) MessageBox.Show("You have the latest Buzzcord (" + Program.Version + ").", "Buzzcord");
                    return;
                }
                if (!Updater.CanInstall)
                {
                    // Running from source: just say so, once per release.
                    if (manual || Settings.Get("UpdateNoticed", "") != r.Tag)
                    {
                        Settings.Set("UpdateNoticed", r.Tag);
                        Settings.Save();
                        BalloonAction(8000, "Buzzcord " + r.Version.ToString(3) + " is out", "Click to open the download page.", () => Program.OpenUrl(r.PageUrl));
                    }
                    return;
                }
                await Updater.Download(r);
                Program.Log("update: " + r.Tag + " downloaded and verified");
                UpdateReady(manual, r);
            }
            catch (Exception ex)
            {
                Program.Log("update: " + ex.Message);
                if (manual) MessageBox.Show("Couldn't check for updates: " + ex.Message, "Buzzcord");
            }
            finally { checking = false; }
        }

        void UpdateReady(bool manual, Updater.Release r)
        {
            string v = Updater.StagedVersion;
            if (manual)
            {
                string notes = r != null ? Updater.Summary(r) : "";
                var answer = MessageBox.Show("Buzzcord " + v + " is ready to install." + (notes.Length > 0 ? "\n\n" + notes : "") +
                    "\n\nRestart now to update? (Otherwise it installs the next time Buzzcord starts.)", "Update ready", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) InstallUpdate(false);
                return;
            }
            BalloonAction(10000, "Buzzcord " + v + " is ready", "Click to restart and update now, or it installs the next time Buzzcord starts.", () => InstallUpdate(false));
        }

        // Swaps in the downloaded files and restarts. False if it couldn't (Buzzcord keeps running).
        public bool InstallUpdate(bool background)
        {
            try
            {
                Updater.InstallAndRestart(background);
                ExitAll();
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("update install: " + ex);
                Settings.Set("UpdateStaged", "");
                Settings.Save();
                MessageBox.Show("Couldn't install the update: " + ex.Message + "\n\nYou can download it from " + Updater.ReleasesUrl + ".", "Buzzcord");
                return false;
            }
        }

        void BalloonClicked()
        {
            if (balloonAction != null) { var a = balloonAction; balloonAction = null; a(); return; }
            var w = balloonFrom;
            if (w == null || w.IsDisposed) { ShowAll(); return; }
            if (balloonChannel != null) w.OpenChatById(balloonChannel);
            else if (balloonUser != null) ProfileForm.Open(w.Session, balloonUser, null);
            else ShowWindow(w);
            balloonFrom = null;
        }

        // Red dot on the tray icon and a count in its tooltip while anything is unread.
        int lastUnread = -1;

        void UpdateBadge()
        {
            int total = 0;
            foreach (var w in windows)
            {
                int n = w.UnreadCount();
                total += n;
                w.SetTaskbarBadge(n);
            }
            if (total == lastUnread) return;
            lastUnread = total;
            var old = tray.Icon;
            tray.Icon = Icons.AppIcon(16, total > 0);
            tray.Text = total > 0 ? "Buzzcord - " + total + " unread" : "Buzzcord";
            if (old != null) old.Dispose();
        }

        void EnsurePipe()
        {
            // If the official Discord app had discord-ipc-0 when we started and has since quit,
            // take it over so games find us first.
            if (rpc.PipeName != "discord-ipc-0") rpc.TryClaimPrimary();
        }

        public string RpcStatus()
        {
            if (rpc.PipeName == null) return "Rich presence is not available: every Discord pipe is taken.";
            string text = "Games' rich presence is received on \\\\.\\pipe\\" + rpc.PipeName + ".";
            if (rpc.PipeName != "discord-ipc-0")
                text += "\n\nThe official Discord app is holding discord-ipc-0, so games will talk to it first. Quit Discord (from its tray icon) and Buzzcord will take over within 30 seconds.";
            var running = new List<string>();
            foreach (var g in PresenceManager.Instance.Detector.Running) running.Add(g.Name);
            text += "\n\nDetected games running: " + (running.Count > 0 ? string.Join(", ", running) : "none");
            var acts = PresenceManager.Instance.ActivitiesFor(null);
            text += "\nShared activity: " + (acts.Count > 0 ? Presence.Describe(acts[0]) : "nothing right now");
            return text;
        }

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return k != null && k.GetValue("Buzzcord") != null;
            }
        }

        public static void SetStartWithWindows(bool on)
        {
            Settings.SetBool("StartWithWindows", on);
            Settings.Save();
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (!on) { k.DeleteValue("Buzzcord", false); return; }
                k.SetValue("Buzzcord", Shortcuts.CommandLine(true));
            }
        }
    }
}
