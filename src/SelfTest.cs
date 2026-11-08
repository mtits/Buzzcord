using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // --selftest <report> [--live] [--ui <folder>]
    //   offline checks always; --live also talks to Discord without signing in;
    //   --ui renders the windows with made-up data into PNGs.
    static partial class SelfTest
    {
        static StringBuilder report;
        static int failures;

        public static int Run(string reportPath, string[] args)
        {
            report = new StringBuilder();
            failures = 0;
            bool live = Array.IndexOf(args, "--live") >= 0;
            int ui = Array.IndexOf(args, "--ui");
            string uiDir = ui >= 0 && ui + 1 < args.Length ? args[ui + 1] : null;

            Check("JSON round trip", JsonRoundTrip);
            Check("Message HTML escapes and formats", MarkupTest);
            Check("QR code layout", QrTest);
            Check("Permission math", PermissionTest);
            Check("Rich presence pipe", PipeTest);
            Check("Quiet rules (Busy, gaming, normal)", QuietTest);
            Check("Friend sign-in detection", SignInTest);
            Check("Game visibility to friends", GameVisibilityTest);
            Check("Profile game lists", GameBoardTest);
            Check("Discord mutes and mention rules", MuteTest);
            Check("Reaction updates", ReactionTest);
            Check("Message archive", ArchiveTest);
            Check("Unfriend detection", UnfriendTest);
            Check("Group order", GroupOrderTest);
            Check("Auto-Away rules", AutoAwayTest);
            Check("Unread: never-opened DMs and message requests", UnreadRulesTest);
            Check("Notification reply targeting", ReplyTargetTest);
            Check("Buddy list shortcut parsing", HotkeyParseTest);
            Check("Server member list", MemberListTest);
            Check("Voice: AES-GCM", GcmTest);
            Check("Voice: HKDF and MLS encoding", HkdfAndTlsTest);
            Check("Voice: P-256 keys, ECDH, ECDSA", P256Test);
            Check("Voice: HPKE", HpkeTest);
            Check("MLS: tree math", MlsTreeMathTest);
            Check("MLS: crypto basics", MlsCryptoBasicsTest);
            Check("MLS: key schedule", MlsKeyScheduleTest);
            Check("MLS: transcript hashes", MlsTranscriptTest);
            Check("MLS: message encoding", MlsMessagesTest);
            Check("MLS: tree validation", MlsTreeValidationTest);
            Check("MLS: tree operations", MlsTreeOperationsTest);
            Check("MLS: TreeKEM update paths", MlsTreeKemTest);
            Check("MLS: Welcome", MlsWelcomeTest);
            Check("MLS: message protection", MlsMessageProtectionTest);
            Check("MLS: joining from a Welcome", () => MlsPassiveClientTest("passive-client-welcome"));
            Check("MLS: following commits", () => MlsPassiveClientTest("passive-client-handling-commit"));
            Check("DAVE: frame format", DaveFrameTest);
            Check("DAVE: simulated call", DaveCallTest);
            Check("Voice: RTP transport encryption", RtpTest);
            Check("Voice: Opus, voice activity, jitter buffer", OpusTest);
            Check("Voice: call against a fake voice server", VoiceCallTest);
            Check("Voice: call events and ringing", CallEventsTest);
            Check("Voice: joining and leaving through the call manager", VoiceManagerTest);
            if (live)
            {
                Check("Gateway rejects a fake token with 4004", GatewayTest);
                Check("QR login handshake reaches a code", () => RemoteAuthTest(uiDir));
            }
            if (uiDir != null) Check("Render windows", () => RenderUi(uiDir));

            report.AppendLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            File.WriteAllText(reportPath, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        static void Check(string name, Func<string> test)
        {
            try
            {
                string detail = test();
                report.AppendLine("PASS  " + name + (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail));
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("FAIL  " + name + "  -- " + ex.GetBaseException().Message);
            }
        }

        static void Assert(bool ok, string what) { if (!ok) throw new Exception(what); }

        static string JsonRoundTrip()
        {
            string src = "{\"a\":[1,2.5,\"x\\n\\u00e9\\\"\",true,null,{\"b\":-3}],\"id\":\"123456789012345678\",\"big\":1234567890123}";
            var o = Json.ParseObj(src);
            Assert(o.Arr("a").Count == 6, "array length");
            Assert((string)o.Arr("a")[2] == "x\né\"", "string escapes");
            Assert(o.Long("big") == 1234567890123L, "long");
            string back = Json.Write(o);
            Assert(Json.Write(Json.ParseObj(back)) == back, "stable output");
            return back.Length + " chars";
        }

        static Session FakeSession()
        {
            var s = new Session("MTIzNDU2Nzg5.fake.token");
            s.Inject("READY", Json.ParseObj(@"{
                ""session_id"":""x"",
                ""user"":{""id"":""100"",""username"":""marc"",""global_name"":""Marc"",""avatar"":null,""discriminator"":""0""},
                ""users"":[
                    {""id"":""200"",""username"":""alyssa"",""global_name"":""Alyssa"",""avatar"":null,""discriminator"":""0""},
                    {""id"":""201"",""username"":""ben"",""global_name"":""Ben"",""avatar"":null,""discriminator"":""0""},
                    {""id"":""202"",""username"":""carlo"",""global_name"":null,""avatar"":null,""discriminator"":""0""},
                    {""id"":""203"",""username"":""dina"",""global_name"":""Dina"",""avatar"":null,""discriminator"":""0""},
                    {""id"":""204"",""username"":""xenia"",""global_name"":""\u2661 \ud835\udce7\ud835\udcee\ud835\udcf7\ud835\udcf2\ud835\udcea \u2661 \u2728"",""avatar"":null,""discriminator"":""0""},
                    {""id"":""205"",""username"":""kawakaze"",""global_name"":""Kawakaze"",""avatar"":null,""discriminator"":""0""},
                    {""id"":""206"",""username"":""lyne"",""global_name"":""Lyne"",""avatar"":null,""discriminator"":""0""}],
                ""relationships"":[{""id"":""200"",""type"":1},{""id"":""201"",""type"":1},{""id"":""202"",""type"":1},{""id"":""203"",""type"":1},{""id"":""204"",""type"":1},{""id"":""205"",""type"":3},{""id"":""206"",""type"":4}],
                ""private_channels"":[
                    {""id"":""500"",""type"":1,""recipient_ids"":[""200""],""last_message_id"":""1300000000000000000""},
                    {""id"":""501"",""type"":3,""name"":""Valo squad"",""recipient_ids"":[""200"",""201"",""202""],""last_message_id"":""1290000000000000000""}],
                ""guilds"":[{""id"":""900"",""properties"":{""name"":""PH Gamers"",""icon"":null,""owner_id"":""999""},
                    ""roles"":[{""id"":""900"",""permissions"":""3072"",""position"":0},{""id"":""901"",""permissions"":""0"",""position"":1,""name"":""Mods"",""color"":15277667}],
                    ""emojis"":[],
                    ""channels"":[
                        {""id"":""910"",""type"":4,""name"":""Text Channels"",""position"":0},
                        {""id"":""911"",""type"":0,""name"":""general"",""parent_id"":""910"",""position"":0,""last_message_id"":""1300000000000000001""},
                        {""id"":""912"",""type"":0,""name"":""clips"",""parent_id"":""910"",""position"":1,""last_message_id"":""1300000000000000009""},
                        {""id"":""913"",""type"":0,""name"":""mods-only"",""parent_id"":""910"",""position"":2,
                            ""permission_overwrites"":[{""id"":""900"",""type"":0,""allow"":""0"",""deny"":""1024""}]},
                        {""id"":""914"",""type"":2,""name"":""Lounge"",""parent_id"":""910"",""position"":3}],
                    ""voice_states"":[{""user_id"":""200"",""channel_id"":""914"",""session_id"":""a""},{""user_id"":""201"",""channel_id"":""914"",""session_id"":""b"",""self_mute"":true}]}],
                ""merged_members"":[[{""user_id"":""100"",""roles"":[""901""]}]],
                ""read_state"":{""entries"":[{""id"":""500"",""last_message_id"":""1200000000000000000""}]},
                ""user_guild_settings"":{""entries"":[{""guild_id"":""900"",""muted"":false,""message_notifications"":1,""suppress_everyone"":true,
                    ""channel_overrides"":[{""channel_id"":""912"",""muted"":true}]}]},
                ""merged_presences"":{""friends"":[
                    {""user_id"":""200"",""status"":""online"",""activities"":[{""type"":0,""name"":""VALORANT"",""application_id"":""700136079562375258"",""details"":""Competitive"",""state"":""In Queue"",""timestamps"":{""start"":" + (PresenceManager.NowMs() - 754000) + @"},""party"":{""size"":[2,5]}}]},
                    {""user_id"":""201"",""status"":""idle"",""activities"":[{""type"":4,""name"":""Custom Status"",""state"":""brb eating""}]},
                    {""user_id"":""204"",""status"":""online"",""activities"":[]},
                    {""user_id"":""203"",""status"":""dnd"",""activities"":[{""type"":2,""name"":""Spotify"",""details"":""Ikaw"",""state"":""Yeng Constantino""}]}]}
            }"));
            return s;
        }

        static string MarkupTest()
        {
            if (PresenceManager.Instance == null) PresenceManager.Instance = new PresenceManager();
            var s = FakeSession();
            string html = MessageHtml.Content(s, "<script>alert(1)</script> **bold** <@200> https://example.com/a_b_c?x=1&y=2 `<i>` <t:0:D> [link](javascript:alert(1)) ||spoil||");
            Assert(!html.Contains("<script"), "script tag leaked");
            Assert(html.Contains("<b>bold</b>"), "bold");
            Assert(html.Contains("@Alyssa"), "mention: " + html);
            Assert(html.Contains("href=\"https://example.com/a_b_c?x=1&amp;y=2\""), "link: " + html);
            Assert(html.Contains("<code>&lt;i&gt;</code>"), "inline code");
            Assert(!html.Contains("href=\"javascript"), "javascript link");
            Assert(html.Contains("class=\"sp\""), "spoiler");
            var msg = Json.ParseObj("{\"id\":\"1300000000000000002\",\"channel_id\":\"500\",\"type\":0,\"content\":\"hi\",\"author\":{\"id\":\"200\",\"username\":\"alyssa\"},"
                + "\"attachments\":[{\"id\":\"1\",\"filename\":\"clip.mp4\",\"url\":\"https://cdn.discordapp.com/x/clip.mp4?ex=1\",\"proxy_url\":\"https://media.discordapp.net/x/clip.mp4?ex=1\",\"size\":2048000,\"width\":1920,\"height\":1080,\"content_type\":\"video/mp4\"},"
                + "{\"id\":\"2\",\"filename\":\"\\\"><img src=x onerror=alert(1)>.png\",\"url\":\"javascript:alert(1)\",\"size\":10}]}");
            string full = MessageHtml.Render(s, msg);
            Assert(full.Contains("<video"), "video element");
            Assert(!full.Contains("<img src=x"), "attribute injection: " + full);
            Assert(!full.Contains("src=\"javascript"), "javascript src");
            return html.Length + "+" + full.Length + " chars";
        }

        static string QrTest()
        {
            var qr = QrCode.Encode("https://discord.com/ra/abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG");
            Assert(qr.Version == 5 && qr.Size == 37, "expected version 5, got " + qr.Version);
            // Finder corners and timing pattern.
            Assert(qr[0, 0] && qr[6, 0] && qr[0, 6] && !qr[1, 1] && qr[3, 3], "top-left finder");
            Assert(qr[qr.Size - 1, 0] && qr[0, qr.Size - 1], "other finders");
            for (int i = 8; i < qr.Size - 8; i++) Assert(qr[i, 6] == (i % 2 == 0) && qr[6, i] == (i % 2 == 0), "timing at " + i);
            Assert(qr[8, qr.Size - 8], "dark module");
            // Both copies of the format bits must agree.
            int a = 0, b = 0;
            for (int i = 0; i <= 5; i++) a |= (qr[8, i] ? 1 : 0) << i;
            a |= (qr[8, 7] ? 1 : 0) << 6; a |= (qr[8, 8] ? 1 : 0) << 7; a |= (qr[7, 8] ? 1 : 0) << 8;
            for (int i = 9; i < 15; i++) a |= (qr[14 - i, 8] ? 1 : 0) << i;
            for (int i = 0; i < 8; i++) b |= (qr[qr.Size - 1 - i, 8] ? 1 : 0) << i;
            for (int i = 8; i < 15; i++) b |= (qr[8, qr.Size - 15 + i] ? 1 : 0) << i;
            Assert(a == b, "format copies differ");
            int fmt = a ^ 0x5412;
            Assert((fmt >> 13) == 0, "ECC level is not M");
            return "version " + qr.Version + ", mask " + ((fmt >> 10) & 7);
        }

        static string PermissionTest()
        {
            var s = FakeSession();
            var g = s.Guilds["900"];
            Assert(g.CanView(g.Channels["911"], "100"), "general visible");
            Assert(!g.CanView(g.Channels["913"], "100"), "mods-only hidden");
            Assert(s.IsUnread(s.PrivateChannels["500"]), "DM unread");
            return null;
        }

        static string PipeTest()
        {
            if (PresenceManager.Instance == null) PresenceManager.Instance = new PresenceManager();
            var server = new RpcServer();
            Assert(server.Start(), "no free pipe");
            using (var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut))
            {
                client.Connect(3000);
                WriteFrame(client, 0, "{\"v\":1,\"client_id\":\"356875221078245376\"}");
                var ready = ReadFrame(client);
                Assert(ready.Str("evt") == "READY", "no READY");
                WriteFrame(client, 1, "{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"n1\",\"args\":{\"pid\":1234,\"activity\":{\"details\":\"Ranked\",\"state\":\"Lobby\",\"timestamps\":{\"start\":1700000000},\"assets\":{\"large_image\":\"https://example.com/a.png\"},\"buttons\":[{\"label\":\"Watch\",\"url\":\"https://example.com\"}]}}}");
                var ack = ReadFrame(client);
                Assert(ack.Str("cmd") == "SET_ACTIVITY" && ack.Str("nonce") == "n1", "no SET_ACTIVITY reply");
                List<JObj> acts = null;
                for (int i = 0; i < 50; i++)
                {
                    acts = PresenceManager.Instance.ActivitiesFor(null);
                    if (acts.Count > 0) break;
                    Thread.Sleep(100);
                }
                Assert(acts.Count == 1, "activity not recorded");
                var a = acts[0];
                Assert(a.Obj("timestamps").Long("start") == 1700000000000L, "timestamp not converted to ms");
                Assert(a.Arr("buttons").Count == 1 && a.Obj("metadata") != null, "buttons not converted");
                string name = a.Str("name");
                client.Close();
                for (int i = 0; i < 30 && PresenceManager.Instance.ActivitiesFor(null).Count > 0; i++) Thread.Sleep(100);
                Assert(PresenceManager.Instance.ActivitiesFor(null).Count == 0, "activity not cleared on disconnect");
                server.Stop();
                return server.PipeName + ", app name \"" + name + "\"";
            }
        }

        static void WriteFrame(Stream s, int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            s.Write(BitConverter.GetBytes(op), 0, 4);
            s.Write(BitConverter.GetBytes(body.Length), 0, 4);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        static JObj ReadFrame(Stream s)
        {
            var h = new byte[8];
            int got = 0;
            while (got < 8) got += s.Read(h, got, 8 - got);
            int len = BitConverter.ToInt32(h, 4);
            var body = new byte[len];
            got = 0;
            while (got < len) got += s.Read(body, got, len - got);
            return Json.ParseObj(Encoding.UTF8.GetString(body));
        }

        static string GatewayTest()
        {
            var gw = new Gateway("fake.token.value", null);
            var done = new ManualResetEvent(false);
            var states = new List<string>();
            gw.StateChanged += st => { lock (states) states.Add(st); };
            gw.AuthFailed += () => done.Set();
            gw.Start();
            bool ok = done.WaitOne(20000);
            gw.Stop();
            Assert(ok, "no auth failure within 20 s; states: " + string.Join(" > ", states));
            return string.Join(" > ", states);
        }

        static string RemoteAuthTest(string outDir)
        {
            using (var ra = new RemoteAuth())
            {
                string url = null, error = null;
                var done = new ManualResetEvent(false);
                ra.QrReady += u => { url = u; done.Set(); };
                ra.Failed += m => { error = m; done.Set(); };
                ra.Start();
                Assert(done.WaitOne(20000), "no answer within 20 s");
                Assert(url != null, "failed: " + error);
                Assert(url.StartsWith("https://discord.com/ra/"), "odd URL " + url);
                if (outDir != null)
                {
                    Directory.CreateDirectory(outDir);
                    using (var bmp = QrCode.Encode(url).ToBitmap(6, 4)) bmp.Save(Path.Combine(outDir, "qr.png"));
                    File.WriteAllText(Path.Combine(outDir, "qr.txt"), url);
                }
                return "code for " + url.Substring(0, 30) + "...";
            }
        }

        // ---- window rendering ----

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        static void Capture(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(f.Handle, hdc, 2); // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(path);
            }
        }

        static void Pump(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
        }

        static string QuietTest()
        {
            var busy = Notifier.Decide("dnd", true, true, true);
            Assert(!busy.Popup && !busy.Sound && !busy.Balloon, "Busy should be silent");
            var gaming = Notifier.Decide("online", true, true, true);
            Assert(!gaming.Popup && !gaming.Sound && gaming.Balloon, "gaming should be balloon only");
            var gamingOff = Notifier.Decide("online", true, false, true);
            Assert(gamingOff.Popup && gamingOff.Sound && gamingOff.Balloon, "quiet-while-gaming off should behave normally");
            var normal = Notifier.Decide("idle", false, true, false);
            Assert(!normal.Popup && normal.Sound && normal.Balloon, "pop-ups off should still ding and balloon");
            return null;
        }

        static string SignInTest()
        {
            var s = FakeSession();
            var fired = new List<string>();
            s.FriendSignedIn += id => fired.Add(id);
            Func<string, string, JObj> presence = (id, st) => Json.O("user", Json.O("id", id), "status", st, "activities", new List<object>());
            s.Inject("PRESENCE_UPDATE", presence("202", "online"));
            Assert(fired.Count == 0, "fired inside the grace period after READY");
            var grace = Session.SignInGrace;
            Session.SignInGrace = TimeSpan.Zero;
            try
            {
                s.Inject("PRESENCE_UPDATE", presence("202", "offline"));
                s.Inject("PRESENCE_UPDATE", presence("202", "online"));
                s.Inject("PRESENCE_UPDATE", presence("202", "idle"));       // already online: no second alert
                s.Inject("PRESENCE_UPDATE", presence("999", "online"));     // not a friend
                var guildOnly = presence("203", "online");
                guildOnly["guild_id"] = "900";                                // a server-member presence, not a friend update
                s.Inject("PRESENCE_UPDATE", guildOnly);
            }
            finally { Session.SignInGrace = grace; }
            Assert(fired.Count == 1 && fired[0] == "202", "expected one alert for 202, got " + string.Join(",", fired));
            return null;
        }

        static string GameVisibilityTest()
        {
            if (PresenceManager.Instance == null) PresenceManager.Instance = new PresenceManager();
            var s = FakeSession();
            Assert(s.DiscordSharesGames == null && s.Broadcast == null, "visibility should start unknown");
            s.Inject("USER_SETTINGS_UPDATE", Json.O("show_current_game", false));
            Assert(s.DiscordSharesGames == false, "Activity Privacy off not picked up");
            var game = Json.O("type", 0, "name", "Stellar Blade");
            Func<object[], object[], JObj> sessions = (mine, all) => Json.O("list", new List<object> {
                Json.O("session_id", "abc", "status", "online", "activities", new List<object>(mine)),
                Json.O("session_id", "all", "status", "online", "activities", new List<object>(all)) });
            s.Inject("SESSIONS_REPLACE", sessions(new object[] { game }, new object[0]));
            Assert(s.Broadcast != null && s.Broadcast.Count == 0, "should read the 'all' session, not ours");
            s.Inject("SESSIONS_REPLACE", sessions(new object[] { game }, new object[] { game }));
            Assert(s.Broadcast.Count == 1 && s.Broadcast[0].Str("name") == "Stellar Blade", "broadcast game not read");
            Assert(!s.FriendsMissGame, "game is visible, nothing should be flagged");
            return null;
        }

        static string GameBoardTest()
        {
            var widgets = Json.Parse(@"[
                {""id"":""1"",""data"":{""type"":""played_games"",""games"":[{""game_id"":""10""}]}},
                {""id"":""2"",""data"":{""type"":""favorite_games"",""games"":[{""game_id"":""20"",""comment"":""goat"",""tags"":[""expert""]}]}},
                {""id"":""3"",""data"":{""type"":""current_games"",""games"":[{""game_id"":""31""},{""game_id"":""32""},{""game_id"":""33""},{""game_id"":""34""},{""game_id"":""35""}]}},
                {""id"":""4"",""data"":{""type"":""application"",""application_id"":""99""}}]") as List<object>;
            Assert(GameBoard.Change(widgets, "played_games", "11", true) == null, "add to Games I Like");
            var liked = GameBoard.Entries(widgets, "played_games");
            Assert(liked.Count == 2 && liked[0].Str("game_id") == "11", "new game should go first");
            Assert(GameBoard.Change(widgets, "played_games", "11", true) != null, "duplicate should be refused");
            Assert(GameBoard.Change(widgets, "favorite_games", "21", true) == null, "new favorite");
            var fav = GameBoard.Entries(widgets, "favorite_games");
            Assert(fav.Count == 1 && fav[0].Str("game_id") == "21", "favorite should be replaced, not added");
            Assert(GameBoard.Change(widgets, "current_games", "36", true) != null, "rotation holds 5");
            Assert(GameBoard.Change(widgets, "want_to_play_games", "40", true) == null && GameBoard.Entries(widgets, "want_to_play_games").Count == 1, "new list created");
            Assert(GameBoard.Change(widgets, "played_games", "10", false) == null && GameBoard.Entries(widgets, "played_games").Count == 1, "remove");
            Assert(new List<string>(GameBoard.GameIds(widgets)).Count == 8, "game ids");
            Assert(widgets.Count == 5 && ((JObj)widgets[3]).Obj("data").Str("application_id") == "99", "other widgets must be kept");
            return null;
        }

        static string MuteTest()
        {
            var s = FakeSession();
            var g = s.Guilds["900"];
            Assert(s.IsMuted(g.Channels["912"]) && !s.IsUnread(g.Channels["912"]), "muted channel shows unread");
            Assert(!s.IsMuted(g.Channels["911"]), "unmuted channel reported muted");
            Func<string, bool, bool, JObj> msg = (cid, me, everyone) => Json.O("id", "1300000000000000100", "channel_id", cid, "guild_id", "900",
                "mention_everyone", everyone, "mentions", me ? new List<object> { Json.O("id", "100") } : new List<object>());
            Assert(s.ShouldNotifyMention(msg("912", true, false)), "a direct mention in a muted channel must still alert");
            Assert(!s.ShouldNotifyMention(msg("912", false, true)), "@everyone in a muted channel alerted");
            Assert(!s.ShouldNotifyMention(msg("911", false, true)), "@everyone alerted despite suppress_everyone");
            Assert(s.ShouldNotifyMention(msg("911", true, false)), "a direct mention did not alert");
            s.Inject("USER_GUILD_SETTINGS_UPDATE", Json.O("guild_id", "900", "muted", true, "channel_overrides", new List<object>()));
            Assert(s.IsGuildMuted("900") && s.IsMuted(g.Channels["911"]), "server mute ignored");
            return null;
        }

        static string ReactionTest()
        {
            var m = Json.O("id", "1");
            var thumbs = Json.O("id", null, "name", "\U0001F44D");
            var blob = Json.O("id", "123", "name", "blob");
            Session.ApplyReaction(m, "MESSAGE_REACTION_ADD", Json.O("user_id", "7", "emoji", thumbs), "100");
            Session.ApplyReaction(m, "MESSAGE_REACTION_ADD", Json.O("user_id", "100", "emoji", thumbs), "100");
            Session.ApplyReaction(m, "MESSAGE_REACTION_ADD", Json.O("user_id", "100", "emoji", thumbs), "100"); // duplicate of ours
            var r = (JObj)m.Arr("reactions")[0];
            Assert(r.Long("count") == 2 && r.Bool("me"), "add: count " + r.Long("count"));
            Session.ApplyReaction(m, "MESSAGE_REACTION_ADD", Json.O("user_id", "7", "emoji", blob), "100");
            Assert(m.Arr("reactions").Count == 2 && MessageHtml.EmojiKey(blob) == "blob:123", "custom emoji");
            Session.ApplyReaction(m, "MESSAGE_REACTION_REMOVE", Json.O("user_id", "100", "emoji", thumbs), "100");
            Assert(r.Long("count") == 1 && !r.Bool("me"), "remove ours");
            Session.ApplyReaction(m, "MESSAGE_REACTION_REMOVE", Json.O("user_id", "7", "emoji", thumbs), "100");
            Session.ApplyReaction(m, "MESSAGE_REACTION_REMOVE_EMOJI", Json.O("emoji", blob), "100");
            Assert(!m.Has("reactions"), "reactions should be gone");
            string html = MessageHtml.Render(FakeSession(), Json.O("id", "1300000000000000003", "channel_id", "500", "type", 0L, "content", "x",
                "author", Json.O("id", "200"), "reactions", new List<object> { Json.O("emoji", thumbs, "count", 3L, "me", true) }));
            Assert(html.Contains("app://react/") && html.Contains("re mine"), "reaction chip is not clickable");
            return null;
        }

        static string ArchiveTest()
        {
            var s = FakeSession();
            long baseId = long.Parse(Api.Nonce());
            Func<long, string, JObj> msg = (id, text) => Json.O("id", id.ToString(), "channel_id", "500", "content", text, "author", Json.O("id", "200"));
            string path = Archive.LogPath(s, s.PrivateChannels["500"]);
            long before = File.Exists(path) ? new FileInfo(path).Length : 0;
            Assert(Archive.Consider(s, msg(baseId, "first")), "first message not archived");
            Assert(!Archive.Consider(s, msg(baseId, "first")), "duplicate archived");
            Assert(!Archive.Consider(s, msg(baseId - 5, "older")), "older message archived");
            Assert(Archive.Consider(s, msg(baseId + 1, "second\nline two")), "next message not archived");
            Assert(!Archive.Consider(s, Json.O("id", (baseId + 2).ToString(), "channel_id", "911", "content", "server")), "server message archived");
            string added = File.ReadAllText(path).Substring((int)before);
            Assert(added.Contains("Alyssa: first") && added.Contains("Alyssa: second") && added.Contains("    line two"), "log text: " + added);
            return path;
        }

        static string UnfriendTest()
        {
            var s = FakeSession();
            var fired = new List<string>();
            s.Unfriended += id => fired.Add(id);
            s.Inject("RELATIONSHIP_REMOVE", Json.O("id", "201", "type", 1L));     // they removed you
            s.RemoveRelationship("202", "Friends").ContinueWith(t => { var ignored = t.Exception; }); // you removed them (fake token: the call itself fails)
            s.Inject("RELATIONSHIP_REMOVE", Json.O("id", "202", "type", 1L));
            s.Inject("RELATIONSHIP_REMOVE", Json.O("id", "205", "type", 3L));     // a request going away isn't an unfriend
            Assert(fired.Count == 1 && fired[0] == "201", "expected only 201, got " + string.Join(",", fired));
            Assert(!s.Relationships.ContainsKey("201") && !s.Relationships.ContainsKey("202"), "relationships not removed");
            return null;
        }

        static string GroupOrderTest()
        {
            Settings.Set("GroupOrder", "");
            Assert(string.Join(",", GroupOrder.Current) == "Favorites,Recent,Groups,Friends,Offline,Servers,Requests", "default order");
            Settings.Set("GroupOrder", "Servers,Friends,bogus");
            string order = string.Join(",", GroupOrder.Current);
            Assert(order == "Favorites,Recent,Groups,Servers,Requests,Friends,Offline", "partial order: " + order);
            Settings.Set("GroupOrder", "");
            return order;
        }

        // Mirrors mac/Tests/AutoAwayTests.swift.
        static string AutoAwayTest()
        {
            Assert(!AutoAway.ShouldBeAway(30, false, 10, true), "30 s idle isn't away");
            Assert(AutoAway.ShouldBeAway(600, false, 10, true), "10 minutes idle is away");
            Assert(!AutoAway.ShouldBeAway(6000, false, 0, true), "Off means never by idle time");
            Assert(AutoAway.ShouldBeAway(0, true, 0, true), "locking Windows");
            Assert(!AutoAway.ShouldBeAway(0, true, 10, false), "locking doesn't count when turned off");
            double idle = AutoAway.IdleSeconds;
            Assert(idle >= 0, "reads the idle time");

            var s = FakeSession();
            s.Status = "online";
            s.SetAutoAway(true);
            Assert(s.ShownStatus == "idle", "Available shows as Away");
            var p = s.BuildPresence();
            Assert(p.Str("status") == "idle" && p.Bool("afk") && p.Long("since") > 0, "presence: " + Json.Write(p));
            Assert(s.Status == "online", "the saved status isn't changed");
            s.Status = "dnd";
            Assert(s.ShownStatus == "dnd", "Busy stays Busy");
            s.Status = "invisible";
            Assert(s.ShownStatus == "invisible", "Invisible stays invisible");
            s.Status = "online";
            s.SetAutoAway(false);
            p = s.BuildPresence();
            Assert(s.ShownStatus == "online" && !p.Bool("afk") && p.Long("since") == 0, "back again: " + Json.Write(p));
            return "idle now " + (int)idle + " s";
        }

        static string SnowflakeDaysAgo(double days)
        {
            long ms = (long)(DateTime.UtcNow.AddDays(-days) - new DateTime(1970, 1, 1)).TotalMilliseconds;
            return ((ms - 1420070400000L) << 22).ToString();
        }

        // Mirrors mac/Tests/UnreadTests.swift.
        static string UnreadRulesTest()
        {
            string old = SnowflakeDaysAgo(200), recent = SnowflakeDaysAgo(1), older = SnowflakeDaysAgo(2);
            var s = new Session("MTIzNDU2Nzg5.unread.test");
            s.Inject("READY", Json.O(
                "session_id", "x",
                "user", Json.O("id", "1", "username", "me"),
                "users", new List<object> { Json.O("id", "2", "username", "a"), Json.O("id", "3", "username", "b") },
                "private_channels", new List<object> {
                    Json.O("id", "10", "type", 1L, "recipient_ids", new List<object> { "2" }, "last_message_id", old),      // never opened, months old
                    Json.O("id", "11", "type", 1L, "recipient_ids", new List<object> { "3" }, "last_message_id", recent),   // never opened, yesterday
                    Json.O("id", "12", "type", 1L, "recipient_ids", new List<object> { "2" }, "last_message_id", recent),   // read up to an older message
                    Json.O("id", "13", "type", 1L, "recipient_ids", new List<object> { "3" }, "last_message_id", recent, "is_message_request", true),
                    Json.O("id", "14", "type", 3L, "recipient_ids", new List<object> { "2", "3" }, "last_message_id", old) },  // read, nothing new
                "guilds", new List<object> { Json.O("id", "20", "properties", Json.O("name", "G", "owner_id", "9"),
                    "roles", new List<object> { Json.O("id", "20", "permissions", "1071698660929", "position", 0L) },
                    "channels", new List<object> { Json.O("id", "21", "type", 0L, "name", "general", "position", 0L, "last_message_id", recent) }) },
                "read_state", Json.O("entries", new List<object> {
                    Json.O("id", "12", "last_message_id", older),
                    Json.O("id", "14", "last_message_id", old),
                    Json.O("id", "21", "last_message_id", older, "mention_count", 2L),
                    Json.O("id", "99", "last_message_id", older, "mention_count", 1L) })));   // a thread Buzzcord hasn't loaded
            Assert(!s.IsUnread(s.FindChannel("10")), "months-old DM nobody opened stays quiet");
            Assert(s.IsUnread(s.FindChannel("11")), "a new DM nobody opened yet counts");
            Assert(s.IsUnread(s.FindChannel("12")), "DM with newer messages counts");
            Assert(!s.IsUnread(s.FindChannel("13")), "message requests don't count");
            Assert(!s.IsUnread(s.FindChannel("14")), "read group");
            var entries = new List<string>();
            foreach (var e in s.UnreadEntries()) entries.Add(e.Key + "=" + e.Value);
            entries.Sort(StringComparer.Ordinal);
            Assert(string.Join(",", entries) == "11=1,12=1,21=2,99=1", "entries: " + string.Join(",", entries));
            Assert(s.BadgeCount() == 5, "the tray number is the Unread window's rows added up, got " + s.BadgeCount());
            s.MarkRead("99", recent);
            Assert(s.BadgeCount() == 4, "marking a thread read clears it");
            Assert(Session.NeverOpenedCounts(s.FindChannel("11"), SnowflakeDaysAgo(13.9), DateTime.Now), "13.9 days old counts");
            Assert(!Session.NeverOpenedCounts(s.FindChannel("11"), SnowflakeDaysAgo(14.1), DateTime.Now), "14.1 days old doesn't");
            return null;
        }

        static string ReplyTargetTest()
        {
            var s = FakeSession();
            Assert(Session.ReplyTargetFor(s.FindChannel("911"), "777") == "777", "server channel replies reference the message");
            Assert(Session.ReplyTargetFor(s.FindChannel("500"), "777") == null, "DM replies are plain messages");
            Assert(Session.ReplyTargetFor(s.FindChannel("501"), "777") == null, "group replies are plain messages");
            Assert(Session.ReplyTargetFor(s.FindChannel("911"), null) == null, "no message id, no reference");
            return null;
        }

        // Mirrors the shortcut half of mac/Tests/DeviceAndShortcutTests.swift.
        static string HotkeyParseTest()
        {
            Assert(HotkeyCombo.Standard.ToString() == "Ctrl+Alt+B", "default is Ctrl+Alt+B");
            var c = HotkeyCombo.Parse("ctrl + alt + f5");
            Assert(c != null && c.Modifiers == (HotkeyCombo.ModControl | HotkeyCombo.ModAlt) && c.Key == System.Windows.Forms.Keys.F5, "lenient parsing");
            Assert(HotkeyCombo.Parse("Win+Shift+Alt+Ctrl+9").ToString() == "Ctrl+Alt+Shift+Win+9", "modifiers in Windows' order");
            Assert(HotkeyCombo.Parse(HotkeyCombo.Standard.ToString()).ToString() == "Ctrl+Alt+B", "round trip");
            Assert(HotkeyCombo.Parse("") == null && HotkeyCombo.Parse("Ctrl+") == null && HotkeyCombo.Parse("Ctrl+A+B") == null && HotkeyCombo.Parse("Ctrl+Bogus") == null && HotkeyCombo.Parse("Ctrl+65") == null, "rejects junk");
            Assert(!HotkeyCombo.Parse("Shift+B").IsUsable, "Shift alone would steal typing");
            Assert(!HotkeyCombo.Usable(0), "no modifiers");
            Assert(HotkeyCombo.Usable(HotkeyCombo.ModControl) && HotkeyCombo.Parse("Win+B").IsUsable, "Ctrl or Win is enough");
            return null;
        }

        static JObj MemberItem(string id, string status, string game, string nick, params string[] roles)
        {
            var acts = new List<object>();
            if (game != null) acts.Add(Json.O("type", 0L, "name", game));
            return Json.O("member", Json.O("user", Json.O("id", id), "nick", nick, "roles", new List<object>(roles),
                "presence", Json.O("status", status, "activities", acts)));
        }

        static void InjectMemberList(Session s)
        {
            s.Inject("GUILD_MEMBER_LIST_UPDATE", Json.O("guild_id", "900", "id", "everyone", "member_count", 40L, "online_count", 3L,
                "ops", new List<object> { Json.O("op", "SYNC", "range", new List<object> { 0L, 99L }, "items", new List<object> {
                    Json.O("group", Json.O("id", "901", "count", 1L)),
                    MemberItem("200", "online", "VALORANT", null, "901"),
                    Json.O("group", Json.O("id", "online", "count", 2L)),
                    MemberItem("201", "idle", null, null),
                    MemberItem("202", "dnd", "Minecraft", "Carlo the Great"),
                    Json.O("group", Json.O("id", "offline", "count", 1L)),
                    MemberItem("203", "offline", null, null) }) }));
        }

        static string MemberListTest()
        {
            var s = FakeSession();
            var ch = s.Guilds["900"].Channels["911"];
            s.SubscribeMembers(ch);                       // not connected: only records the subscription
            InjectMemberList(s);
            var ml = s.MembersFor(ch);
            Assert(ml != null && ml.Items.Count == 7 && ml.OnlineCount == 3, "sync");
            Assert(s.Guilds["900"].ColorFor(new List<object> { "901" }) == 15277667, "role colour");
            s.Inject("GUILD_MEMBER_LIST_UPDATE", Json.O("guild_id", "900", "id", "everyone", "ops", new List<object> {
                Json.O("op", "INSERT", "index", 3L, "item", MemberItem("206", "online", null, null)),
                Json.O("op", "UPDATE", "index", 4L, "item", MemberItem("201", "online", "Sun Haven", null)),
                Json.O("op", "DELETE", "index", 7L) }));
            Assert(ml.Items.Count == 7, "after ops: " + ml.Items.Count);
            Assert(ml.Items[3].Obj("member").Obj("user").Str("id") == "206", "insert");
            Assert(ml.Items[4].Obj("member").Obj("presence").Str("status") == "online", "update");
            Assert(s.PresenceOf("206").Status == "online", "non-friend member presence not stored");
            s.UnsubscribeMembers(ch);
            Assert(s.MembersFor(ch) == null, "unsubscribe keeps the binding");
            return null;
        }

        static readonly Color[] artColors = { Color.FromArgb(0xF2, 0x9E, 0x4C), Color.FromArgb(0x5B, 0xA8, 0xE0), Color.FromArgb(0x8E, 0x6B, 0xD8),
                                              Color.FromArgb(0x4C, 0xB8, 0x8A), Color.FromArgb(0xE0, 0x6B, 0x8B), Color.FromArgb(0xD8, 0xB0, 0x3C) };

        // Avatars: a smiley on a pastel square. Game art: a play button on dark purple.
        static Image FakeArt(string url)
        {
            bool avatar = url.Contains("/embed/avatars/") || url.Contains("/avatars/");
            bool game = url.Contains("/app-assets/") || url.Contains("/app-icons/");
            if (!avatar && !game) return null;
            var bmp = new Bitmap(128, 128);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (avatar)
                {
                    using (var br = new SolidBrush(artColors[(url.GetHashCode() & 0x7fffffff) % artColors.Length])) g.FillRectangle(br, 0, 0, 128, 128);
                    g.DrawImage(Icons.Status("online", 80), new Rectangle(24, 24, 80, 80));
                }
                else
                {
                    using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 128, 128), Color.FromArgb(0x3A, 0x1F, 0x6B), Color.FromArgb(0x1A, 0x10, 0x30), 90f))
                        g.FillRectangle(br, 0, 0, 128, 128);
                    using (var pen = new Pen(Color.White, 6)) g.DrawEllipse(pen, 30, 30, 68, 68);
                    g.FillPolygon(Brushes.White, new[] { new Point(56, 46), new Point(56, 82), new Point(84, 64) });
                }
            }
            return bmp;
        }

        static string RenderUi(string dir)
        {
            Directory.CreateDirectory(dir);
            Application.EnableVisualStyles();
            if (SynchronizationContext.Current == null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            PresenceManager.Instance = new PresenceManager();
            // The made-up accounts and games get drawn pictures, so the screenshots (also used on the
            // website) show no Discord or game artwork.
            ImageCache.Override = FakeArt;
            var s = FakeSession();
            var app = new AppContext(true, true);
            Theme.Init();
            Theme.ForceForTest(false);
            Settings.SetBool("Sounds", false);
            Favorites.SetPinned(s, "911", true);
            Favorites.SetFollowed(s, "911", true);
            Settings.Set("Collapsed." + s.TokenKey, "Offline|guilds|open:900");   // show the server's channels, voice included
            var list = new BuddyListForm(s, app) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(280, 620) };
            list.Show();
            Pump(2500);
            Capture(list, Path.Combine(dir, "buddylist.png"));

            // In a voice channel: the voice bar, and the lounge lit up with Alyssa talking.
            var call = new VoiceCall(s, s.Guilds["900"].Channels["914"]);
            call.FakeForTest("200");
            s.Inject("VOICE_STATE_UPDATE", Json.O("user_id", "100", "channel_id", "914", "guild_id", "900", "session_id", "me"));
            VoiceManager.SetForTest(call);
            Pump(1500);
            Capture(list, Path.Combine(dir, "buddylist-voice.png"));
            list.Height = 980;   // tall enough to show the server's channels, voice included
            Pump(800);
            Capture(list, Path.Combine(dir, "buddylist-servers.png"));
            list.Height = 620;
            IncomingCallForm.Open(list, s, "500", false);
            Pump(1200);
            foreach (Form f in Application.OpenForms) if (f is IncomingCallForm) { Capture(f, Path.Combine(dir, "incoming-call.png")); f.Close(); break; }
            var voiceSettings = new VoiceSettingsForm { StartPosition = FormStartPosition.Manual, Location = new Point(360, 40) };
            voiceSettings.Show();
            Pump(800);
            Capture(voiceSettings, Path.Combine(dir, "voice-settings.png"));
            voiceSettings.Close();
            Notifier.Alert(list, AlertKind.Mention, "Alyssa in #general (PH Gamers)", "@Marc are you on tonight?", "911", null, false);
            Notifier.Alert(list, AlertKind.SignIn, "Ben is online", "Ben just signed in.", null, "201", false);
            Notifier.Alert(list, AlertKind.FriendRequest, "Friend request", "Kawakaze wants to be your friend.", null, "205", false);

            var chat = new ChatForm(s, s.PrivateChannels["500"], false) { StartPosition = FormStartPosition.Manual, Location = new Point(340, 40) };
            chat.Show();
            Pump(1500);
            long t0 = 1300000000000000000L;
            string[] lines = {
                "hey are you on later?",
                "yeah after dinner **for sure**",
                "bring the duo, we're 2 games from Immortal :fire:",
                "check this clip https://example.com/clip",
                "> that ace was insane\nI know right `ez`" };
            for (int i = 0; i < lines.Length; i++)
            {
                string author = i % 2 == 0 ? "200" : "100";
                var m = Json.O("id", (t0 + (long)i * 50000000000L).ToString(), "channel_id", "500", "type", 0L, "content", lines[i],
                    "author", Json.O("id", author, "username", author == "100" ? "marc" : "alyssa"));
                chat.Add(m);
            }
            Pump(2500);
            Capture(chat, Path.Combine(dir, "chat.png"));
            // A server channel with its member list, and a group chat.
            var server = new ChatForm(s, s.Guilds["900"].Channels["911"], false) { StartPosition = FormStartPosition.Manual, Location = new Point(300, 60) };
            server.Show();
            Pump(400);
            InjectMemberList(s);
            Pump(1500);
            Capture(server, Path.Combine(dir, "chat-server.png"));
            var groupChat = new ChatForm(s, s.PrivateChannels["501"], false) { StartPosition = FormStartPosition.Manual, Location = new Point(320, 80) };
            groupChat.Show();
            Pump(1500);
            Capture(groupChat, Path.Combine(dir, "chat-group.png"));
            groupChat.Close();

            var alerts = new AlertsForm(s, list) { StartPosition = FormStartPosition.Manual, Location = new Point(360, 80) };
            alerts.Show();
            Pump(2500);
            Capture(alerts, Path.Combine(dir, "alerts.png"));
            Theme.ForceForTest(true);
            Pump(1500);
            Capture(list, Path.Combine(dir, "buddylist-dark.png"));
            VoiceManager.SetForTest(null);
            Capture(chat, Path.Combine(dir, "chat-dark.png"));
            Capture(alerts, Path.Combine(dir, "alerts-dark.png"));
            Capture(server, Path.Combine(dir, "chat-server-dark.png"));
            server.Close();
            alerts.Close();
            Theme.ForceForTest(false);
            for (int i = 0; i < 6; i++) { chat.Size = new Size(chat.Width + 40, chat.Height + 30); Pump(120); }
            Pump(800);
            Capture(chat, Path.Combine(dir, "chat-resized.png"));

            ProfileForm.Open(s, "200", null);
            Pump(3000);
            foreach (Form f in Application.OpenForms) if (f is ProfileForm) { f.Location = new Point(700, 40); Pump(800); Capture(f, Path.Combine(dir, "profile.png")); f.Close(); break; }

            // Discord's public hCaptcha site key: checks the widget really loads in the window.
            var captcha = new CaptchaForm(Json.O("captcha_sitekey", "a9b5fb07-92ff-493f-86fe-352a2803b3df", "captcha_service", "hcaptcha"))
                { StartPosition = FormStartPosition.Manual, Location = new Point(100, 60) };
            captcha.Show();
            // hCaptcha may hand out a key without a puzzle (the window then closes by itself);
            // otherwise it shows the checkbox. Either proves the widget runs on the discord.com page.
            for (int i = 0; i < 60 && !captcha.Result.IsCompleted; i++) Pump(200);
            string captchaOutcome;
            if (captcha.Result.IsCompleted)
            {
                string key = captcha.Result.Result;
                Assert(key != null && key.StartsWith("P1_"), "captcha closed without a key");
                captchaOutcome = "hCaptcha issued a key without a puzzle";
            }
            else
            {
                Capture(captcha, Path.Combine(dir, "captcha.png"));
                captcha.Close();
                captchaOutcome = "hCaptcha widget shown (captcha.png)";
            }

            var login = new LoginForm { StartPosition = FormStartPosition.Manual, Location = new Point(980, 40) };
            login.Show();
            Pump(4000);
            Capture(login, Path.Combine(dir, "login.png"));
            login.Close();
            var notice = new NoticeForm { StartPosition = FormStartPosition.Manual, Location = new Point(980, 40) };
            notice.Show();
            Pump(500);
            Capture(notice, Path.Combine(dir, "notice.png"));
            notice.Close();

            // Auto-Away under your name, the quick-reply alert and the Unread Messages window.
            app.Windows.Add(list);
            s.SetAutoAway(true);
            list.Invalidate(true);
            Pump(500);
            Capture(list, Path.Combine(dir, "buddylist-away.png"));
            s.SetAutoAway(false);
            QuickReplyForm.Show(list, "Alyssa", "are you on later? we're 2 games from Immortal", "500", "1300000000000000002", null);
            Pump(800);
            foreach (Form f in Application.OpenForms) if (f is QuickReplyForm) { Capture(f, Path.Combine(dir, "quick-reply.png")); f.Close(); break; }
            s.PrivateChannels["500"].LastMessageId = SnowflakeDaysAgo(0.01);
            s.ReadStates["500"] = SnowflakeDaysAgo(0.5);
            s.Mentions["911"] = 2;
            s.Mentions["123456789012345678"] = 1;   // a thread Buzzcord hasn't loaded
            UnreadForm.Open(app);
            Pump(3000);
            foreach (Form f in Application.OpenForms) if (f is UnreadForm) { Capture(f, Path.Combine(dir, "unread.png")); f.Close(); break; }
            app.Windows.Remove(list);
            chat.Close();
            list.Exiting = true;
            list.Close();
            app.Dispose();
            return "PNGs in " + dir + "; " + captchaOutcome;
        }
    }
}
