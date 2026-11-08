using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Buzzcord
{
    // "Alyssa is calling you" with Answer / Ignore, ringing until answered, declined, or the
    // caller gives up. A call nobody answered goes into the notification history.
    public class IncomingCallForm : Form
    {
        static readonly Dictionary<string, IncomingCallForm> open = new Dictionary<string, IncomingCallForm>();
        readonly Session session;
        readonly Channel channel;
        readonly BuddyListForm owner;
        readonly Timer giveUp = new Timer { Interval = 60000 };
        bool handled;

        public static void Open(BuddyListForm owner, Session s, string channelId, bool ring)
        {
            Channel c;
            if (!s.PrivateChannels.TryGetValue(channelId, out c) || open.ContainsKey(s.TokenKey + channelId)) return;
            var f = new IncomingCallForm(owner, s, c);
            open[s.TokenKey + channelId] = f;
            f.Show();
            if (ring) Sounds.Ring();
        }

        IncomingCallForm(BuddyListForm owner, Session s, Channel c)
        {
            this.owner = owner;
            session = s;
            channel = c;
            Text = "Incoming call";
            Icon = Icons.AppIcon(32);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(300, 126);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Theme.Window;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

            var answer = new Button { Text = "Answer", Size = new Size(88, 30), Location = new Point(110, 86), BackColor = Color.FromArgb(0x2E, 0xA0, 0x43), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Tahoma", 9f, FontStyle.Bold) };
            var ignore = new Button { Text = "Ignore", Size = new Size(88, 30), Location = new Point(204, 86), BackColor = Color.FromArgb(0xC8, 0x3A, 0x3A), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Tahoma", 9f, FontStyle.Bold) };
            answer.FlatAppearance.BorderSize = ignore.FlatAppearance.BorderSize = 0;
            answer.Click += async (o, e) =>
            {
                handled = true;
                Close();
                try { await VoiceManager.Join(session, channel); }
                catch (Exception ex) { MessageBox.Show(this.owner, ex.Message, "Buzzcord"); }
            };
            ignore.Click += (o, e) =>
            {
                handled = true;
                session.StopRinging(channel.Id).ContinueWith(t => { var ignored = t.Exception; });
                Close();
            };
            Controls.Add(answer);
            Controls.Add(ignore);
            AcceptButton = answer;

            Action<string> onCall = cid => { if (cid == channel.Id && !session.IsRingingMe(cid) && !IsDisposed) Close(); };
            session.CallChanged += onCall;
            giveUp.Tick += (o, e) => Close();
            giveUp.Start();
            FormClosed += (o, e) =>
            {
                session.CallChanged -= onCall;
                giveUp.Dispose();
                open.Remove(session.TokenKey + channel.Id);
                Sounds.StopRing();
                if (!handled && this.owner != null && !this.owner.IsDisposed)
                    Notifier.Alert(this.owner, AlertKind.Call, "Missed call", "You missed a call from " + session.ChannelTitle(channel) + ".", channel.Id, Caller, false);
            };
            Theme.Attach(this, Invalidate);
        }

        string Caller { get { return channel.IsDm && channel.RecipientIds.Count > 0 ? channel.RecipientIds[0] : null; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = new Rectangle(0, 0, ClientSize.Width, 78);
            Icons.FillHeader(g, top);
            var avatar = new Rectangle(12, 12, 54, 54);
            var img = Caller != null ? ImageCache.Scaled(session.GetUser(Caller).AvatarUrl(128), 54, Invalidate) : null;
            if (img != null) g.DrawImage(img, avatar);
            else using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, avatar);
            using (var pen = new Pen(Color.FromArgb(0xB0, 0x90, 0x20))) g.DrawRectangle(pen, avatar);
            VoiceBar.DrawSpeaker(g, new Rectangle(78, 16, 16, 16), Color.FromArgb(0x2E, 0xA0, 0x43));
            using (var bold = new Font("Arial", 11f, FontStyle.Bold))
                TextRenderer.DrawText(g, session.ChannelTitle(channel), bold, new Rectangle(98, 14, ClientSize.Width - 106, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, channel.IsGroup ? "Your group is calling..." : "is calling you...", Font, new Rectangle(78, 40, ClientSize.Width - 86, 16), Theme.SubText, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "Voice call", Font, new Rectangle(12, 94, 90, 16), Theme.SubText, TextFormatFlags.NoPrefix);
        }
    }

    // Options > Voice settings: devices, voice activity or push-to-talk, sensitivity with a live
    // meter, mic volume.
    public class VoiceSettingsForm : Form
    {
        readonly ComboBox input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly ComboBox output = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly RadioButton vad = new RadioButton { Text = "Voice activity", AutoSize = true };
        readonly RadioButton ptt = new RadioButton { Text = "Push to talk", AutoSize = true };
        readonly Button keyButton = new Button { Width = 140, Height = 24 };
        readonly TrackBar sensitivity = new TrackBar { Minimum = -80, Maximum = 0, TickFrequency = 10, Width = 260, AutoSize = false, Height = 30 };
        readonly TrackBar gain = new TrackBar { Minimum = 0, Maximum = 200, TickFrequency = 25, Width = 260, AutoSize = false, Height = 30 };
        readonly CheckBox joinMuted = new CheckBox { Text = "Join calls muted", AutoSize = true };
        readonly CheckBox test = new CheckBox { Text = "Test my mic", AutoSize = true, Appearance = Appearance.Button };
        readonly BufferedPanel meter = new BufferedPanel { Width = 260, Height = 10 };
        readonly Label sensLabel = new Label { AutoSize = true }, gainLabel = new Label { AutoSize = true };
        int key = VoiceManager.PushToTalkKey;
        bool capturingKey;
        float level = -100;
        AudioEngine tester;

        public VoiceSettingsForm()
        {
            Text = "Voice settings";
            Icon = Icons.AppIcon(32);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Tahoma", 8.25f);
            ClientSize = new Size(310, 545);
            KeyPreview = true;

            input.Items.Add("Windows default");
            input.Items.AddRange(AudioEngine.InputDevices().ToArray());
            output.Items.Add("Windows default");
            output.Items.AddRange(AudioEngine.OutputDevices().ToArray());
            Select(input, VoiceManager.InputDevice);
            Select(output, VoiceManager.OutputDevice);
            vad.Checked = !VoiceManager.PushToTalk;
            ptt.Checked = VoiceManager.PushToTalk;
            sensitivity.Value = Math.Max(-80, Math.Min(0, VoiceManager.Sensitivity));
            gain.Value = Math.Max(0, Math.Min(200, VoiceManager.InputGainPercent));
            joinMuted.Checked = VoiceManager.JoinMuted;
            UpdateLabels();

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 8) };
            flow.Controls.Add(Heading("Microphone"));
            flow.Controls.Add(input);
            flow.Controls.Add(Heading("Speakers / headphones"));
            flow.Controls.Add(output);
            flow.Controls.Add(Heading("When to send my voice"));
            flow.Controls.Add(vad);
            flow.Controls.Add(sensLabel);
            flow.Controls.Add(sensitivity);
            flow.Controls.Add(meter);
            flow.Controls.Add(ptt);
            flow.Controls.Add(keyButton);
            flow.Controls.Add(Heading("Mic volume"));
            flow.Controls.Add(gainLabel);
            flow.Controls.Add(gain);
            flow.Controls.Add(joinMuted);
            flow.Controls.Add(test);
            flow.Controls.Add(new Label { Text = "Headphones recommended: Buzzcord has no echo cancellation yet.", AutoSize = true, MaximumSize = new Size(270, 0), ForeColor = Theme.SubText, Margin = new Padding(0, 8, 0, 0) });

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
            var ok = new Button { Text = "OK", Width = 80 };
            var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
            ok.Click += (o, e) => { Save(); Close(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(flow);
            Controls.Add(buttons);

            sensitivity.ValueChanged += (o, e) => { UpdateLabels(); if (tester != null) tester.SensitivityDb = sensitivity.Value; };
            gain.ValueChanged += (o, e) => { UpdateLabels(); if (tester != null) tester.InputGain = gain.Value / 100f; };
            vad.CheckedChanged += (o, e) => UpdateLabels();
            keyButton.Click += (o, e) => { capturingKey = true; keyButton.Text = "Press a key..."; };
            KeyDown += (o, e) =>
            {
                if (!capturingKey) return;
                capturingKey = false;
                key = (int)e.KeyCode;
                UpdateLabels();
                e.SuppressKeyPress = e.Handled = true;
            };
            meter.Paint += PaintMeter;
            test.CheckedChanged += (o, e) => ToggleTest();
            FormClosed += (o, e) => StopTest();
            Theme.Attach(this, () => meter.Invalidate());
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Tahoma", 8.25f, FontStyle.Bold), ForeColor = Icons.Purple, Margin = new Padding(0, 8, 0, 3) };
        }

        static void Select(ComboBox box, string name)
        {
            int i = string.IsNullOrEmpty(name) ? -1 : box.Items.IndexOf(name);
            box.SelectedIndex = i > 0 ? i : 0;
        }

        void UpdateLabels()
        {
            sensLabel.Text = "Sensitivity: " + sensitivity.Value + " dB";
            gainLabel.Text = gain.Value + "%";
            keyButton.Text = "Key: " + KeyName((Keys)key);
            sensitivity.Enabled = meter.Enabled = vad.Checked;
            keyButton.Enabled = ptt.Checked;
        }

        static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Capital: return "Caps Lock";
                case Keys.XButton1: return "Mouse 4";
                case Keys.XButton2: return "Mouse 5";
                case Keys.Oemtilde: return "` (backtick)";
                case Keys.ControlKey: return "Ctrl";
                case Keys.ShiftKey: return "Shift";
                case Keys.Menu: return "Alt";
                default: return k.ToString();
            }
        }

        void PaintMeter(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var b = meter.ClientRectangle;
            using (var br = new SolidBrush(Theme.Placeholder)) g.FillRectangle(br, b);
            float frac = Math.Max(0, Math.Min(1, (level + 80) / 80f));
            bool loud = level > sensitivity.Value;
            using (var br = new SolidBrush(loud ? Color.FromArgb(0x2E, 0xC0, 0x4F) : Color.FromArgb(0xC0, 0x8A, 0x10))) g.FillRectangle(br, 0, 0, (int)(b.Width * frac), b.Height);
            int mark = (int)(b.Width * (sensitivity.Value + 80) / 80f);
            using (var pen = new Pen(Theme.Text, 2)) g.DrawLine(pen, mark, 0, mark, b.Height);
        }

        void ToggleTest()
        {
            if (!test.Checked) { StopTest(); return; }
            tester = new AudioEngine { SensitivityDb = sensitivity.Value, InputGain = gain.Value / 100f };
            tester.InputLevel += db => { level = db; try { meter.BeginInvoke((Action)meter.Invalidate); } catch (Exception) { } };
            try { tester.StartInput(input.SelectedIndex - 1); }
            catch (Exception ex) { StopTest(); test.Checked = false; MessageBox.Show(this, ex.Message, "Buzzcord"); }
        }

        void StopTest()
        {
            if (tester != null) tester.Dispose();
            tester = null;
            level = -100;
            meter.Invalidate();
        }

        void Save()
        {
            Settings.Set("VoiceInputDevice", input.SelectedIndex > 0 ? (string)input.SelectedItem : "");
            Settings.Set("VoiceOutputDevice", output.SelectedIndex > 0 ? (string)output.SelectedItem : "");
            Settings.SetBool("VoicePushToTalk", ptt.Checked);
            Settings.SetInt("VoicePttKey", key);
            Settings.SetInt("VoiceSensitivity", sensitivity.Value);
            Settings.SetInt("VoiceGain", gain.Value);
            Settings.SetBool("VoiceJoinMuted", joinMuted.Checked);
            Settings.Save();
            if (VoiceManager.Current != null) VoiceManager.Current.ApplySettings();
        }
    }
}
