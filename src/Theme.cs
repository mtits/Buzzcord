using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Buzzcord
{
    // Light (the classic look) and dark palettes. Every colour has a role with a light and a dark
    // value; Apply() swaps a control's colours to the same role in the current palette, so forms
    // re-colour live when the theme changes without each one listing its controls.
    public static class Theme
    {
        enum Role { Panel, Window, Text, SubText, Dim, Accent, AccentLight, HeaderTop, HeaderBottom, HeaderLine,
            GroupBack, GroupEnd, Selected, Hover, Link, Playing, Border, Placeholder, Banner, Button }

        static readonly Color[] Light = {
            Color.FromArgb(0xF3, 0xF1, 0xF9), Color.White, Color.Black, Color.Gray, Color.DimGray,
            Color.FromArgb(0x5B, 0x2C, 0x8F), Color.FromArgb(0x9D, 0x7B, 0xC9),
            Color.FromArgb(0xFF, 0xF4, 0xB8), Color.FromArgb(0xFF, 0xD8, 0x4A), Color.FromArgb(0xC8, 0xA0, 0x20),
            Color.FromArgb(0xE8, 0xE4, 0xF4), Color.FromArgb(0xD6, 0xCE, 0xEC), Color.FromArgb(0xDC, 0xD2, 0xF2), Color.FromArgb(0xF3, 0xEF, 0xFB),
            Color.FromArgb(0x2A, 0x3E, 0xC0), Color.FromArgb(0x2E, 0x7D, 0x32), Color.Silver, Color.Gainsboro,
            Color.FromArgb(0xFF, 0xF6, 0xCC), SystemColors.Control };

        static readonly Color[] Dark = {
            Color.FromArgb(0x1F, 0x1B, 0x2A), Color.FromArgb(0x17, 0x14, 0x1F), Color.FromArgb(0xE8, 0xE4, 0xF2), Color.FromArgb(0x9A, 0x93, 0xAD), Color.FromArgb(0xB4, 0xAD, 0xC6),
            Color.FromArgb(0xC3, 0xA6, 0xF7), Color.FromArgb(0x4A, 0x3B, 0x6B),
            Color.FromArgb(0x3D, 0x30, 0x5E), Color.FromArgb(0x2A, 0x21, 0x42), Color.FromArgb(0x8A, 0x6D, 0x1F),
            Color.FromArgb(0x2A, 0x24, 0x38), Color.FromArgb(0x23, 0x1E, 0x30), Color.FromArgb(0x3B, 0x31, 0x57), Color.FromArgb(0x26, 0x20, 0x35),
            Color.FromArgb(0x8F, 0xA8, 0xFF), Color.FromArgb(0x7B, 0xD3, 0x89), Color.FromArgb(0x3A, 0x34, 0x48), Color.FromArgb(0x2C, 0x27, 0x38),
            Color.FromArgb(0x3A, 0x32, 0x20), Color.FromArgb(0x2C, 0x26, 0x40) };

        static bool dark;
        static SynchronizationContext ui;
        public static event Action Changed;

        public static bool IsDark { get { return dark; } }
        static Color C(Role r) { return (dark ? Dark : Light)[(int)r]; }

        public static Color Panel { get { return C(Role.Panel); } }
        public static Color Window { get { return C(Role.Window); } }
        public static Color Text { get { return C(Role.Text); } }
        public static Color SubText { get { return C(Role.SubText); } }
        public static Color Dim { get { return C(Role.Dim); } }
        public static Color Accent { get { return C(Role.Accent); } }
        public static Color AccentLight { get { return C(Role.AccentLight); } }
        public static Color HeaderTop { get { return C(Role.HeaderTop); } }
        public static Color HeaderBottom { get { return C(Role.HeaderBottom); } }
        public static Color HeaderLine { get { return C(Role.HeaderLine); } }
        public static Color GroupBack { get { return C(Role.GroupBack); } }
        public static Color GroupEnd { get { return C(Role.GroupEnd); } }
        public static Color Selected { get { return C(Role.Selected); } }
        public static Color Hover { get { return C(Role.Hover); } }
        public static Color Link { get { return C(Role.Link); } }
        public static Color Playing { get { return C(Role.Playing); } }
        public static Color Border { get { return C(Role.Border); } }
        public static Color Placeholder { get { return C(Role.Placeholder); } }
        public static Color Banner { get { return C(Role.Banner); } }

        // "system" (follow Windows), "light" or "dark".
        public static string Mode { get { return Settings.Get("Theme", "system"); } }

        public static void Init()
        {
            ui = SynchronizationContext.Current;
            dark = WantDark();
            ToolStripManager.Renderer = new Renderer();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                if (ui != null) ui.Post(_ => Refresh(), null); else Refresh();
            };
        }

        public static void SetMode(string mode)
        {
            Settings.Set("Theme", mode);
            Settings.Save();
            Refresh();
        }

        static bool WantDark()
        {
            string mode = Mode;
            if (mode == "dark") return true;
            if (mode == "light") return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        // Re-evaluates the theme and re-colours every open window if it changed.
        public static void Refresh()
        {
            bool want = WantDark();
            if (want == dark) return;
            dark = want;
            foreach (Form f in Application.OpenForms)
            {
                Apply(f);
                f.Invalidate(true);
            }
            if (Changed != null) Changed();
        }

        // Colours a new form now, and runs onChange (for HTML views and custom painting) whenever
        // the theme changes while it's open.
        public static void Attach(Form f, Action onChange)
        {
            Apply(f);
            if (onChange == null) return;
            Action handler = () => { if (!f.IsDisposed) onChange(); };
            Changed += handler;
            f.FormClosed += (s, e) => Changed -= handler;
        }

        // Test hook: force a palette without touching settings.
        public static void ForceForTest(bool isDark)
        {
            if (isDark == dark) return;
            dark = isDark;
            foreach (Form f in Application.OpenForms) { Apply(f); f.Invalidate(true); }
            if (Changed != null) Changed();
        }

        // The colour in the current palette that plays the same role as c (in either palette).
        public static Color Map(Color c)
        {
            int argb = c.ToArgb();
            var cur = dark ? Dark : Light;
            for (int i = 0; i < Light.Length; i++)
                if (Light[i].ToArgb() == argb || Dark[i].ToArgb() == argb) return cur[i];
            if (argb == SystemColors.Window.ToArgb()) return Window;
            if (argb == SystemColors.ControlText.ToArgb() || argb == SystemColors.WindowText.ToArgb()) return Text;
            return c;
        }

        public static void Apply(Control c)
        {
            var form = c as Form;
            if (form != null)
            {
                if (form.IsHandleCreated) Native.SetDarkTitleBar(form.Handle, dark);
                else
                {
                    EventHandler once = null;
                    once = (s, e) => { Native.SetDarkTitleBar(form.Handle, dark); form.HandleCreated -= once; };
                    form.HandleCreated += once;
                }
            }

            if (c is WebBrowser) return; // themed by its page's CSS
            var tb = c as TextBoxBase;
            var button = c as Button;
            if (tb != null)
            {
                tb.BackColor = Window;
                tb.ForeColor = Text;
                ThemeScrollbars(tb);
            }
            else if (button != null)
            {
                if (dark)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = AccentLight;
                    button.BackColor = C(Role.Button);
                    button.ForeColor = Text;
                }
                else
                {
                    button.FlatStyle = FlatStyle.Standard;
                    button.BackColor = SystemColors.Control;
                    button.UseVisualStyleBackColor = true;
                    button.ForeColor = SystemColors.ControlText;
                }
            }
            else if (c is ScrollBar) ThemeScrollbars(c);
            else
            {
                if (!(c is ToolStrip) || c.BackColor != SystemColors.Control) c.BackColor = Map(c.BackColor);
                c.ForeColor = Map(c.ForeColor);
            }
            var strip = c as ToolStrip;
            if (strip != null)
            {
                strip.BackColor = Panel;
                strip.ForeColor = Text;
                if (strip.RenderMode != ToolStripRenderMode.ManagerRenderMode) strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            }
            foreach (Control child in c.Controls) Apply(child);
        }

        static void ThemeScrollbars(Control c)
        {
            if (c.IsHandleCreated) Native.SetControlTheme(c.Handle, dark);
            else
            {
                EventHandler once = null;
                once = (s, e) => { Native.SetControlTheme(c.Handle, dark); c.HandleCreated -= once; };
                c.HandleCreated += once;
            }
        }

        static string Hex(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        // Colours for the HTML views (chat history, search, profile). IE11 has no CSS variables,
        // so the rules are generated per theme and swapped by the page's setTheme().
        public static string ChatCss()
        {
            var sb = new StringBuilder();
            sb.Append("html,body{background:").Append(Hex(Window)).Append(";color:").Append(Hex(Text)).Append(";}");
            sb.Append("a{color:").Append(Hex(Link)).Append(";}");
            sb.Append(".m:hover{background:").Append(Hex(Hover)).Append(";}");
            sb.Append(".t,.ed{color:").Append(Hex(SubText)).Append(";}");
            sb.Append(".me{color:").Append(dark ? "#7fa7ff" : "#1f4fa0").Append(";}");
            sb.Append(".them{color:").Append(dark ? "#ff7b84" : "#b0141c").Append(";}");
            sb.Append(".sys,.rp{color:").Append(Hex(SubText)).Append(";}");
            sb.Append(".act{background:").Append(Hex(Window)).Append(";border-color:").Append(Hex(AccentLight)).Append(";}");
            sb.Append(".act a{color:").Append(Hex(Accent)).Append(";}");
            sb.Append(".media img{border-color:").Append(Hex(Border)).Append(";}");
            sb.Append(".file{background:").Append(Hex(Hover)).Append(";border-color:").Append(Hex(AccentLight)).Append(";}");
            sb.Append(".emb{background:").Append(Hex(Panel)).Append(";border-left-color:").Append(Hex(AccentLight)).Append(";}");
            sb.Append(".emb .ed2{color:").Append(Hex(Dim)).Append(";}");
            sb.Append("code,pre{background:").Append(Hex(GroupBack)).Append(";color:").Append(Hex(Text)).Append(";}");
            sb.Append("blockquote{border-left-color:").Append(Hex(Border)).Append(";color:").Append(Hex(Dim)).Append(";}");
            sb.Append(".mention{background:").Append(Hex(Selected)).Append(";color:").Append(Hex(Accent)).Append(";}");
            sb.Append(".hl{background:").Append(dark ? "#3a3320" : "#fff6cc").Append(";}");
            sb.Append(".re{background:").Append(Hex(Hover)).Append(";border-color:").Append(Hex(AccentLight)).Append(";color:").Append(Hex(Text)).Append(";}");
            sb.Append(".re.mine{background:").Append(Hex(Selected)).Append(";border-color:").Append(Hex(Accent)).Append(";}");
            sb.Append(".day{color:").Append(Hex(SubText)).Append(";border-bottom-color:").Append(Hex(Border)).Append(";}");
            sb.Append(".sec{border-top-color:").Append(Hex(Border)).Append(";}");
            sb.Append(".h{color:").Append(Hex(Accent)).Append(";}");
            sb.Append(".user{color:").Append(Hex(SubText)).Append(";}");
            sb.Append(".pill{background:").Append(Hex(Hover)).Append(";border-color:").Append(Hex(AccentLight)).Append(";color:").Append(Hex(Text)).Append(";}");
            sb.Append(".av,.st{border-color:").Append(Hex(Window)).Append(";background-color:").Append(Hex(Window)).Append(";}");
            sb.Append("::-webkit-scrollbar{width:0}"); // harmless in IE
            if (dark) sb.Append("html{scrollbar-base-color:#2a2438;scrollbar-arrow-color:#b4adc6;scrollbar-track-color:#17141f;}");
            return sb.ToString();
        }

        // Menus, toolbars and status bars in either palette.
        class Renderer : ToolStripProfessionalRenderer
        {
            public Renderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // Items with their own colour (BUZZ!) keep it; everything else follows the theme.
                bool custom = e.Item.ForeColor != SystemColors.ControlText && e.Item.ForeColor != Light[(int)Role.Text]
                    && e.Item.ForeColor != Dark[(int)Role.Text] && e.Item.ForeColor.ToArgb() != Color.Black.ToArgb();
                if (!custom) e.TextColor = e.Item.Enabled ? Text : SubText;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e); // menus keep a frame
            }
        }

        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin { get { return Panel; } }
            public override Color ToolStripGradientMiddle { get { return Panel; } }
            public override Color ToolStripGradientEnd { get { return Panel; } }
            public override Color MenuStripGradientBegin { get { return Panel; } }
            public override Color MenuStripGradientEnd { get { return Panel; } }
            public override Color StatusStripGradientBegin { get { return Panel; } }
            public override Color StatusStripGradientEnd { get { return Panel; } }
            public override Color ToolStripDropDownBackground { get { return dark ? Window : Color.White; } }
            public override Color ImageMarginGradientBegin { get { return dark ? Panel : Color.FromArgb(0xF7, 0xF5, 0xFC); } }
            public override Color ImageMarginGradientMiddle { get { return ImageMarginGradientBegin; } }
            public override Color ImageMarginGradientEnd { get { return ImageMarginGradientBegin; } }
            public override Color MenuBorder { get { return AccentLight; } }
            public override Color MenuItemBorder { get { return AccentLight; } }
            public override Color MenuItemSelected { get { return Selected; } }
            public override Color MenuItemSelectedGradientBegin { get { return Selected; } }
            public override Color MenuItemSelectedGradientEnd { get { return Selected; } }
            public override Color MenuItemPressedGradientBegin { get { return Selected; } }
            public override Color MenuItemPressedGradientEnd { get { return Selected; } }
            public override Color ButtonSelectedHighlight { get { return Selected; } }
            public override Color ButtonSelectedGradientBegin { get { return Selected; } }
            public override Color ButtonSelectedGradientMiddle { get { return Selected; } }
            public override Color ButtonSelectedGradientEnd { get { return Selected; } }
            public override Color ButtonSelectedBorder { get { return AccentLight; } }
            public override Color ButtonPressedGradientBegin { get { return AccentLight; } }
            public override Color ButtonPressedGradientMiddle { get { return AccentLight; } }
            public override Color ButtonPressedGradientEnd { get { return AccentLight; } }
            public override Color ButtonCheckedGradientBegin { get { return Selected; } }
            public override Color ButtonCheckedGradientEnd { get { return Selected; } }
            public override Color CheckBackground { get { return Selected; } }
            public override Color CheckSelectedBackground { get { return Selected; } }
            public override Color CheckPressedBackground { get { return Selected; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return dark ? Panel : Color.White; } }
            public override Color GripDark { get { return Border; } }
            public override Color GripLight { get { return Panel; } }
        }
    }
}
