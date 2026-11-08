using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace Buzzcord
{
    // Start menu / desktop shortcuts and the app identity Windows uses to group taskbar buttons.
    // Unsigned, Buzzcord runs inside PowerShell (Smart App Control blocks unsigned exes); the
    // shortcut starts it through conhost --headless so no console window flashes, and the shared
    // AppUserModelID makes the taskbar show Buzzcord's icon and pin the shortcut, not PowerShell.
    static class Shortcuts
    {
        public const string AppId = "MarcTurtal.Buzzcord";

        [DllImport("shell32.dll", SetLastError = true)]
        static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        public static void SetAppIdentity()
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch (Exception) { }
        }

        // Project folder (the one holding run.ps1 and bin\).
        public static string Home
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("BUZZCORD_HOME");
                if (!string.IsNullOrEmpty(env)) return env;
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                return string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir) : dir;
            }
        }

        // Folder holding Buzzcord.exe and its libraries: wherever the exe runs from (a release zip
        // unpacked anywhere), or the project's bin\ when PowerShell hosts it.
        public static string BinDir
        {
            get
            {
                string self = Application.ExecutablePath;
                if (string.Equals(Path.GetFileName(self), "Buzzcord.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(self);
                return Path.Combine(Home, "bin");
            }
        }

        static string Exe { get { return Path.Combine(BinDir, "Buzzcord.exe"); } }

        // Running as Buzzcord.exe itself (a release, signed or not), rather than hosted by
        // PowerShell from the source folder through run.ps1.
        public static bool RunningAsExe
        {
            get { return string.Equals(Path.GetFileName(Application.ExecutablePath), "Buzzcord.exe", StringComparison.OrdinalIgnoreCase); }
        }

        // Once the exe is code-signed, Windows lets it run directly.
        public static bool ExeIsSigned
        {
            get
            {
                try { X509Certificate.CreateFromSignedFile(Exe); return true; }
                catch (Exception) { return false; }
            }
        }

        // Program and arguments that start Buzzcord the way that works on this PC.
        public static void Command(bool background, out string target, out string args)
        {
            Command(background, RunningAsExe, ExeIsSigned, Exe, Home, out target, out args);
        }

        // A copy that runs as Buzzcord.exe starts the same way again: Windows already let this exe run.
        // A copy hosted by PowerShell goes back through run.ps1 unless the exe in bin\ is signed
        // (Smart App Control would block it unsigned).
        public static void Command(bool background, bool asExe, bool signed, string exe, string home, out string target, out string args)
        {
            if (asExe || signed)
            {
                target = exe;
                args = background ? "--background" : "";
                return;
            }
            target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
            args = "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File \"" + Path.Combine(home, "run.ps1") + "\"" + (background ? " --background" : "");
        }

        public static string CommandLine(bool background)
        {
            string target, args;
            Command(background, out target, out args);
            return "\"" + target + "\" " + args;
        }

        static string StartMenuPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Buzzcord.lnk"); }
        }

        static string DesktopPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Buzzcord.lnk"); }
        }

        // Creates/refreshes the Start menu shortcut every start (so it follows the folder if moved
        // or the exe gets signed); the desktop one only the first time, so deleting it sticks.
        public static void Ensure()
        {
            if (!RunningAsExe && !File.Exists(Path.Combine(Home, "run.ps1")) && !ExeIsSigned) return;
            try
            {
                Create(StartMenuPath);
                if (!Settings.GetBool("DesktopShortcutMade", false))
                {
                    Create(DesktopPath);
                    Settings.SetBool("DesktopShortcutMade", true);
                    Settings.Save();
                }
                else if (File.Exists(DesktopPath)) Create(DesktopPath);
            }
            catch (Exception ex) { Program.Log("shortcuts: " + ex.Message); }
        }

        static void Create(string path)
        {
            string target, args;
            Command(false, out target, out args);
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetArguments(args);
                link.SetWorkingDirectory(Home);
                link.SetDescription("Buzzcord - old-school messenger for Discord");
                string ico = Path.Combine(Home, "assets", "Buzzcord.ico");
                if (File.Exists(ico)) link.SetIconLocation(ico, 0);
                else if (File.Exists(Exe)) link.SetIconLocation(Exe, 0);   // release build: the exe carries the icon
                link.SetShowCmd(7); // minimized: the console host never shows anything anyway

                var store = (IPropertyStore)link;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var value = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(AppId) };
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally { Marshal.FreeCoTaskMem(value.p); }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ((IPersistFile)link).Save(path, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
            [FieldOffset(16)] public IntPtr pad; // PROPVARIANT is 24 bytes on 64-bit
        }
    }
}
