using System;
using System.Collections.Generic;

namespace Buzzcord
{
    public class User
    {
        public string Id, Username, GlobalName, Avatar, Discriminator;
        public bool Bot;
        public int PremiumType;   // 0 none, 1 Nitro Classic, 2 Nitro, 3 Nitro Basic (only sent for yourself)

        public string DisplayName
        {
            get { return !string.IsNullOrEmpty(GlobalName) ? GlobalName : (Username ?? Id); }
        }

        public string AvatarUrl(int size)
        {
            if (string.IsNullOrEmpty(Avatar))
            {
                long idx = 0;
                long id;
                if (long.TryParse(Id, out id)) idx = (id >> 22) % 6;
                return "https://cdn.discordapp.com/embed/avatars/" + idx + ".png";
            }
            return "https://cdn.discordapp.com/avatars/" + Id + "/" + Avatar + ".png?size=" + size;
        }

        public static User From(JObj j)
        {
            var u = new User();
            u.Update(j);
            return u;
        }

        public void Update(JObj j)
        {
            Id = j.Str("id") ?? Id;
            if (j.Has("username")) Username = j.Str("username");
            if (j.Has("global_name")) GlobalName = j.Str("global_name");
            if (j.Has("avatar")) Avatar = j.Str("avatar");
            if (j.Has("discriminator")) Discriminator = j.Str("discriminator");
            if (j.Has("bot")) Bot = j.Bool("bot");
            if (j.Has("premium_type")) PremiumType = (int)j.Long("premium_type");
        }
    }

    public class Presence
    {
        public string Status = "offline";
        public List<JObj> Activities = new List<JObj>();

        public static Presence From(JObj j)
        {
            var p = new Presence { Status = j.Str("status") ?? "offline" };
            foreach (var a in j.Objs("activities")) p.Activities.Add(a);
            return p;
        }

        public string CustomStatus
        {
            get
            {
                foreach (var a in Activities)
                    if (a.Long("type") == 4)
                    {
                        string text = a.Str("state");
                        var emoji = a.Obj("emoji");
                        string e = emoji != null && emoji.Str("id") == null ? emoji.Str("name") : null;
                        if (e != null) text = string.IsNullOrEmpty(text) ? e : e + " " + text;
                        return text;
                    }
                return null;
            }
        }

        // The first non-custom activity: a game, Spotify, a stream...
        public JObj MainActivity
        {
            get
            {
                foreach (var a in Activities) if (a.Long("type") != 4) return a;
                return null;
            }
        }

        public static string Describe(JObj a)
        {
            if (a == null) return null;
            string name = a.Str("name") ?? "";
            switch ((int)a.Long("type"))
            {
                case 0: return "Playing " + name;
                case 1: return "Streaming " + (a.Str("details") ?? name);
                case 2:
                    if (name == "Spotify" && a.Str("details") != null)
                        return "Listening to " + a.Str("details") + (a.Str("state") != null ? " by " + a.Str("state") : "");
                    return "Listening to " + name;
                case 3: return "Watching " + name;
                case 5: return "Competing in " + name;
                default: return name;
            }
        }

        // One line for the buddy list: what they're doing, else their custom status.
        public string Summary
        {
            get
            {
                var main = MainActivity;
                if (main != null)
                {
                    string s = Describe(main);
                    string extra = main.Str("details");
                    if (main.Long("type") == 0 && !string.IsNullOrEmpty(extra)) s += " - " + extra;
                    return s;
                }
                return CustomStatus;
            }
        }
    }

    // Someone in a voice channel or call.
    public class VoiceState
    {
        public string UserId, ChannelId, GuildId, SessionId;
        public bool SelfMute, SelfDeaf, Mute, Deaf, SelfVideo, SelfStream;

        public static VoiceState From(JObj j, string guildId)
        {
            return new VoiceState
            {
                UserId = j.Str("user_id"), ChannelId = j.Str("channel_id"), GuildId = j.Str("guild_id") ?? guildId, SessionId = j.Str("session_id"),
                SelfMute = j.Bool("self_mute"), SelfDeaf = j.Bool("self_deaf"), Mute = j.Bool("mute"), Deaf = j.Bool("deaf"),
                SelfVideo = j.Bool("self_video"), SelfStream = j.Bool("self_stream"),
            };
        }
    }

    // Where to connect for voice, from VOICE_STATE_UPDATE + VOICE_SERVER_UPDATE.
    public class VoiceServer
    {
        public string Endpoint, Token, ServerId, SessionId, ChannelId;
    }

    public class Overwrite
    {
        public string Id;
        public int Type; // 0 role, 1 member
        public long Allow, Deny;
    }

    public class Channel
    {
        public string Id, GuildId, Name, ParentId, Topic, LastMessageId, Icon, OwnerId;
        public int Type, Position;
        public List<string> RecipientIds = new List<string>();
        public List<Overwrite> Overwrites = new List<Overwrite>();

        public bool IsRequest;   // message request, or a DM Discord filtered as spam
        public bool IsPrivate { get { return Type == 1 || Type == 3; } }
        public bool IsDm { get { return Type == 1; } }
        public bool IsGroup { get { return Type == 3; } }
        public bool IsText { get { return Type == 0 || Type == 5; } }
        public bool IsCategory { get { return Type == 4; } }
        public bool IsVoice { get { return Type == 2; } }   // stage channels (13) aren't supported

        public static Channel From(JObj j, string guildId)
        {
            var c = new Channel();
            c.Update(j);
            if (c.GuildId == null) c.GuildId = guildId;
            return c;
        }

        public void Update(JObj j)
        {
            Id = j.Str("id") ?? Id;
            if (j.Has("type")) Type = (int)j.Long("type");
            if (j.Has("guild_id")) GuildId = j.Str("guild_id");
            if (j.Has("name")) Name = j.Str("name");
            if (j.Has("parent_id")) ParentId = j.Str("parent_id");
            if (j.Has("topic")) Topic = j.Str("topic");
            if (j.Has("last_message_id")) LastMessageId = j.Str("last_message_id");
            if (j.Has("icon")) Icon = j.Str("icon");
            if (j.Has("owner_id")) OwnerId = j.Str("owner_id");
            if (j.Has("is_message_request") || j.Has("is_spam")) IsRequest = j.Bool("is_message_request") || j.Bool("is_spam");
            if (j.Has("position")) Position = (int)j.Long("position");
            if (j.Has("recipient_ids"))
            {
                RecipientIds.Clear();
                foreach (var o in j.Arr("recipient_ids")) RecipientIds.Add(Convert.ToString(o));
            }
            else if (j.Has("recipients"))
            {
                RecipientIds.Clear();
                foreach (var r in j.Objs("recipients")) RecipientIds.Add(r.Str("id"));
            }
            if (j.Has("permission_overwrites"))
            {
                Overwrites.Clear();
                foreach (var o in j.Objs("permission_overwrites"))
                    Overwrites.Add(new Overwrite
                    {
                        Id = o.Str("id"),
                        Type = o.Str("type") == "member" || o.Long("type") == 1 ? 1 : 0,
                        Allow = o.Long("allow"),
                        Deny = o.Long("deny")
                    });
            }
        }
    }

    public class Role
    {
        public string Id, Name;
        public long Permissions;
        public int Position, Color;   // Color: 0xRRGGBB, 0 = none
    }

    public class Guild
    {
        public string Id, Name, Icon, OwnerId;
        public int PremiumTier;   // server boost level 0-3
        public Dictionary<string, Role> Roles = new Dictionary<string, Role>();
        public Dictionary<string, Channel> Channels = new Dictionary<string, Channel>();
        public HashSet<string> MyRoles = new HashSet<string>();
        public List<JObj> Emojis = new List<JObj>();

        public string IconUrl(int size)
        {
            return string.IsNullOrEmpty(Icon) ? null
                : "https://cdn.discordapp.com/icons/" + Id + "/" + Icon + ".png?size=" + size;
        }

        public void Update(JObj j)
        {
            Id = j.Str("id") ?? Id;
            // Newer gateway payloads nest the guild's own fields under "properties".
            var props = j.Obj("properties") ?? j;
            if (props.Has("name")) Name = props.Str("name");
            if (props.Has("icon")) Icon = props.Str("icon");
            if (props.Has("owner_id")) OwnerId = props.Str("owner_id");
            if (props.Has("premium_tier")) PremiumTier = (int)props.Long("premium_tier");
            if (j.Has("roles"))
            {
                Roles.Clear();
                foreach (var r in j.Objs("roles")) SetRole(r);
            }
            if (j.Has("emojis"))
            {
                Emojis.Clear();
                foreach (var e in j.Objs("emojis")) if (e.Str("id") != null && e.Str("name") != null) Emojis.Add(e);
            }
            if (j.Has("channels"))
            {
                Channels.Clear();
                foreach (var c in j.Objs("channels"))
                {
                    var ch = Channel.From(c, Id);
                    Channels[ch.Id] = ch;
                }
            }
        }

        public void SetRole(JObj r)
        {
            var colors = r.Obj("colors");
            int color = colors != null && colors.Has("primary_color") ? (int)colors.Long("primary_color") : (int)r.Long("color");
            Roles[r.Str("id")] = new Role { Id = r.Str("id"), Name = r.Str("name"), Permissions = r.Long("permissions"), Position = (int)r.Long("position"), Color = color };
        }

        // The colour of a member's highest coloured role (0 if none), as in Discord's member list.
        public int ColorFor(IEnumerable<object> roleIds)
        {
            int best = -1, color = 0;
            if (roleIds == null) return 0;
            foreach (var o in roleIds)
            {
                Role r;
                if (Roles.TryGetValue(Convert.ToString(o), out r) && r.Color != 0 && r.Position > best) { best = r.Position; color = r.Color; }
            }
            return color;
        }

        public const long Administrator = 1L << 3, ViewChannel = 1L << 10, SendMessages = 1L << 11,
            AttachFiles = 1L << 15, ReadHistory = 1L << 16, ManageMessages = 1L << 13;

        public long PermissionsIn(Channel c, string myId)
        {
            if (OwnerId == myId) return -1;
            Role everyone;
            long perms = Roles.TryGetValue(Id, out everyone) ? everyone.Permissions : 0;
            foreach (var rid in MyRoles)
            {
                Role r;
                if (Roles.TryGetValue(rid, out r)) perms |= r.Permissions;
            }
            if ((perms & Administrator) != 0) return -1;
            if (c == null) return perms;
            long allow = 0, deny = 0;
            foreach (var o in c.Overwrites)
                if (o.Type == 0 && o.Id == Id) { perms &= ~o.Deny; perms |= o.Allow; }
            foreach (var o in c.Overwrites)
                if (o.Type == 0 && MyRoles.Contains(o.Id)) { allow |= o.Allow; deny |= o.Deny; }
            perms &= ~deny;
            perms |= allow;
            foreach (var o in c.Overwrites)
                if (o.Type == 1 && o.Id == myId) { perms &= ~o.Deny; perms |= o.Allow; }
            return perms;
        }

        public bool CanView(Channel c, string myId)
        {
            return (PermissionsIn(c, myId) & ViewChannel) != 0;
        }
    }

    public static class Snowflake
    {
        public static int Compare(string a, string b)
        {
            if (a == b) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            return string.CompareOrdinal(a, b);
        }

        public static DateTime Time(string id)
        {
            long v;
            if (!long.TryParse(id, out v)) return DateTime.MinValue;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((v >> 22) + 1420070400000L).ToLocalTime();
        }
    }
}
