using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // The one call Buzzcord is in, across all accounts (there's only one mic). Joining another
    // call, on any account, leaves this one first.
    public static class VoiceManager
    {
        public static VoiceCall Current { get; private set; }
        public static event Action Changed;     // UI thread: a call started, changed or ended
        public static bool OpenDevices = true;  // the self-test runs calls without audio devices

        public static bool IsIn(Session s, string channelId)
        {
            var c = Current;
            return c != null && c.Session == s && c.ChannelId == channelId;
        }

        // Joins a server voice channel, or a DM/group call (ringing the others if nobody's in it yet).
        public static async Task Join(Session s, Channel channel)
        {
            if (IsIn(s, channel.Id)) return;
            if (Current != null) Current.Leave();
            var call = new VoiceCall(s, channel);
            Current = call;
            call.Changed += Raise;
            Raise();
            await call.Connect().ConfigureAwait(true);
        }

        public static void Leave() { if (Current != null) Current.Leave(); }

        internal static void Ended(VoiceCall call)
        {
            if (Current != call) return;
            Current = null;
            Raise();
        }

        internal static void SetForTest(VoiceCall call) { Current = call; Raise(); }

        static void Raise() { if (Changed != null) Changed(); }

        // ---- settings ----

        public static bool PushToTalk { get { return Settings.GetBool("VoicePushToTalk", false); } }
        public static int PushToTalkKey { get { return Settings.GetInt("VoicePttKey", 0x14); } }
        public static int Sensitivity { get { return Settings.GetInt("VoiceSensitivity", -50); } }
        public static int InputGainPercent { get { return Settings.GetInt("VoiceGain", 100); } }
        public static bool JoinMuted { get { return Settings.GetBool("VoiceJoinMuted", false); } }
        public static string InputDevice { get { return Settings.Get("VoiceInputDevice", ""); } }
        public static string OutputDevice { get { return Settings.Get("VoiceOutputDevice", ""); } }

        public static void Apply(AudioEngine audio)
        {
            audio.UsePushToTalk = PushToTalk;
            audio.PushToTalkKey = PushToTalkKey;
            audio.SensitivityDb = Sensitivity;
            audio.InputGain = InputGainPercent / 100f;
        }
    }

    public sealed class VoiceCall
    {
        public readonly Session Session;
        public readonly Channel Channel;
        public string GuildId { get { return Channel.GuildId; } }
        public string ChannelId { get { return Channel.Id; } }
        public string State = "Connecting...", Error;
        public int PingMs = -1;
        public bool Muted, Deafened, MeTalking;
        public event Action Changed;   // UI thread

        readonly HashSet<ulong> speaking = new HashSet<ulong>();
        readonly SynchronizationContext ui;
        VoiceConnection conn;
        AudioEngine audio;
        bool ended;

        public VoiceCall(Session session, Channel channel)
        {
            Session = session;
            Channel = channel;
            ui = SynchronizationContext.Current;
            Muted = VoiceManager.JoinMuted;
        }

        public bool Connected { get { return (conn != null && conn.IsReady || testConnected) && !ended; } }

        // For the self-test's screenshots: a call that looks connected, with someone talking.
        bool testConnected;
        internal void FakeForTest(string speakingUser)
        {
            testConnected = true;
            State = "Voice connected";
            PingMs = 38;
            lock (speaking) speaking.Add(ulong.Parse(speakingUser));
        }
        public bool Encrypted { get { return conn != null && conn.Dave.ProtocolVersion > 0 && conn.Dave.Established; } }
        // Connected, but the end-to-end group isn't set up yet: no one can hear anyone until it is.
        public bool Securing { get { return Connected && conn != null && conn.Dave.ProtocolVersion > 0 && !conn.Dave.Established; } }
        public string PrivacyCode { get { return conn != null ? conn.Dave.PrivacyCode : null; } }

        public string Title
        {
            get
            {
                if (Channel.IsPrivate) return "Call with " + Session.ChannelTitle(Channel);
                Guild g;
                return "#" + Channel.Name + (Session.Guilds.TryGetValue(GuildId ?? "", out g) ? " — " + g.Name : "");
            }
        }

        // Everyone in the call, us included.
        public List<string> Participants
        {
            get
            {
                var list = Session.VoiceMembers(ChannelId);
                if (Session.Me != null && !list.Contains(Session.Me.Id) && !ended) list.Insert(0, Session.Me.Id);
                return list;
            }
        }

        public bool IsSpeaking(string userId)
        {
            if (Session.Me != null && userId == Session.Me.Id) return MeTalking && !Muted;
            ulong u;
            if (!ulong.TryParse(userId, out u)) return false;
            lock (speaking) if (speaking.Contains(u)) return true;
            return audio != null && audio.IsPlaying(u);
        }

        void Post(Action a)
        {
            if (ui == null) a();
            else ui.Post(_ => a(), null);
        }

        void Raise() { if (Changed != null) Changed(); }

        internal async Task Connect()
        {
            try
            {
                bool startCall = Channel.IsPrivate && Session.VoiceMembers(ChannelId).Count == 0;
                var server = await Session.JoinVoice(GuildId, ChannelId, Muted, Deafened).ConfigureAwait(true);
                if (ended) { var left = Session.SetVoice(GuildId, null, false, false).ContinueWith(t => { var ignored = t.Exception; }); return; }
                if (startCall)
                {
                    try { await Session.RingCall(ChannelId).ConfigureAwait(true); }
                    catch (Exception ex) { Program.Log("ring: " + ex.Message); }
                }
                ulong me = ulong.Parse(Session.Me.Id), channel = ulong.Parse(ChannelId);
                conn = new VoiceConnection(server.Endpoint, server.ServerId, me, channel, server.SessionId, server.Token);
                audio = new AudioEngine { Muted = Muted, Deafened = Deafened };
                VoiceManager.Apply(audio);
                foreach (var uid in Session.VoiceMembers(ChannelId))
                {
                    ulong u;
                    if (ulong.TryParse(uid, out u)) audio.SetVolume(u, Settings.GetInt("VoiceVolume." + uid, 100) / 100f);
                }
                audio.FrameReady += f => conn.SendOpus(f);
                audio.TransmittingChanged += t => { conn.SetSpeaking(t); Post(() => { MeTalking = t; Raise(); }); };
                conn.OpusReceived += (u, seq, f) => audio.Receive(u, seq, f);
                conn.Ready += () => Post(() =>
                {
                    if (ended) return;
                    State = "Voice connected";
                    if (VoiceManager.OpenDevices)
                    {
                        try { audio.Start(AudioEngine.DeviceIndex(true, VoiceManager.InputDevice), AudioEngine.DeviceIndex(false, VoiceManager.OutputDevice)); }
                        catch (Exception ex) { Error = ex.Message; Program.Log("audio: " + ex.Message); }
                    }
                    Sounds.Joined();
                    Raise();
                });
                conn.StateChanged += st => Post(() => { if (!ended && st != "Connected") { State = st; Raise(); } });
                conn.Closed += code => Post(() => End(code == 4014 ? "Disconnected (removed from the call)" : "Disconnected"));
                conn.Ping += ms => Post(() => { PingMs = ms; Raise(); });
                conn.Speaking += (u, on) => { lock (speaking) { if (on) speaking.Add(u); else speaking.Remove(u); } Post(Raise); };
                conn.UserLeft += u => { audio.RemoveUser(u); lock (speaking) speaking.Remove(u); Post(Raise); };
                conn.Dave.StateChanged += () => Post(Raise);
                conn.Start();
            }
            catch (Exception ex)
            {
                Program.Log("voice join: " + ex.Message);
                End("Couldn't join: " + ex.Message);
            }
        }

        public void SetMuted(bool muted)
        {
            Muted = muted;
            if (!muted && Deafened) Deafened = false;   // unmuting also undeafens, as in Discord
            Update();
        }

        public void SetDeafened(bool deafened)
        {
            Deafened = deafened;
            if (deafened) Muted = true;
            else Muted = VoiceManager.JoinMuted && Muted;
            Update();
        }

        void Update()
        {
            if (audio != null) { audio.Muted = Muted; audio.Deafened = Deafened; }
            if (!ended) Session.SetVoice(GuildId, ChannelId, Muted, Deafened).ContinueWith(t => { var ignored = t.Exception; });
            Raise();
        }

        public void SetVolume(string userId, int percent)
        {
            Settings.SetInt("VoiceVolume." + userId, percent);
            Settings.Save();
            ulong u;
            if (audio != null && ulong.TryParse(userId, out u)) audio.SetVolume(u, percent / 100f);
        }

        // Settings changed while in the call.
        public void ApplySettings()
        {
            if (audio == null) return;
            VoiceManager.Apply(audio);
            if (VoiceManager.OpenDevices && Connected)
            {
                audio.Dispose();
                try { audio.Start(AudioEngine.DeviceIndex(true, VoiceManager.InputDevice), AudioEngine.DeviceIndex(false, VoiceManager.OutputDevice)); }
                catch (Exception ex) { Error = ex.Message; }
            }
            Raise();
        }

        public void Leave()
        {
            if (ended) return;
            Session.SetVoice(GuildId, null, false, false).ContinueWith(t => { var ignored = t.Exception; });
            End("Disconnected");
        }

        void End(string why)
        {
            if (ended) return;
            ended = true;
            State = why;
            if (conn != null) conn.Stop();
            if (audio != null) audio.Dispose();
            if (why != null && why.StartsWith("Couldn't")) Error = why;
            Sounds.Left();
            Raise();
            VoiceManager.Ended(this);
        }
    }
}
