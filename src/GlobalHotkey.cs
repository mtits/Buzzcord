using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Buzzcord
{
    // A key combination, saved in settings as text like "Ctrl+Alt+B".
    public class HotkeyCombo
    {
        public const int ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;

        public int Modifiers;   // RegisterHotKey's MOD_* flags
        public Keys Key;

        public static readonly HotkeyCombo Standard = new HotkeyCombo { Modifiers = ModControl | ModAlt, Key = Keys.B };

        // Needs Ctrl, Alt or Win: Shift alone would eat ordinary typing.
        public static bool Usable(int modifiers) { return (modifiers & (ModControl | ModAlt | ModWin)) != 0; }

        public bool IsUsable { get { return Usable(Modifiers) && Key != Keys.None && !IsModifierKey(Key); } }

        static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin
                || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.LMenu || k == Keys.RMenu;
        }

        // Ctrl+Alt+Shift+Win order, as Windows menus show it.
        public override string ToString()
        {
            var parts = new List<string>();
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            switch (k)
            {
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                default: return k.ToString();
            }
        }

        // Accepts "Ctrl+Alt+B", "ctrl + alt + f5", "Win+Shift+9". Null when it can't be read.
        public static HotkeyCombo Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var combo = new HotkeyCombo();
            foreach (var raw in text.Split('+'))
            {
                string p = raw.Trim();
                if (p.Length == 0) return null;
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": combo.Modifiers |= ModControl; continue;
                    case "alt": combo.Modifiers |= ModAlt; continue;
                    case "shift": combo.Modifiers |= ModShift; continue;
                    case "win": case "windows": combo.Modifiers |= ModWin; continue;
                }
                if (combo.Key != Keys.None) return null; // two non-modifier keys
                Keys k;
                if (p.Length == 1 && char.IsDigit(p[0])) k = Keys.D0 + (p[0] - '0');
                else if (p == ",") k = Keys.Oemcomma;
                else if (p == ".") k = Keys.OemPeriod;
                else if (p == "-") k = Keys.OemMinus;
                else if (p == "=") k = Keys.Oemplus;
                else if (char.IsDigit(p[0]) || !Enum.TryParse(p, true, out k) || (int)k <= 0 || (int)k > 0xFE) return null; // numbers would parse as raw key codes
                combo.Key = k;
            }
            return combo.Key == Keys.None ? null : combo;
        }

        public static HotkeyCombo FromKeyEvent(KeyEventArgs e)
        {
            var c = new HotkeyCombo { Key = e.KeyCode };
            if (e.Control) c.Modifiers |= ModControl;
            if (e.Alt) c.Modifiers |= ModAlt;
            if (e.Shift) c.Modifiers |= ModShift;
            if (IsWinDown()) c.Modifiers |= ModWin;
            return c;
        }

        [DllImport("user32.dll")]
        static extern short GetKeyState(int key);

        static bool IsWinDown() { return GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0; }
    }

    // The buddy list shortcut (default Ctrl+Alt+B): brings every buddy list to the front from
    // any app. It uses RegisterHotKey, so Windows only tells Buzzcord about that one combination.
    public class GlobalHotkey : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312, MOD_NOREPEAT = 0x4000, Id = 0xB022;

        public static GlobalHotkey Instance;
        readonly Action action;
        bool registered;

        public string Problem { get; private set; }

        public GlobalHotkey(Action action)
        {
            this.action = action;
            CreateHandle(new CreateParams());
        }

        // Null when turned off.
        public static HotkeyCombo Saved
        {
            get
            {
                if (Settings.GetBool("HotkeyOff", false)) return null;
                return HotkeyCombo.Parse(Settings.Get("Hotkey", "")) ?? HotkeyCombo.Standard;
            }
        }

        public static void Save(HotkeyCombo combo)
        {
            Settings.SetBool("HotkeyOff", combo == null);
            if (combo != null) Settings.Set("Hotkey", combo.ToString());
            Settings.Save();
        }

        // Registers the saved shortcut. False (with Problem set) when another app already has it.
        public bool Apply()
        {
            Unregister();
            Problem = null;
            var c = Saved;
            if (c == null) return true;
            if (!RegisterHotKey(Handle, Id, c.Modifiers | MOD_NOREPEAT, (int)c.Key))
            {
                int err = Marshal.GetLastWin32Error();
                Problem = err == 1409
                    ? c + " is already used by another app, so the buddy list shortcut is off. Pick another in Options > Buddy list shortcut."
                    : "Windows wouldn't register " + c + " (error " + err + ").";
                Program.Log("hotkey: " + Problem);
                return false;
            }
            registered = true;
            return true;
        }

        void Unregister()
        {
            if (registered) UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id)
            {
                try { action(); } catch (Exception ex) { Program.Log("hotkey action: " + ex.Message); }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    // Options > Buddy list shortcut: press the new keys, or turn it off.
    public class HotkeyForm : Form
    {
        readonly TextBox box = new TextBox { ReadOnly = true, Width = 220, TextAlign = HorizontalAlignment.Center };
        readonly CheckBox on = new CheckBox { Text = "Use a shortcut to bring the buddy lists forward", AutoSize = true };
        readonly Label note = new Label { AutoSize = false, Width = 300, Height = 44 };
        HotkeyCombo combo;

        public HotkeyForm()
        {
            Text = "Buddy list shortcut";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Tahoma", 8.25f);
            ClientSize = new Size(324, 170);
            KeyPreview = true;

            combo = GlobalHotkey.Saved;
            on.Checked = combo != null;
            if (combo == null) combo = HotkeyCombo.Parse(Settings.Get("Hotkey", "")) ?? HotkeyCombo.Standard;

            on.Location = new Point(12, 12);
            var label = new Label { Text = "Click the box and press the keys (needs Ctrl, Alt or Win):", Location = new Point(12, 40), AutoSize = true };
            box.Location = new Point(52, 60);
            box.Text = combo.ToString();
            box.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                var c = HotkeyCombo.FromKeyEvent(e);
                if (c.Key == Keys.ControlKey || c.Key == Keys.ShiftKey || c.Key == Keys.Menu || c.Key == Keys.LWin || c.Key == Keys.RWin) return;
                if (!c.IsUsable) { note.Text = "Add Ctrl, Alt or Win: a shortcut with only Shift would get in the way of typing."; return; }
                combo = c;
                box.Text = c.ToString();
                note.Text = "";
            };
            note.Location = new Point(12, 88);
            note.ForeColor = Color.DarkRed;
            var ok = new Button { Text = "OK", Location = new Point(154, 134) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(235, 134) };
            ok.Click += (s, e) => Save();
            on.CheckedChanged += (s, e) => box.Enabled = on.Checked;
            box.Enabled = on.Checked;
            Controls.AddRange(new Control[] { on, label, box, note, ok, cancel });
            CancelButton = cancel;
            Theme.Apply(this);
        }

        // Escape and Enter would otherwise never reach the box as a shortcut; keep them as
        // Cancel and OK instead.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { Save(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Save()
        {
            var old = GlobalHotkey.Saved;
            GlobalHotkey.Save(on.Checked ? combo : null);
            var hk = GlobalHotkey.Instance;
            if (hk != null && !hk.Apply())
            {
                note.Text = hk.Problem;
                GlobalHotkey.Save(old);
                hk.Apply();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
