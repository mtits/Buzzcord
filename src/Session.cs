using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // Everything Buzzcord knows about one signed-in account. The gateway thread hands events to
    // the UI thread, and all state below is only touched there.
    public class Session
    {
        public readonly Api Api;
        public readonly Gateway Gateway;
        public readonly string Token;
        public User Me;
        public bool IsReady;
        public string ConnectionState = "Connecting...";
        public string Status = "online";      // online, idle, dnd, invisible
        public string CustomStatusText;
        // Discord's own "Share your detected activities with others" (Activity Privacy). When it's
        // off Discord strips games from what friends see, whatever this app sends. Null if unknown.
        public bool? DiscordSharesGames;
        // What Discord actually shows everyone for this account (the "all" session from
        // SESSIONS_REPLACE), null until Discord has said.
        public List<JObj> Broadcast;
        public DateTime LastPresencePush = DateTime.MinValue;

        public readonly Dictionary<string, User> Users = new Dictionary<string, User>();
        public readonly Dictionary<string, Presence> Presences = new Dictionary<string, Presence>();
        public readonly Dictionary<string, int> Relationships = new Dictionary<string, int>();   // 1 friend, 2 blocked, 3 incoming, 4 outgoing
        public readonly Dictionary<string, string> Nicknames = new Dictionary<string, string>();
        public readonly Dictionary<string, Channel> PrivateChannels = new Dictionary<string, Channel>();
        public readonly Dictionary<string, Guild> Guilds = new Dictionary<string, Guild>();
        public readonly List<string> GuildOrder = new List<string>();
        public readonly Dictionary<string, string> ReadStates = new Dictionary<string, string>();
        public readonly Dictionary<string, int> Mentions = new Dictionary<string, int>();

        readonly SynchronizationContext ui;

        public event Action Ready;
        public event Action<JObj> MessageCreated;
        public event Action<JObj> MessageUpdated;
        public event Action<string, string> MessageDeleted;      // channel id, message id
        public event Action<string> PresenceChanged;              // user id
        public event Action ListChanged;                         // channels, friends, guilds or unread state
        public event Action<string, string> Typing;              // channel id, user id
        public event Action<string> StateChanged;
        public event Action SignedOut;
        public Action<Channel> OpenChatHandler;                  // set by the buddy list window
        public event Action<string> FriendRequest;               // user id; someone wants to add you
        public event Action<string> FriendAccepted;              // user id; your request was accepted
        public event Action<string> FriendSignedIn;              // user id; a friend just came online
        public event Action<string> Unfriended;                  // user id; they removed (or blocked) you
        public event Action<string, JObj> ReactionChanged;       // gateway event name, data
        public static TimeSpan SignInGrace = TimeSpan.FromSeconds(60);
        DateTime readyAt = DateTime.MaxValue;

        // ---- Discord's notification settings (mutes) ----

        class MuteInfo
        {
            public bool Muted;
            public DateTime MutedUntil = DateTime.MaxValue;
            public int Level = -1;                // message_notifications: 0 all, 1 mentions, 2 nothing
            public bool SuppressEveryone, SuppressRoles;
            public Dictionary<string, bool> Channels = new Dictionary<string, bool>();   // channel id -> muted
            public bool Active { get { return Muted && DateTime.UtcNow < MutedUntil; } }
        }

        readonly Dictionary<string, MuteInfo> muteSettings = new Dictionary<string, MuteInfo>();  // guild id ("" = DMs)

        void ApplyGuildSettings(JObj g)
        {
            var info = new MuteInfo
            {
                Muted = g.Bool("muted"),
                Level = g.Has("message_notifications") ? (int)g.Long("message_notifications") : -1,
                SuppressEveryone = g.Bool("suppress_everyone"),
                SuppressRoles = g.Bool("suppress_roles")
            };
            info.MutedUntil = MuteEnd(g.Obj("mute_config"));
            foreach (var o in g.Objs("channel_overrides"))
                if (o.Str("channel_id") != null)
                    info.Channels[o.Str("channel_id")] = o.Bool("muted") && DateTime.UtcNow < MuteEnd(o.Obj("mute_config"));
            muteSettings[g.Str("guild_id") ?? ""] = info;
        }

        static DateTime MuteEnd(JObj cfg)
        {
            DateTime end;
            if (cfg != null && cfg.Str("end_time") != null &&
                DateTime.TryParse(cfg.Str("end_time"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out end))
                return end;
            return DateTime.MaxValue;
        }

        public bool IsGuildMuted(string guildId)
        {
            MuteInfo m;
            return guildId != null && muteSettings.TryGetValue(guildId, out m) && m.Active;
        }

        // Muted channel, a channel in a muted category, a channel in a muted server, or a muted DM.
        public bool IsMuted(Channel c)
        {
            if (c == null) return false;
            MuteInfo m;
            if (!muteSettings.TryGetValue(c.GuildId ?? "", out m)) return false;
            bool muted;
            if (m.Channels.TryGetValue(c.Id, out muted) && muted) return true;
            if (c.ParentId != null && m.Channels.TryGetValue(c.ParentId, out muted) && muted) return true;
            return c.GuildId != null && m.Active;
        }

        // Discord's rules: a direct @you always counts; @everyone and role pings follow the
        // server's suppress settings, and nothing else gets through a muted channel or server.
        public bool ShouldNotifyMention(JObj m)
        {
            if (Me == null) return false;
            foreach (var u in m.Objs("mentions")) if (u.Str("id") == Me.Id) return true;
            if (!MentionsMe(m)) return false;
            var c = FindChannel(m.Str("channel_id"));
            if (IsMuted(c)) return false;
            MuteInfo info;
            if (c != null && muteSettings.TryGetValue(c.GuildId ?? "", out info))
            {
                if (info.Level == 2) return false;
                if (m.Bool("mention_everyone") && info.SuppressEveryone) return false;
                if (!m.Bool("mention_everyone") && info.SuppressRoles) return false;
            }
            return true;
        }

        public Session(string token)
        {
            Token = token;
            ui = SynchronizationContext.Current;
            Api = new Api(token);
            Status = Settings.Get("Status." + TokenKey, "online");
            Gateway = new Gateway(token, BuildPresence);
            Gateway.Dispatch += (t, d) => Post(() => Handle(t, d));
            Gateway.StateChanged += s => Post(() => { ConnectionState = s; if (StateChanged != null) StateChanged(s); });
            Gateway.AuthFailed += () => Post(() => { if (SignedOut != null) SignedOut(); });
            // 4001 "unknown opcode" right after a member-list request: this gateway wants the older
            // request format. Switch for good; the reconnect re-sends the subscriptions.
            Gateway.Closed += code => Post(() =>
            {
                if (code == 4001 && !legacyMemberRequests && DateTime.UtcNow - lastMemberRequest < TimeSpan.FromSeconds(15))
                {
                    legacyMemberRequests = true;
                    Program.Log("member list: switching to the older request format");
                }
            });
        }

        // Settings key for this account that doesn't reveal the token.
        public string TokenKey
        {
            get
            {
                int dot = Token.IndexOf('.');
                return dot > 0 ? Token.Substring(0, dot) : Token.GetHashCode().ToString("x");
            }
        }

        public void Start() { Gateway.Start(); }
        public void Stop() { Gateway.Stop(); }

        void Post(Action a)
        {
            if (ui == null) a();
            else ui.Post(_ => a(), null);
        }

        // ---- lookups ----

        public User GetUser(string id)
        {
            User u;
            if (id != null && Users.TryGetValue(id, out u)) return u;
            return new User { Id = id, Username = "unknown" };
        }

        public string NameOf(string userId)
        {
            string nick;
            if (Nicknames.TryGetValue(userId, out nick) && !string.IsNullOrEmpty(nick)) return nick;
            return GetUser(userId).DisplayName;
        }

        public Presence PresenceOf(string userId)
        {
            Presence p;
            if (Me != null && userId == Me.Id) return new Presence { Status = Status == "invisible" ? "offline" : ShownStatus, Activities = PresenceManager.Instance.ActivitiesFor(this) };
            return Presences.TryGetValue(userId, out p) ? p : new Presence();
        }

        public Channel FindChannel(string id)
        {
            Channel c;
            if (PrivateChannels.TryGetValue(id, out c)) return c;
            foreach (var g in Guilds.Values)
                if (g.Channels.TryGetValue(id, out c)) return c;
            return null;
        }

        public Guild GuildOf(Channel c)
        {
            Guild g;
            return c != null && c.GuildId != null && Guilds.TryGetValue(c.GuildId, out g) ? g : null;
        }

        public string ChannelTitle(Channel c)
        {
            if (c == null) return "?";
            if (c.IsDm) return c.RecipientIds.Count > 0 ? NameOf(c.RecipientIds[0]) : "Direct message";
            if (c.IsGroup)
            {
                if (!string.IsNullOrEmpty(c.Name)) return c.Name;
                var names = new List<string>();
                foreach (var r in c.RecipientIds) names.Add(NameOf(r));
                return names.Count > 0 ? string.Join(", ", names) : "Group chat";
            }
            var g = GuildOf(c);
            return "#" + c.Name + (g != null ? " (" + g.Name + ")" : "");
        }

        public bool IsUnread(Channel c)
        {
            if (c == null || c.LastMessageId == null || IsMuted(c) || c.IsRequest) return false;
            string read;
            if (!ReadStates.TryGetValue(c.Id, out read)) return NeverOpenedCounts(c, c.LastMessageId, DateTime.Now);
            return Snowflake.Compare(c.LastMessageId, read) > 0;
        }

        // Discord keeps no read marker for a chat that was never opened anywhere. Such a DM only
        // counts as unread while its last message is recent: old ones nobody opened stay quiet.
        public static readonly TimeSpan NeverOpenedWindow = TimeSpan.FromDays(14);

        public static bool NeverOpenedCounts(Channel c, string last, DateTime now)
        {
            return c.IsPrivate && now - Snowflake.Time(last) < NeverOpenedWindow;
        }

        // Everything in the unread count, one entry per chat (newest first): unread DMs and
        // groups (their mentions, or 1), and mentions in server channels and threads. The tray
        // and taskbar numbers and the Unread Messages window all come from this list.
        public List<KeyValuePair<string, int>> UnreadEntries()
        {
            var list = new List<KeyValuePair<string, int>>();
            if (!IsReady) return list;
            foreach (var c in PrivateChannels.Values)
                if (IsUnread(c)) list.Add(new KeyValuePair<string, int>(c.Id, Math.Max(1, MentionCount(c.Id))));
            foreach (var kv in Mentions)
                if (!PrivateChannels.ContainsKey(kv.Key) && kv.Value > 0) list.Add(kv);
            list.Sort((a, b) =>
            {
                var ca = FindChannel(a.Key);
                var cb = FindChannel(b.Key);
                return Snowflake.Compare(cb != null && cb.LastMessageId != null ? cb.LastMessageId : b.Key,
                    ca != null && ca.LastMessageId != null ? ca.LastMessageId : a.Key);
            });
            return list;
        }

        public int BadgeCount()
        {
            int n = 0;
            foreach (var kv in UnreadEntries()) n += kv.Value;
            return n;
        }

        public int MentionCount(string channelId)
        {
            int n;
            return Mentions.TryGetValue(channelId, out n) ? n : 0;
        }

        public bool CanSend(Channel c)
        {
            var g = GuildOf(c);
            return g == null || (g.PermissionsIn(c, Me.Id) & Guild.SendMessages) != 0;
        }

        // ---- actions ----

        DateTime lastAck = DateTime.MinValue;

        public void MarkRead(Channel c)
        {
            if (c == null || c.LastMessageId == null) return;
            string read;
            bool changed = !ReadStates.TryGetValue(c.Id, out read) || Snowflake.Compare(c.LastMessageId, read) > 0;
            ReadStates[c.Id] = c.LastMessageId;
            bool hadMentions = Mentions.Remove(c.Id);
            if (!changed && !hadMentions) return;
            if (ListChanged != null) ListChanged();
            // Tell Discord too, so the phone app agrees. Spaced out like the official client does.
            if (DateTime.UtcNow - lastAck < TimeSpan.FromSeconds(1)) return;
            lastAck = DateTime.UtcNow;
            string id = c.LastMessageId;
            Api.Post("/channels/" + c.Id + "/messages/" + id + "/ack", Json.O("token", null)).ContinueWith(t => { var ignored = t.Exception; });
        }

        // For a chat Buzzcord has no Channel for (a thread with a mention).
        public void MarkRead(string channelId, string upTo)
        {
            var c = FindChannel(channelId);
            if (c != null)
            {
                if (c.LastMessageId == null) c.LastMessageId = upTo;
                MarkRead(c);
                return;
            }
            ReadStates[channelId] = upTo;
            Mentions.Remove(channelId);
            if (ListChanged != null) ListChanged();
            Api.Post("/channels/" + channelId + "/messages/" + upTo + "/ack", Json.O("token", null)).ContinueWith(t => { var ignored = t.Exception; });
        }

        // A reply typed into a message alert: in a server channel it answers that message, in DMs
        // and groups it's a plain message. Null means no message_reference.
        public static string ReplyTargetFor(Channel c, string messageId)
        {
            return c == null || c.IsPrivate || string.IsNullOrEmpty(messageId) ? null : messageId;
        }

        // ---- Auto-Away ----

        public bool AutoAwayOn { get; private set; }
        long awaySince;

        // What friends see: Away while Auto-Away is on, unless you picked Busy or Invisible.
        // Your chosen Status itself never changes.
        public string ShownStatus { get { return AutoAwayOn && Status == "online" ? "idle" : Status; } }

        public void SetAutoAway(bool on)
        {
            if (on == AutoAwayOn) return;
            AutoAwayOn = on;
            awaySince = on ? PresenceManager.NowMs() : 0;
            if (Status == "online") PushPresence();
            else if (Me != null && PresenceChanged != null) PresenceChanged(Me.Id);
        }

        public void SetStatus(string status)
        {
            Status = status;
            Settings.Set("Status." + TokenKey, status);
            Settings.Save();
            PushPresence();
            Api.Patch("/users/@me/settings", Json.O("status", status)).ContinueWith(t => { var ignored = t.Exception; });
        }

        public void PushPresence()
        {
            if (Gateway.Connected) SendPresence();
            if (Me != null && PresenceChanged != null) PresenceChanged(Me.Id);
        }

        void SendPresence()
        {
            var p = BuildPresence();
            LastPresencePush = DateTime.UtcNow;
            LogPresence("sent", p.Objs("activities"));
            Gateway.UpdatePresence(p);
        }

        public JObj BuildPresence()
        {
            var acts = new List<object>();
            if (!string.IsNullOrEmpty(CustomStatusText))
                acts.Add(Json.O("name", "Custom Status", "type", 4, "state", CustomStatusText));
            foreach (var a in PresenceManager.Instance.ActivitiesFor(this)) acts.Add(a);
            // afk tells Discord to send notifications to your phone while you're away from the PC.
            return Json.O("status", ShownStatus, "since", AutoAwayOn ? awaySince : 0L, "activities", acts, "afk", AutoAwayOn);
        }

        // True when Discord has had time to apply our game but its view of us still has none:
        // friends aren't seeing what the header says we're playing.
        public bool FriendsMissGame
        {
            get
            {
                if (Broadcast == null || DateTime.UtcNow - LastPresencePush < TimeSpan.FromSeconds(15)) return false;
                if (PresenceManager.Instance.ActivitiesFor(this).Count == 0) return false;
                foreach (var a in Broadcast) if (a.Long("type") != 4) return false;
                return true;
            }
        }

        // Turns Discord's Activity Privacy sharing back on for this account.
        public async Task ShareGamesOnDiscord()
        {
            await Api.Patch("/users/@me/settings", Json.O("show_current_game", true)).ConfigureAwait(false);
            Post(() => { DiscordSharesGames = true; PushPresence(); });
        }

        readonly Dictionary<string, string> lastPresenceLog = new Dictionary<string, string>();

        // Names only, and only when they change: enough to see whether Discord drops our game.
        void LogPresence(string what, IEnumerable<JObj> acts)
        {
            var names = new List<string>();
            foreach (var a in acts) names.Add(a.Long("type") == 4 ? "custom status" : a.Str("name"));
            string line = "[" + string.Join(", ", names) + "]";
            string before;
            if (lastPresenceLog.TryGetValue(what, out before) && before == line) return;
            lastPresenceLog[what] = line;
            Program.Log("presence " + (Me != null ? Me.Id : "?") + ": " + what + " " + line);
        }

        void ApplySessions(IEnumerable<JObj> sessions)
        {
            JObj all = null, only = null;
            int count = 0;
            foreach (var s in sessions)
            {
                count++;
                only = s;
                if (s.Str("session_id") == "all") all = s;
            }
            var chosen = all ?? (count == 1 ? only : null);
            if (chosen == null) return;
            Broadcast = new List<JObj>(chosen.Objs("activities"));
            LogPresence("Discord shows", Broadcast);
            if (Me != null && PresenceChanged != null) PresenceChanged(Me.Id);
        }

        // ---- voice ----

        // Who is in which voice channel or call (user id -> state), for this account's servers and calls.
        public readonly Dictionary<string, VoiceState> VoiceStates = new Dictionary<string, VoiceState>();
        public event Action<string> VoiceStateChanged;           // channel id (the one joined or left)
        string mySessionVoice, voiceToken, voiceEndpoint, voiceServerId, wantedVoiceChannel;
        TaskCompletionSource<VoiceServer> pendingVoice;

        void OnVoiceState(JObj d, string guildId)
        {
            string uid = d.Str("user_id");
            if (uid == null) return;
            var s = VoiceState.From(d, guildId);
            VoiceState old;
            VoiceStates.TryGetValue(uid, out old);
            if (s.ChannelId == null) VoiceStates.Remove(uid); else VoiceStates[uid] = s;
            if (Me != null && uid == Me.Id && s.ChannelId != null && s.ChannelId == wantedVoiceChannel)
            {
                mySessionVoice = s.SessionId;
                CompleteVoice();
            }
            if (VoiceStateChanged != null)
            {
                if (old != null && old.ChannelId != s.ChannelId) VoiceStateChanged(old.ChannelId);
                if (s.ChannelId != null) VoiceStateChanged(s.ChannelId);
            }
        }

        public List<string> VoiceMembers(string channelId)
        {
            var list = new List<string>();
            foreach (var kv in VoiceStates) if (kv.Value.ChannelId == channelId) list.Add(kv.Key);
            return list;
        }

        // Asks the main gateway to put us in a voice channel (or a DM/group call when guildId is
        // null); completes with the voice server to connect to.
        public async Task<VoiceServer> JoinVoice(string guildId, string channelId, bool selfMute, bool selfDeaf)
        {
            // Still in this call as far as Discord knows (the voice connection dropped): asking to
            // join again changes nothing, so no new voice state or server comes. Leave first.
            VoiceState stale;
            if (Me != null && VoiceStates.TryGetValue(Me.Id, out stale) && stale.ChannelId == channelId)
            {
                await SetVoice(guildId, null, false, false).ConfigureAwait(false);
                await Task.Delay(700).ConfigureAwait(false);
            }
            pendingVoice = new TaskCompletionSource<VoiceServer>();
            wantedVoiceChannel = channelId;
            mySessionVoice = voiceToken = voiceEndpoint = null;
            var wait = pendingVoice.Task;
            await Gateway.Send(Json.O("op", 4L, "d", Json.O("guild_id", guildId, "channel_id", channelId,
                "self_mute", selfMute, "self_deaf", selfDeaf, "self_video", false))).ConfigureAwait(false);
            if (await Task.WhenAny(wait, Task.Delay(10000)).ConfigureAwait(false) != wait)
                throw new TimeoutException("Discord didn't answer the voice join");
            return wait.Result;
        }

        // Mute/deafen as others see it, or leave (channelId null).
        public Task SetVoice(string guildId, string channelId, bool selfMute, bool selfDeaf)
        {
            if (channelId == null) wantedVoiceChannel = null;
            return Gateway.Send(Json.O("op", 4L, "d", Json.O("guild_id", guildId, "channel_id", channelId,
                "self_mute", selfMute, "self_deaf", selfDeaf, "self_video", false)));
        }

        // ---- DM and group calls ----

        public class Call
        {
            public string ChannelId;
            public List<string> Ringing = new List<string>();
        }

        public readonly Dictionary<string, Call> Calls = new Dictionary<string, Call>();
        public event Action<string> CallChanged;     // channel id
        public event Action<string> IncomingCall;    // channel id: we just started ringing

        void OnCall(JObj d)
        {
            string cid = d.Str("channel_id");
            if (cid == null) return;
            Call old;
            Calls.TryGetValue(cid, out old);
            bool wasRinging = old != null && Me != null && old.Ringing.Contains(Me.Id);
            var call = new Call { ChannelId = cid };
            if (d.Has("ringing")) foreach (var o in d.Arr("ringing") ?? new List<object>()) call.Ringing.Add(Convert.ToString(o));
            else if (old != null) call.Ringing = old.Ringing;
            Calls[cid] = call;
            foreach (var vs in d.Objs("voice_states")) OnVoiceState(vs, null);
            if (CallChanged != null) CallChanged(cid);
            if (!wasRinging && IsRingingMe(cid) && IncomingCall != null) IncomingCall(cid);
        }

        public bool IsRingingMe(string channelId)
        {
            Call c;
            return Me != null && Calls.TryGetValue(channelId, out c) && c.Ringing.Contains(Me.Id);
        }

        // Rings everyone else in the DM or group (after we've joined its call).
        public Task RingCall(string channelId)
        {
            return Api.Post("/channels/" + channelId + "/call/ring", Json.O("recipients", null));
        }

        // Declines a call ringing us.
        public Task StopRinging(string channelId)
        {
            return Api.Post("/channels/" + channelId + "/call/stop-ringing", Json.O("recipients", new List<object> { Me.Id }));
        }

        void CompleteVoice()
        {
            var tcs = pendingVoice;
            if (tcs == null || mySessionVoice == null || voiceToken == null || voiceEndpoint == null) return;
            pendingVoice = null;
            tcs.TrySetResult(new VoiceServer { Endpoint = voiceEndpoint, Token = voiceToken, ServerId = voiceServerId ?? wantedVoiceChannel, SessionId = mySessionVoice, ChannelId = wantedVoiceChannel });
        }

        // ---- friends ----

        public IEnumerable<string> RelationshipsOfType(int type)
        {
            foreach (var kv in Relationships) if (kv.Value == type) yield return kv.Key;
        }

        // By username, like Discord's Add Friend box.
        public Task SendFriendRequest(string username)
        {
            return Api.SendWithContext(HttpMethod.Post, "/users/@me/relationships",
                Json.O("username", username.Trim().TrimStart('@'), "discriminator", null), "Add Friend");
        }

        // Accepts their request if they sent one, otherwise sends one to them.
        public Task AddOrAcceptFriend(string userId, string location)
        {
            return Api.SendWithContext(HttpMethod.Put, "/users/@me/relationships/" + userId, new JObj(), location);
        }

        // Ignores a request, cancels one you sent, or unfriends.
        public Task RemoveRelationship(string userId, string location)
        {
            return RemovedByMe(userId, () => Api.SendWithContext(HttpMethod.Delete, "/users/@me/relationships/" + userId, null, location));
        }

        // Marks userId so the RELATIONSHIP_REMOVE that follows isn't reported as them unfriending
        // you. If Discord refused, nothing was removed: forget it again, or a real unfriend later
        // would be missed.
        Task RemovedByMe(string userId, Func<Task> send)
        {
            removedByMe.Add(userId);
            var ui = SynchronizationContext.Current;
            var task = send();
            task.ContinueWith(t =>
            {
                if (ui != null) ui.Post(_ => removedByMe.Remove(userId), null);
                else removedByMe.Remove(userId);
            }, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }

        readonly HashSet<string> removedByMe = new HashSet<string>();

        // ---- blocking ----

        public bool IsBlocked(string userId)
        {
            int rel;
            return userId != null && Relationships.TryGetValue(userId, out rel) && rel == 2;
        }

        // Everyone you blocked, by name.
        public List<string> BlockedUsers()
        {
            var list = new List<string>(RelationshipsOfType(2));
            list.Sort((a, b) =>
            {
                int c = string.Compare(NameOf(a), NameOf(b), StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            return list;
        }

        // Blocking a friend also unfriends them. Discord stops their DMs and calls; their messages
        // in servers and group chats still arrive and Buzzcord folds them away.
        public Task Block(string userId, string location)
        {
            return RemovedByMe(userId, () => Api.SendWithContext(HttpMethod.Put, "/users/@me/relationships/" + userId, Json.O("type", 2L), location));
        }

        public Task Unblock(string userId, string location)
        {
            return Api.SendWithContext(HttpMethod.Delete, "/users/@me/relationships/" + userId, null, location);
        }

        // Takes a one-to-one chat off Recent chats at once, like the X in Discord's DM list. The
        // messages stay, and it comes back with the next message. Never a group: closing one means
        // leaving it. If Discord refuses, the chat is put back.
        public async Task CloseDm(Channel c)
        {
            if (c == null || !c.IsDm || !PrivateChannels.ContainsKey(c.Id)) return;
            PrivateChannels.Remove(c.Id);
            Changed();
            try { await Api.Delete("/channels/" + c.Id); }
            catch
            {
                if (!PrivateChannels.ContainsKey(c.Id)) { PrivateChannels[c.Id] = c; Changed(); }
                throw;
            }
        }

        public async Task<Channel> OpenDm(string userId)
        {
            foreach (var c in PrivateChannels.Values)
                if (c.IsDm && c.RecipientIds.Count == 1 && c.RecipientIds[0] == userId) return c;
            var j = await Api.Post("/users/@me/channels", Json.O("recipients", new List<object> { userId })) as JObj;
            var ch = Channel.From(j, null);
            PrivateChannels[ch.Id] = ch;
            return ch;
        }

        // ---- gateway events ----

        void Handle(string t, JObj d)
        {
            if (d == null) return;
            switch (t)
            {
                case "READY": OnReady(d); ResubscribeMembers(); break;
                case "GUILD_MEMBER_LIST_UPDATE": OnMemberListUpdate(d); break;
                case "RESUMED": if (memberSubs.Count > 0) ResubscribeMembers(); break;
                case "READY_SUPPLEMENTAL": OnSupplemental(d); break;
                case "MESSAGE_CREATE": OnMessage(d); break;
                case "MESSAGE_UPDATE":
                    if (d.Obj("author") != null) RememberUser(d.Obj("author"));
                    if (MessageUpdated != null) MessageUpdated(d);
                    break;
                case "MESSAGE_DELETE":
                    if (MessageDeleted != null) MessageDeleted(d.Str("channel_id"), d.Str("id"));
                    break;
                case "MESSAGE_ACK":
                    ReadStates[d.Str("channel_id")] = d.Str("message_id");
                    Mentions.Remove(d.Str("channel_id"));
                    Changed();
                    break;
                case "PRESENCE_UPDATE":
                    {
                        var u = d.Obj("user");
                        if (u == null) break;
                        string id = u.Str("id");
                        if (u.Has("username")) RememberUser(u);
                        if (Me != null && id == Me.Id) break; // our own state comes from this app
                        Presence old;
                        string before = Presences.TryGetValue(id, out old) ? old.Status : "offline";
                        Presences[id] = Presence.From(d);
                        if (PresenceChanged != null) PresenceChanged(id);
                        // A friend coming online. Presences flood in right after connecting, so
                        // the first minute doesn't count.
                        int rel;
                        if (FriendSignedIn != null && d.Str("guild_id") == null && before == "offline" && Presences[id].Status != "offline"
                            && Relationships.TryGetValue(id, out rel) && rel == 1 && DateTime.UtcNow - readyAt >= SignInGrace)
                            FriendSignedIn(id);
                        break;
                    }
                case "MESSAGE_REACTION_ADD":
                case "MESSAGE_REACTION_REMOVE":
                case "MESSAGE_REACTION_REMOVE_ALL":
                case "MESSAGE_REACTION_REMOVE_EMOJI":
                    if (ReactionChanged != null) ReactionChanged(t, d);
                    break;
                case "SESSIONS_REPLACE":
                    ApplySessions(d.Objs("list"));
                    break;
                case "USER_SETTINGS_UPDATE":
                    if (d.Has("show_current_game"))
                    {
                        DiscordSharesGames = d.Bool("show_current_game");
                        if (Me != null && PresenceChanged != null) PresenceChanged(Me.Id);
                    }
                    break;
                case "USER_GUILD_SETTINGS_UPDATE":
                    ApplyGuildSettings(d);
                    Changed();
                    break;
                case "TYPING_START":
                    if (Typing != null && (Me == null || d.Str("user_id") != Me.Id)) Typing(d.Str("channel_id"), d.Str("user_id"));
                    break;
                case "CHANNEL_CREATE":
                case "CHANNEL_UPDATE":
                    {
                        string gid = d.Str("guild_id");
                        Guild g;
                        if (gid != null && Guilds.TryGetValue(gid, out g))
                        {
                            Channel c;
                            if (g.Channels.TryGetValue(d.Str("id"), out c)) c.Update(d);
                            else g.Channels[d.Str("id")] = Channel.From(d, gid);
                        }
                        else if (gid == null)
                        {
                            foreach (var r in d.Objs("recipients")) RememberUser(r);
                            Channel c;
                            if (PrivateChannels.TryGetValue(d.Str("id"), out c)) c.Update(d);
                            else PrivateChannels[d.Str("id")] = Channel.From(d, null);
                        }
                        Changed();
                        break;
                    }
                case "CHANNEL_DELETE":
                    {
                        Guild g;
                        if (d.Str("guild_id") != null && Guilds.TryGetValue(d.Str("guild_id"), out g)) g.Channels.Remove(d.Str("id"));
                        else PrivateChannels.Remove(d.Str("id"));
                        Changed();
                        break;
                    }
                case "CHANNEL_RECIPIENT_ADD":
                case "CHANNEL_RECIPIENT_REMOVE":
                    {
                        Channel c;
                        var u = d.Obj("user");
                        if (u == null || !PrivateChannels.TryGetValue(d.Str("channel_id"), out c)) break;
                        RememberUser(u);
                        if (t == "CHANNEL_RECIPIENT_ADD") { if (!c.RecipientIds.Contains(u.Str("id"))) c.RecipientIds.Add(u.Str("id")); }
                        else c.RecipientIds.Remove(u.Str("id"));
                        Changed();
                        break;
                    }
                case "RELATIONSHIP_ADD":
                case "RELATIONSHIP_UPDATE":
                    {
                        if (d.Obj("user") != null) RememberUser(d.Obj("user"));
                        string id = d.Str("id") ?? d.Str("user_id");
                        int before;
                        Relationships.TryGetValue(id, out before);
                        Relationships[id] = (int)d.Long("type");
                        // Blocked: whatever removal was expected has happened (Discord may send
                        // one update instead of remove + add), so don't let the mark linger.
                        if (d.Long("type") == 2) removedByMe.Remove(id);
                        if (d.Has("nickname")) Nicknames[id] = d.Str("nickname");
                        if (t == "RELATIONSHIP_ADD" && d.Long("type") == 3 && FriendRequest != null) FriendRequest(id);
                        if (before == 4 && d.Long("type") == 1 && FriendAccepted != null) FriendAccepted(id);
                        Changed();
                        break;
                    }
                case "RELATIONSHIP_REMOVE":
                    {
                        string id = d.Str("id");
                        int before;
                        bool wasFriend = Relationships.TryGetValue(id ?? "", out before) && before == 1;
                        Relationships.Remove(id);
                        // A friendship that ended without you doing it: they unfriended (or blocked) you.
                        bool byMe = removedByMe.Remove(id);
                        if (wasFriend && !byMe && Unfriended != null) Unfriended(id);
                        Changed();
                        break;
                    }
                case "VOICE_STATE_UPDATE": OnVoiceState(d, d.Str("guild_id")); break;
                case "CALL_CREATE":
                case "CALL_UPDATE": OnCall(d); break;
                case "CALL_DELETE":
                    {
                        string cid = d.Str("channel_id");
                        if (cid != null && Calls.Remove(cid) && CallChanged != null) CallChanged(cid);
                        break;
                    }
                case "VOICE_SERVER_UPDATE":
                    voiceToken = d.Str("token");
                    voiceEndpoint = d.Str("endpoint");   // null while Discord moves the call to another server
                    voiceServerId = d.Str("guild_id") ?? d.Str("channel_id");
                    CompleteVoice();
                    break;
                case "GUILD_CREATE":
                case "GUILD_UPDATE":
                    {
                        string gid = d.Str("id");
                        Guild g;
                        if (!Guilds.TryGetValue(gid, out g))
                        {
                            g = new Guild();
                            Guilds[gid] = g;
                            if (!GuildOrder.Contains(gid)) GuildOrder.Add(gid);
                        }
                        g.Update(d);
                        Changed();
                        break;
                    }
                case "GUILD_DELETE":
                    if (!d.Bool("unavailable"))
                    {
                        Guilds.Remove(d.Str("id"));
                        GuildOrder.Remove(d.Str("id"));
                        Changed();
                    }
                    break;
                case "GUILD_ROLE_CREATE":
                case "GUILD_ROLE_UPDATE":
                    {
                        Guild g;
                        if (Guilds.TryGetValue(d.Str("guild_id"), out g) && d.Obj("role") != null) { g.SetRole(d.Obj("role")); Changed(); }
                        break;
                    }
                case "GUILD_MEMBER_UPDATE":
                    {
                        Guild g;
                        var u = d.Obj("user");
                        if (Me != null && u != null && u.Str("id") == Me.Id && Guilds.TryGetValue(d.Str("guild_id"), out g))
                        {
                            g.MyRoles.Clear();
                            foreach (var r in d.Arr("roles") ?? new List<object>()) g.MyRoles.Add(Convert.ToString(r));
                            Changed();
                        }
                        break;
                    }
                case "USER_UPDATE":
                    if (Me != null && d.Str("id") == Me.Id) { Me.Update(d); RememberUser(d); Changed(); }
                    break;
            }
        }

        // ---- server member lists (the sidebar in channel windows) ----

        // Discord's member list for a channel: a flat list of group headers ({"group":{id,count}})
        // and members ({"member":{user,roles,nick,presence}}), kept in sync by
        // GUILD_MEMBER_LIST_UPDATE ops. The first 100 entries are requested.
        public class MemberList
        {
            public string Id, GuildId;
            public int MemberCount, OnlineCount;
            public readonly List<JObj> Items = new List<JObj>();
        }

        public event Action<string> MemberListChanged;   // guild id
        readonly Dictionary<string, MemberList> memberLists = new Dictionary<string, MemberList>();  // "guild/list"
        readonly Dictionary<string, string> channelLists = new Dictionary<string, string>();          // channel -> "guild/list"
        readonly Dictionary<string, List<string>> memberSubs = new Dictionary<string, List<string>>(); // guild -> channels shown
        readonly Dictionary<string, Queue<string>> pendingLists = new Dictionary<string, Queue<string>>();
        readonly Dictionary<string, string> lastSynced = new Dictionary<string, string>();
        bool legacyMemberRequests;   // fall back to the older opcode if Discord rejects the newer one
        DateTime lastMemberRequest = DateTime.MinValue;

        public MemberList MembersFor(Channel c)
        {
            string key;
            MemberList list;
            return c != null && channelLists.TryGetValue(c.Id, out key) && memberLists.TryGetValue(key, out list) ? list : null;
        }

        public void SubscribeMembers(Channel c)
        {
            if (c == null || c.GuildId == null) return;
            List<string> chans;
            if (!memberSubs.TryGetValue(c.GuildId, out chans)) memberSubs[c.GuildId] = chans = new List<string>();
            if (!chans.Contains(c.Id)) chans.Add(c.Id);
            QueueBinding(c.GuildId, c.Id);
            SendMemberRequest(c.GuildId);
        }

        public void UnsubscribeMembers(Channel c)
        {
            List<string> chans;
            if (c == null || c.GuildId == null || !memberSubs.TryGetValue(c.GuildId, out chans)) return;
            chans.Remove(c.Id);
            channelLists.Remove(c.Id);
            SendMemberRequest(c.GuildId);
        }

        // Which list belongs to which channel isn't in Discord's payload, so a new channel takes
        // the next list Discord syncs for that server (or, if none comes, the last one synced).
        void QueueBinding(string gid, string cid)
        {
            Queue<string> q;
            if (!pendingLists.TryGetValue(gid, out q)) pendingLists[gid] = q = new Queue<string>();
            if (!q.Contains(cid)) q.Enqueue(cid);
            var ui = this.ui;
            Task.Delay(3000).ContinueWith(t => Post(() =>
            {
                Queue<string> pq;
                string last;
                if (!pendingLists.TryGetValue(gid, out pq) || !pq.Contains(cid) || !lastSynced.TryGetValue(gid, out last)) return;
                var rest = new Queue<string>();
                foreach (var x in pq) if (x != cid) rest.Enqueue(x);
                pendingLists[gid] = rest;
                channelLists[cid] = last;
                if (MemberListChanged != null) MemberListChanged(gid);
            }));
        }

        void SendMemberRequest(string gid)
        {
            List<string> chans;
            memberSubs.TryGetValue(gid, out chans);
            var channels = new JObj();
            if (chans != null) foreach (var cid in chans) channels[cid] = new List<object> { new List<object> { 0L, 99L } };
            lastMemberRequest = DateTime.UtcNow;
            var request = legacyMemberRequests
                ? Json.O("op", 14, "d", Json.O("guild_id", gid, "typing", true, "activities", true, "threads", true, "channels", channels))
                : Json.O("op", 37, "d", Json.O("subscriptions", Json.O(gid,
                    Json.O("typing", true, "activities", true, "threads", true, "channels", channels))));
            Gateway.Send(request).ContinueWith(t => { var ignored = t.Exception; });
        }

        void ResubscribeMembers()
        {
            memberLists.Clear();
            channelLists.Clear();
            foreach (var kv in memberSubs)
            {
                if (kv.Value.Count == 0) continue;
                foreach (var cid in kv.Value) QueueBinding(kv.Key, cid);
                SendMemberRequest(kv.Key);
            }
        }

        void OnMemberListUpdate(JObj d)
        {
            string gid = d.Str("guild_id"), key = gid + "/" + d.Str("id");
            MemberList list;
            if (!memberLists.TryGetValue(key, out list)) memberLists[key] = list = new MemberList { Id = d.Str("id"), GuildId = gid };
            list.MemberCount = (int)d.Long("member_count", list.MemberCount);
            list.OnlineCount = (int)d.Long("online_count", list.OnlineCount);
            bool synced = false;
            foreach (var op in d.Objs("ops"))
            {
                switch (op.Str("op"))
                {
                    case "SYNC":
                        {
                            synced = true;
                            var range = op.Arr("range");
                            int start = range != null && range.Count > 0 ? Convert.ToInt32(range[0]) : 0;
                            var items = op.Arr("items") ?? new List<object>();
                            while (list.Items.Count < start + items.Count) list.Items.Add(null);
                            for (int i = 0; i < items.Count; i++) list.Items[start + i] = Remember(items[i] as JObj);
                            if (list.Items.Count > start + items.Count && items.Count < 100)
                                list.Items.RemoveRange(start + items.Count, list.Items.Count - start - items.Count);
                            break;
                        }
                    case "INSERT":
                        {
                            int i = (int)op.Long("index");
                            if (i >= 0 && i <= list.Items.Count) list.Items.Insert(i, Remember(op.Obj("item")));
                            break;
                        }
                    case "UPDATE":
                        {
                            int i = (int)op.Long("index");
                            if (i >= 0 && i < list.Items.Count) list.Items[i] = Remember(op.Obj("item"));
                            break;
                        }
                    case "DELETE":
                        {
                            int i = (int)op.Long("index");
                            if (i >= 0 && i < list.Items.Count) list.Items.RemoveAt(i);
                            break;
                        }
                    case "INVALIDATE":
                        {
                            var range = op.Arr("range");
                            if (range == null || range.Count < 2) break;
                            for (int i = Convert.ToInt32(range[0]); i <= Convert.ToInt32(range[1]) && i < list.Items.Count; i++) list.Items[i] = null;
                            break;
                        }
                }
            }
            if (synced)
            {
                lastSynced[gid] = key;
                Queue<string> q;
                bool bound = channelLists.ContainsValue(key);
                if (!bound && pendingLists.TryGetValue(gid, out q) && q.Count > 0) channelLists[q.Dequeue()] = key;
                else if (pendingLists.TryGetValue(gid, out q) && q.Count > 0 && bound)
                {
                    // Same list as a channel already showing (common: "everyone can see"): share it.
                    var next = q.Peek();
                    List<string> chans;
                    if (memberSubs.TryGetValue(gid, out chans) && chans.Contains(next)) { q.Dequeue(); channelLists[next] = key; }
                }
            }
            if (MemberListChanged != null) MemberListChanged(gid);
        }

        JObj Remember(JObj item)
        {
            var m = item != null ? item.Obj("member") : null;
            if (m != null && m.Obj("user") != null)
            {
                RememberUser(m.Obj("user"));
                var p = m.Obj("presence");
                string id = m.Obj("user").Str("id");
                int rel;
                // Friends' presence comes from their own updates; for everyone else the list is the source.
                if (p != null && id != null && (Me == null || id != Me.Id) && !(Relationships.TryGetValue(id, out rel) && rel == 1))
                    Presences[id] = Presence.From(p);
            }
            return item;
        }

        // Updates a stored message's "reactions" list from a MESSAGE_REACTION_* event.
        public static void ApplyReaction(JObj message, string t, JObj d, string myId)
        {
            if (t == "MESSAGE_REACTION_REMOVE_ALL") { message.Remove("reactions"); return; }
            var list = message.Arr("reactions");
            if (list == null) { list = new List<object>(); message["reactions"] = list; }
            string key = MessageHtml.EmojiKey(d.Obj("emoji"));
            JObj entry = null;
            foreach (var o in list)
            {
                var r = o as JObj;
                if (r != null && MessageHtml.EmojiKey(r.Obj("emoji")) == key) { entry = r; break; }
            }
            bool byMe = d.Str("user_id") == myId;
            if (t == "MESSAGE_REACTION_ADD")
            {
                if (entry == null)
                {
                    entry = Json.O("emoji", d.Obj("emoji"), "count", 0L, "me", false);
                    list.Add(entry);
                }
                if (byMe && entry.Bool("me")) return; // our own optimistic update already counted it
                entry["count"] = entry.Long("count") + 1;
                if (byMe) entry["me"] = true;
            }
            else if (entry != null && t == "MESSAGE_REACTION_REMOVE")
            {
                if (byMe && !entry.Bool("me")) return;
                entry["count"] = entry.Long("count") - 1;
                if (byMe) entry["me"] = false;
                if (entry.Long("count") <= 0) list.Remove(entry);
            }
            else if (entry != null && t == "MESSAGE_REACTION_REMOVE_EMOJI") list.Remove(entry);
            if (list.Count == 0) message.Remove("reactions");
        }

        // Feeds a gateway event in directly; used by the self-test.
        public void Inject(string t, JObj d) { Handle(t, d); }

        void Changed() { if (ListChanged != null) ListChanged(); }

        void RememberUser(JObj u)
        {
            string id = u.Str("id");
            if (id == null) return;
            User existing;
            if (Users.TryGetValue(id, out existing)) existing.Update(u);
            else Users[id] = User.From(u);
        }

        void OnReady(JObj d)
        {
            Me = User.From(d.Obj("user"));
            Users[Me.Id] = Me;
            foreach (var u in d.Objs("users")) RememberUser(u);

            Relationships.Clear();
            foreach (var r in d.Objs("relationships"))
            {
                if (r.Obj("user") != null) RememberUser(r.Obj("user"));
                string id = r.Str("id") ?? r.Str("user_id");
                Relationships[id] = (int)r.Long("type");
                if (r.Str("nickname") != null) Nicknames[id] = r.Str("nickname");
            }

            PrivateChannels.Clear();
            foreach (var c in d.Objs("private_channels"))
            {
                foreach (var r in c.Objs("recipients")) RememberUser(r);
                var ch = Channel.From(c, null);
                PrivateChannels[ch.Id] = ch;
            }

            Guilds.Clear();
            GuildOrder.Clear();
            var guildList = new List<JObj>(d.Objs("guilds"));
            VoiceStates.Clear();
            foreach (var gj in guildList)
            {
                var g = new Guild();
                g.Update(gj);
                Guilds[g.Id] = g;
                GuildOrder.Add(g.Id);
                foreach (var vs in gj.Objs("voice_states")) OnVoiceState(vs, g.Id);
            }
            ApplyMembers(d.Arr("merged_members"), guildList);

            ReadStates.Clear();
            Mentions.Clear();
            var rs = d.Has("read_state") ? d["read_state"] : null;
            var entries = rs is JObj ? ((JObj)rs).Arr("entries") : rs as List<object>;
            if (entries != null)
                foreach (var o in entries)
                {
                    var e = o as JObj;
                    if (e == null || e.Str("last_message_id") == null) continue;
                    if (e.Has("read_state_type") && e.Long("read_state_type") != 0) continue;
                    ReadStates[e.Str("id")] = e.Str("last_message_id");
                    if (e.Long("mention_count") > 0) Mentions[e.Str("id")] = (int)e.Long("mention_count");
                }

            var settings = d.Obj("user_settings");
            if (settings != null)
            {
                if (!Settings.Has("Status." + TokenKey) && settings.Str("status") != null) Status = settings.Str("status");
                var cs = settings.Obj("custom_status");
                if (cs != null) CustomStatusText = cs.Str("text");
                if (settings.Has("show_current_game")) DiscordSharesGames = settings.Bool("show_current_game");
            }

            ApplyPresences(d);
            muteSettings.Clear();
            var ugs = d.Has("user_guild_settings") ? d["user_guild_settings"] : null;
            var ugsList = ugs is JObj ? ((JObj)ugs).Arr("entries") : ugs as List<object>;
            if (ugsList != null) foreach (var o in ugsList) if (o is JObj) ApplyGuildSettings((JObj)o);
            ApplySessions(d.Objs("sessions"));
            readyAt = DateTime.UtcNow;
            IsReady = true;
            SendPresence();
            if (Ready != null) Ready();
        }

        void OnSupplemental(JObj d)
        {
            ApplyPresences(d);
            var guildIds = new List<JObj>(d.Objs("guilds"));
            ApplyMembers(d.Arr("merged_members"), guildIds);
            Changed();
        }

        void ApplyMembers(List<object> merged, List<JObj> guilds)
        {
            if (merged == null || Me == null) return;
            for (int i = 0; i < merged.Count && i < guilds.Count; i++)
            {
                Guild g;
                if (!Guilds.TryGetValue(guilds[i].Str("id"), out g)) continue;
                var members = merged[i] as List<object>;
                if (members == null) continue;
                foreach (var m in members)
                {
                    var mj = m as JObj;
                    if (mj == null) continue;
                    string uid = mj.Str("user_id") ?? (mj.Obj("user") != null ? mj.Obj("user").Str("id") : null);
                    if (uid != Me.Id) continue;
                    g.MyRoles.Clear();
                    foreach (var r in mj.Arr("roles") ?? new List<object>()) g.MyRoles.Add(Convert.ToString(r));
                }
            }
        }

        void ApplyPresences(JObj d)
        {
            foreach (var p in d.Objs("presences"))
            {
                var u = p.Obj("user");
                string id = u != null ? u.Str("id") : p.Str("user_id");
                if (id != null) Presences[id] = Presence.From(p);
            }
            var merged = d.Obj("merged_presences");
            if (merged != null)
                foreach (var p in merged.Objs("friends"))
                {
                    string id = p.Str("user_id") ?? (p.Obj("user") != null ? p.Obj("user").Str("id") : null);
                    if (id != null) Presences[id] = Presence.From(p);
                }
        }

        void OnMessage(JObj m)
        {
            var author = m.Obj("author");
            if (author != null) RememberUser(author);
            string cid = m.Str("channel_id");
            var c = FindChannel(cid);
            if (c != null)
            {
                bool wasUnread = IsUnread(c);
                c.LastMessageId = m.Str("id");
                if (author != null && Me != null && author.Str("id") == Me.Id) ReadStates[cid] = c.LastMessageId;
                // A blocked user's message doesn't make the chat new (only here: Discord isn't told).
                else if (author != null && IsBlocked(author.Str("id"))) { if (!wasUnread) ReadStates[cid] = c.LastMessageId; }
                else if (Me != null && (c.IsPrivate ? !IsMuted(c) : ShouldNotifyMention(m)))
                    Mentions[cid] = MentionCount(cid) + 1;
            }
            else if (m.Str("guild_id") == null)
            {
                // A DM channel we haven't seen yet: fetch it lazily through the list refresh.
                var ch = new Channel { Id = cid, Type = 1, LastMessageId = m.Str("id") };
                if (author != null) ch.RecipientIds.Add(author.Str("id"));
                PrivateChannels[cid] = ch;
            }
            if (MessageCreated != null) MessageCreated(m);
            Changed();
        }

        public bool MentionsMe(JObj m)
        {
            if (Me == null) return false;
            if (m.Bool("mention_everyone")) return true;
            foreach (var u in m.Objs("mentions")) if (u.Str("id") == Me.Id) return true;
            if (m.Str("guild_id") != null)
            {
                Guild g;
                if (Guilds.TryGetValue(m.Str("guild_id"), out g))
                    foreach (var r in m.Arr("mention_roles") ?? new List<object>())
                        if (g.MyRoles.Contains(Convert.ToString(r))) return true;
            }
            return false;
        }
    }
}
