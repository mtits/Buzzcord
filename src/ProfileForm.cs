using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // A user's profile: banner, picture, names, about me, what they're doing, mutual servers and
    // friends, connections. Opening it also saves their full-size picture and banner.
    public class ProfileForm : Form
    {
        static readonly Dictionary<string, ProfileForm> open = new Dictionary<string, ProfileForm>();

        readonly Session s;
        readonly string userId, guildId;
        readonly WebBrowser web = new WebBrowser();
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
        JObj profile;
        string avatarFile, html;

        public static void Open(Session s, string userId, string guildId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            string key = s.TokenKey + ":" + userId;
            ProfileForm f;
            if (open.TryGetValue(key, out f) && !f.IsDisposed)
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Activate();
                return;
            }
            f = new ProfileForm(s, userId, guildId);
            open[key] = f;
            f.FormClosed += (o, e) => open.Remove(key);
            f.Show();
        }

        ProfileForm(Session s, string userId, string guildId)
        {
            this.s = s;
            this.userId = userId;
            this.guildId = guildId;
            Text = s.NameOf(userId) + " - Profile";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            ClientSize = new Size(380, 600);
            MinimumSize = new Size(320, 420);
            StartPosition = FormStartPosition.CenterScreen;

            web.Dock = DockStyle.Fill;
            web.ScriptErrorsSuppressed = true;
            web.AllowWebBrowserDrop = false;
            web.IsWebBrowserContextMenuEnabled = true;
            web.Navigating += Navigating;
            web.NewWindow += (o, e) => e.Cancel = true;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6, 6, 6, 0), BackColor = Icons.Panel };
            bool isMe = s.Me != null && userId == s.Me.Id;
            if (!isMe) buttons.Controls.Add(Btn("Message", async () =>
            {
                try
                {
                    var c = await s.OpenDm(userId);
                    if (s.OpenChatHandler != null) s.OpenChatHandler(c);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Buzzcord"); }
            }));
            int rel;
            s.Relationships.TryGetValue(userId, out rel);
            if (!isMe && (rel == 0 || rel == 3))
            {
                // Not friends yet: send a request, or accept theirs.
                Button add = null;
                add = Btn(rel == 3 ? "Accept Request" : "Add Friend", async () =>
                {
                    add.Enabled = false;
                    try
                    {
                        await s.AddOrAcceptFriend(userId, "User Profile");
                        status.Text = rel == 3 ? "You're now friends." : "Friend request sent.";
                    }
                    catch (Exception ex) { add.Enabled = true; MessageBox.Show(this, ex.Message, "Buzzcord"); }
                });
                buttons.Controls.Add(add);
                ClientSize = new Size(440, ClientSize.Height);
            }
            buttons.Controls.Add(Btn("Full Picture", () => OpenPicture()));
            buttons.Controls.Add(Btn("Saved Pictures", () => OpenFolder(PictureFolder(s, s.GetUser(userId)))));
            buttons.Controls.Add(Btn("Copy Name", () => Clipboard.SetText(s.GetUser(userId).Username ?? userId)));

            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 20, Padding = new Padding(8, 0, 8, 0) };
            statusBar.Controls.Add(status);

            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(web);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 0) };
            host.Controls.Add(border);
            Controls.Add(host);
            Controls.Add(statusBar);
            Controls.Add(buttons);

            html = Render();
            Shown += (o, e) => { web.DocumentText = html; FetchProfile(); };
            Theme.Attach(this, () => { html = Render(); web.DocumentText = html; });
            // Your own Board changed (here, from the header, or another profile): show it.
            Action<Session> boardChanged = changed =>
            {
                if (changed == s && IsMe && IsHandleCreated && !IsDisposed) BeginInvoke((Action)FetchProfile);
            };
            GameBoard.Changed += boardChanged;
            FormClosed += (o, e) => GameBoard.Changed -= boardChanged;
        }

        static Button Btn(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 0, 4, 0) };
            b.Click += (o, e) => click();
            return b;
        }

        async void FetchProfile()
        {
            status.Text = "Loading profile...";
            try
            {
                string q = "/users/" + userId + "/profile?with_mutual_guilds=true&with_mutual_friends=true"
                    + (guildId != null ? "&guild_id=" + guildId : "");
                profile = await s.Api.Get(q) as JObj;
                var u = profile != null ? profile.Obj("user") : null;
                if (u != null)
                {
                    User existing;
                    if (s.Users.TryGetValue(userId, out existing)) existing.Update(u);
                    else s.Users[userId] = User.From(u);
                }
                status.Text = "";
            }
            catch (Exception ex)
            {
                status.Text = ex is ApiException && ((ApiException)ex).Status == 403
                    ? "Discord only shares full profiles of friends and people in your servers."
                    : ex.Message;
            }
            if (IsDisposed) return;
            html = Render();
            web.DocumentText = html;
            SavePictures();
            // Game names and pictures for the Board, then draw again.
            var ids = new List<string>(GameBoard.GameIds(profile != null ? profile.Arr("widgets") : null));
            foreach (var a in s.PresenceOf(userId).Activities) if (a.Str("application_id") != null) ids.Add(a.Str("application_id"));
            if (ids.Count == 0) return;
            await GameBoard.FetchAll(ids);
            if (IsDisposed) return;
            html = Render();
            web.DocumentText = html;
        }

        bool IsMe { get { return s.Me != null && userId == s.Me.Id; } }

        // Clicked a game on the Board or in "Now": add it to your own lists, or take it off them.
        void GameMenu(string type, string appId)
        {
            var g = GameBoard.Cached(appId);
            string name = g != null && g.Name != null ? g.Name : null;
            var menu = new ContextMenuStrip();
            if (name != null) menu.Items.Add(new ToolStripMenuItem(name) { Enabled = false });
            var kind = GameBoard.KindOf(type);
            if (IsMe && kind != null)
                menu.Items.Add("Remove from " + kind.Title, null, async (o, e) =>
                {
                    status.Text = "Saving...";
                    string problem = await GameBoard.Update(s, type, appId, false);
                    if (!IsDisposed) status.Text = problem ?? (name ?? "Game") + " removed from " + kind.Title + ".";
                });
            menu.Items.Add(GameBoard.AddMenu(this, s, appId, name, t => status.Text = t));
            if (name != null) menu.Items.Add("Copy name", null, (o, e) => Clipboard.SetText(name));
            menu.Closed += (o, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(Cursor.Position);
        }

        async void AddGame(string type)
        {
            var kind = GameBoard.KindOf(type);
            if (kind == null) return;
            string appId, name;
            using (var picker = new GamePickerForm("Add to " + kind.Title))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.AppId == null) return;
                appId = picker.AppId;
                name = picker.GameName;
            }
            status.Text = "Saving...";
            string problem = await GameBoard.Update(s, type, appId, true);
            if (!IsDisposed) status.Text = problem ?? name + " added to " + kind.Title + ".";
        }

        // ---- full-size pictures ----

        public static string PictureFolder(Session s, User u)
        {
            string account = AutoSaver.Clean(s.Me != null ? s.Me.Username : "account");
            return Path.Combine(Settings.SaveFolder, account, "Profile pictures", AutoSaver.Clean(u.Username ?? u.Id));
        }

        static string Ext(string hash) { return hash != null && hash.StartsWith("a_") ? ".gif" : ".png"; }

        async void SavePictures()
        {
            var u = s.GetUser(userId);
            var jobs = new List<KeyValuePair<string, string>>(); // url, file
            string folder = PictureFolder(s, u);
            if (!string.IsNullOrEmpty(u.Avatar))
            {
                avatarFile = Path.Combine(folder, "avatar " + u.Avatar + Ext(u.Avatar));
                jobs.Add(new KeyValuePair<string, string>(
                    "https://cdn.discordapp.com/avatars/" + u.Id + "/" + u.Avatar + Ext(u.Avatar) + "?size=4096", avatarFile));
            }
            string banner = Banner();
            if (banner != null)
                jobs.Add(new KeyValuePair<string, string>(
                    "https://cdn.discordapp.com/banners/" + u.Id + "/" + banner + Ext(banner) + "?size=4096",
                    Path.Combine(folder, "banner " + banner + Ext(banner))));
            var member = profile != null ? profile.Obj("guild_member") : null;
            if (member != null && member.Str("avatar") != null && guildId != null)
            {
                string h = member.Str("avatar");
                Guild g;
                string gname = s.Guilds.TryGetValue(guildId, out g) ? AutoSaver.Clean(g.Name) : guildId;
                jobs.Add(new KeyValuePair<string, string>(
                    "https://cdn.discordapp.com/guilds/" + guildId + "/users/" + u.Id + "/avatars/" + h + Ext(h) + "?size=4096",
                    Path.Combine(folder, "server avatar (" + gname + ") " + h + Ext(h))));
            }
            if (!Settings.GetBool("AutoSaveAvatars", true) || jobs.Count == 0) return;

            int saved = 0;
            foreach (var job in jobs)
            {
                if (File.Exists(job.Value)) continue;
                try
                {
                    Directory.CreateDirectory(folder);
                    await Api.Download(job.Key, job.Value);
                    saved++;
                }
                catch (Exception ex) { Program.Log("save picture: " + ex.Message); }
            }
            if (saved > 0 && !IsDisposed) status.Text = "Saved " + saved + " full-size picture" + (saved == 1 ? "" : "s") + ".";
        }

        void OpenPicture()
        {
            var u = s.GetUser(userId);
            string target = avatarFile != null && File.Exists(avatarFile) ? avatarFile
                : !string.IsNullOrEmpty(u.Avatar) ? "https://cdn.discordapp.com/avatars/" + u.Id + "/" + u.Avatar + Ext(u.Avatar) + "?size=4096"
                : u.AvatarUrl(256);
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Program.Log("open picture: " + ex.Message); }
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("open folder: " + ex.Message); }
        }

        void Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.ToString();
            if (url == "about:blank") return;
            e.Cancel = true;
            if (url.StartsWith("app://picture", StringComparison.OrdinalIgnoreCase)) { OpenPicture(); return; }
            if (url.StartsWith("app://profile/", StringComparison.OrdinalIgnoreCase))
            {
                Open(s, Uri.UnescapeDataString(url.Substring(14)), null);
                return;
            }
            if (url.StartsWith("app://game/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url.Substring(11).TrimEnd('/').Split('/');   // <widget type or "now">/<app id>
                if (parts.Length == 2) GameMenu(parts[0], parts[1]);
                return;
            }
            if (url.StartsWith("app://addgame/", StringComparison.OrdinalIgnoreCase))
            {
                AddGame(url.Substring(14).TrimEnd('/'));
                return;
            }
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        // ---- page ----

        string Banner()
        {
            var u = profile != null ? profile.Obj("user") : null;
            var up = profile != null ? profile.Obj("user_profile") : null;
            return (u != null ? u.Str("banner") : null) ?? (up != null ? up.Str("banner") : null);
        }

        static string E(string x) { return MessageHtml.E(x); }

        string Render()
        {
            var u = s.GetUser(userId);
            var pu = profile != null ? profile.Obj("user") : null;
            var up = profile != null ? profile.Obj("user_profile") : null;
            var member = profile != null ? profile.Obj("guild_member") : null;
            var p = s.PresenceOf(userId);

            string accent = "#9d7bc9";
            long color = pu != null && pu.Has("accent_color") && pu["accent_color"] != null ? pu.Long("accent_color", -1) : -1;
            if (color < 0 && up != null && up.Has("accent_color") && up["accent_color"] != null) color = up.Long("accent_color", -1);
            if (color >= 0) accent = "#" + color.ToString("x6");
            string banner = Banner();

            var sb = new StringBuilder();
            sb.Append(@"<!DOCTYPE html><html><head><meta http-equiv=""X-UA-Compatible"" content=""IE=edge""><meta charset=""utf-8""><style>
html,body{margin:0;padding:0;background:#fff;}
body{font:12px Tahoma,Arial,sans-serif;color:#000;word-wrap:break-word;}
a{color:#2a3ec0;}
.banner{height:110px;background-color:" + accent + @";background-size:cover;background-position:center;}
.top{position:relative;height:56px;}
.av{position:absolute;left:14px;top:-48px;width:96px;height:96px;border:4px solid #fff;background:#fff;cursor:pointer;}
.st{position:absolute;left:96px;top:30px;width:22px;height:22px;border-radius:11px;border:3px solid #fff;}
.box{margin:0 12px 10px 12px;}
.name{font:bold 18px Arial,sans-serif;}
.user{color:#555;}
.sec{margin:10px 12px 0 12px;border-top:1px solid #e5e0f0;padding-top:6px;}
.h{font-weight:bold;color:#5b2c8f;font-size:11px;text-transform:uppercase;margin-bottom:3px;}
.act{display:table;margin-top:2px;}
.act img{width:64px;height:64px;margin-right:8px;float:left;border:1px solid #ddd;}
.t{color:#777;font-size:11px;}
.pill{display:inline-block;margin:2px 3px 0 0;padding:1px 6px;background:#f3f0fa;border:1px solid #ddd5ee;}
code{font:11px Consolas,monospace;background:#f0eef4;}
.e{width:18px;height:18px;vertical-align:middle;}
.mention{color:#5b2c8f;text-decoration:none;}
.game-link{color:#5b2c8f;text-decoration:none;}
.add{font-weight:normal;text-transform:none;margin-left:6px;}
.gt{display:inline-block;width:72px;margin:3px 4px 0 0;vertical-align:top;text-align:center;text-decoration:none;color:inherit;font-size:11px;}
.gt img,.gt .gph{display:block;width:56px;height:56px;margin:0 auto 2px auto;border:1px solid #ddd5ee;}
.gt span{display:block;overflow:hidden;height:28px;}
.gd{display:block;margin:4px 0;text-decoration:none;color:inherit;overflow:hidden;}
.gd img,.gd .gph{float:left;width:64px;height:64px;margin-right:8px;border:1px solid #ddd5ee;}
.gd .gn{display:block;font-weight:bold;}
.gd .gc{display:block;margin-top:2px;}
.gph{display:inline-block;background:#e5e0f0;}
</style><style>" + Theme.ChatCss() + "</style></head><body>");

            sb.Append("<div class=\"banner\"");
            if (banner != null)
                sb.Append(" style=\"background-image:url('https://cdn.discordapp.com/banners/").Append(E(userId)).Append('/').Append(E(banner))
                  .Append(banner.StartsWith("a_") ? ".gif" : ".png").Append("?size=600')\"");
            sb.Append("></div><div class=\"top\"><a href=\"app://picture\" title=\"Open full size\"><img class=\"av\" src=\"")
              .Append(E(BigAvatar(u))).Append("\"></a>");
            string dot = StatusHex(p.Status);
            sb.Append("<div class=\"st\" style=\"background:").Append(dot).Append("\" title=\"").Append(E(Icons.StatusLabel(p.Status))).Append("\"></div></div>");

            sb.Append("<div class=\"box\"><div class=\"name\">").Append(E(member != null && member.Str("nick") != null ? member.Str("nick") : u.DisplayName)).Append("</div>");
            string pronouns = up != null ? up.Str("pronouns") : null;
            sb.Append("<div class=\"user\">").Append(E(u.Username));
            if (!string.IsNullOrEmpty(pronouns)) sb.Append(" &middot; ").Append(E(pronouns));
            if (u.Bot) sb.Append(" &middot; BOT");
            sb.Append("</div>");
            if (p.CustomStatus != null) sb.Append("<div style=\"margin-top:4px\">").Append(E(p.CustomStatus)).Append("</div>");
            sb.Append("</div>");

            // What they're doing right now.
            var acts = new List<JObj>();
            foreach (var a in p.Activities) if (a.Long("type") != 4) acts.Add(a);
            if (acts.Count > 0)
            {
                sb.Append("<div class=\"sec\"><div class=\"h\">Now</div>");
                foreach (var a in acts) sb.Append(ActivityHtml(a));
                sb.Append("</div>");
            }

            string bio = (up != null ? up.Str("bio") : null) ?? (pu != null ? pu.Str("bio") : null);
            if (!string.IsNullOrEmpty(bio))
                sb.Append("<div class=\"sec\"><div class=\"h\">About me</div>").Append(MessageHtml.Content(s, bio)).Append("</div>");

            BoardHtml(sb);

            sb.Append("<div class=\"sec\"><div class=\"h\">Member since</div>Discord: ").Append(E(Snowflake.Time(userId).ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
            if (member != null && member.Str("joined_at") != null)
            {
                DateTime joined;
                if (DateTime.TryParse(member.Str("joined_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out joined))
                {
                    Guild g;
                    string gname = guildId != null && s.Guilds.TryGetValue(guildId, out g) ? g.Name : "this server";
                    sb.Append("<br>").Append(E(gname)).Append(": ").Append(E(joined.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
                }
            }
            int rel;
            if (s.Relationships.TryGetValue(userId, out rel))
                sb.Append("<br><span class=\"t\">").Append(rel == 1 ? "Friend" : rel == 2 ? "Blocked" : rel == 3 ? "Sent you a friend request" : "Friend request sent").Append("</span>");
            sb.Append("</div>");

            if (profile != null)
            {
                var guilds = profile.Arr("mutual_guilds");
                if (guilds != null && guilds.Count > 0)
                {
                    sb.Append("<div class=\"sec\"><div class=\"h\">Mutual servers (").Append(guilds.Count).Append(")</div>");
                    foreach (var o in guilds)
                    {
                        var mg = o as JObj;
                        Guild g;
                        if (mg == null || !s.Guilds.TryGetValue(mg.Str("id") ?? "", out g)) continue;
                        sb.Append("<span class=\"pill\">").Append(E(g.Name));
                        if (mg.Str("nick") != null) sb.Append(" <span class=\"t\">as ").Append(E(mg.Str("nick"))).Append("</span>");
                        sb.Append("</span>");
                    }
                    sb.Append("</div>");
                }
                var friends = profile.Arr("mutual_friends");
                if (friends != null && friends.Count > 0)
                {
                    sb.Append("<div class=\"sec\"><div class=\"h\">Mutual friends (").Append(friends.Count).Append(")</div>");
                    foreach (var o in friends)
                    {
                        var mf = o as JObj;
                        if (mf == null) continue;
                        string name = mf.Str("global_name") ?? mf.Str("username");
                        sb.Append("<a class=\"pill\" style=\"text-decoration:none\" href=\"app://profile/").Append(Uri.EscapeDataString(mf.Str("id") ?? "")).Append("\">").Append(E(name)).Append("</a>");
                    }
                    sb.Append("</div>");
                }
                var conns = profile.Arr("connected_accounts");
                if (conns != null && conns.Count > 0)
                {
                    sb.Append("<div class=\"sec\"><div class=\"h\">Connections</div>");
                    foreach (var o in conns)
                    {
                        var c = o as JObj;
                        if (c == null) continue;
                        string link = ConnectionUrl(c.Str("type"), c.Str("name"), c.Str("id"));
                        sb.Append("<div><b>").Append(E(Title(c.Str("type")))).Append(":</b> ");
                        if (link != null) sb.Append("<a href=\"").Append(E(link)).Append("\">").Append(E(c.Str("name"))).Append("</a>");
                        else sb.Append(E(c.Str("name")));
                        if (c.Bool("verified")) sb.Append(" <span class=\"t\">&#10003;</span>");
                        sb.Append("</div>");
                    }
                    sb.Append("</div>");
                }
            }
            sb.Append("<div class=\"sec t\" style=\"border:0\">ID ").Append(E(userId)).Append("</div><div style=\"height:10px\"></div></body></html>");
            return sb.ToString();
        }

        // The Board: Favorite, Games in Rotation, Games I Like, Want to Play. Your own profile also
        // shows empty lists, with an "add" link on each.
        void BoardHtml(StringBuilder sb)
        {
            if (profile == null) return;
            var widgets = profile.Arr("widgets");
            foreach (var kind in GameBoard.Kinds)
            {
                var entries = GameBoard.Entries(widgets, kind.Type);
                if (entries.Count == 0 && !IsMe) continue;
                sb.Append("<div class=\"sec\"><div class=\"h\">").Append(E(kind.Title));
                if (kind.Max > 1) sb.Append(" (").Append(entries.Count).Append(')');
                if (IsMe && entries.Count < kind.Max || IsMe && kind.Max == 1)
                    sb.Append(" <a class=\"add\" href=\"app://addgame/").Append(kind.Type).Append("\">").Append(entries.Count > 0 && kind.Max == 1 ? "change" : "+ add").Append("</a>");
                sb.Append("</div>");
                if (entries.Count == 0) sb.Append("<span class=\"t\">Nothing here yet.</span>");
                // Favorite and Rotation are the "detailed" widgets: they carry a comment and tags.
                bool detailed = kind.Type == "favorite_games" || kind.Type == "current_games";
                foreach (var e in entries)
                {
                    string id = e.Str("game_id");
                    if (id == null) continue;
                    var g = GameBoard.Cached(id);
                    string name = g != null && g.Name != null ? g.Name : "Game " + id;
                    string pic = g != null ? g.IconUrl : null;
                    string href = "app://game/" + kind.Type + "/" + Uri.EscapeDataString(id);
                    if (detailed)
                    {
                        sb.Append("<a class=\"gd\" href=\"").Append(E(href)).Append("\" title=\"").Append(E(name)).Append("\">");
                        if (pic != null) sb.Append("<img src=\"").Append(E(pic)).Append("\">");
                        else sb.Append("<span class=\"gph\"></span>");
                        sb.Append("<span class=\"gn\">").Append(E(name)).Append("</span>");
                        var tags = e.Arr("tags");
                        if (tags != null)
                            foreach (var t in tags) sb.Append("<span class=\"pill\">").Append(E(GameBoard.TagText(Convert.ToString(t)))).Append("</span>");
                        if (!string.IsNullOrEmpty(e.Str("comment"))) sb.Append("<span class=\"t gc\">").Append(E(e.Str("comment"))).Append("</span>");
                        sb.Append("</a>");
                    }
                    else
                    {
                        sb.Append("<a class=\"gt\" href=\"").Append(E(href)).Append("\" title=\"").Append(E(name)).Append("\">");
                        if (pic != null) sb.Append("<img src=\"").Append(E(pic)).Append("\">");
                        else sb.Append("<span class=\"gph\"></span>");
                        sb.Append("<span>").Append(E(name)).Append("</span></a>");
                    }
                }
                sb.Append("</div>");
            }
        }

        static string BigAvatar(User u)
        {
            if (string.IsNullOrEmpty(u.Avatar)) return u.AvatarUrl(256);
            return "https://cdn.discordapp.com/avatars/" + u.Id + "/" + u.Avatar + Ext(u.Avatar) + "?size=256";
        }

        static string StatusHex(string status)
        {
            var c = Icons.StatusColor(status);
            return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        static string ActivityHtml(JObj a)
        {
            var sb = new StringBuilder("<div class=\"act\">");
            var assets = a.Obj("assets");
            string img = assets != null ? PresenceManager.AssetUrl(a.Str("application_id"), assets.Str("large_image")) : null;
            if (img != null && (img.StartsWith("https://") || img.StartsWith("http://"))) sb.Append("<img src=\"").Append(E(img)).Append("\">");
            string app = a.Long("type") == 0 ? a.Str("application_id") : null;
            sb.Append("<div><b style=\"color:#5b2c8f\">");
            // A game with a Discord app id can go on your own Board.
            if (app != null) sb.Append("<a class=\"game-link\" title=\"Add to my profile...\" href=\"app://game/now/").Append(E(app)).Append("\">");
            sb.Append(E(Presence.Describe(a)));
            if (app != null) sb.Append(" &#9662;</a>");
            sb.Append("</b>");
            bool spotify = a.Str("name") == "Spotify";
            if (!spotify && a.Str("details") != null) sb.Append("<br>").Append(E(a.Str("details")));
            if (a.Str("state") != null && !spotify) sb.Append("<br>").Append(E(a.Str("state")));
            var party = a.Obj("party");
            var size = party != null ? party.Arr("size") : null;
            if (size != null && size.Count == 2) sb.Append(" (").Append(size[0]).Append(" of ").Append(size[1]).Append(")");
            var ts = a.Obj("timestamps");
            if (ts != null && ts.Long("start") > 0)
            {
                var since = TimeSpan.FromMilliseconds(Math.Max(0, PresenceManager.NowMs() - ts.Long("start")));
                sb.Append("<br><span class=\"t\">for ").Append(since.TotalHours >= 1 ? (int)since.TotalHours + "h " + since.Minutes + "m" : since.Minutes + "m").Append("</span>");
            }
            sb.Append("</div></div>");
            return sb.ToString();
        }

        static string Title(string type)
        {
            switch (type)
            {
                case "youtube": return "YouTube";
                case "tiktok": return "TikTok";
                case "twitter": return "X";
                case "github": return "GitHub";
                case "playstation": return "PlayStation";
                case "xbox": return "Xbox";
                case "battlenet": return "Battle.net";
                case "riotgames": return "Riot Games";
                case "leagueoflegends": return "League of Legends";
                default: return string.IsNullOrEmpty(type) ? "?" : char.ToUpperInvariant(type[0]) + type.Substring(1);
            }
        }

        static string ConnectionUrl(string type, string name, string id)
        {
            if (name == null) return null;
            string n = Uri.EscapeDataString(name);
            switch (type)
            {
                case "steam": return id != null ? "https://steamcommunity.com/profiles/" + Uri.EscapeDataString(id) : null;
                case "twitch": return "https://twitch.tv/" + n;
                case "youtube": return id != null ? "https://youtube.com/channel/" + Uri.EscapeDataString(id) : null;
                case "github": return "https://github.com/" + n;
                case "twitter": return "https://x.com/" + n;
                case "reddit": return "https://reddit.com/u/" + n;
                case "tiktok": return "https://tiktok.com/@" + n;
                case "instagram": return "https://instagram.com/" + n;
                case "spotify": return id != null ? "https://open.spotify.com/user/" + Uri.EscapeDataString(id) : null;
                default: return null;
            }
        }
    }
}
