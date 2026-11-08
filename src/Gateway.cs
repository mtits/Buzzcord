using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // One live connection to Discord's gateway for one account. Runs on background threads and
    // reports events through Dispatch; reconnects and resumes by itself.
    public class Gateway
    {
        const string DefaultUrl = "wss://gateway.discord.gg";
        // DEDUPE_USER_OBJECTS: users are sent once in READY.users and referenced by id elsewhere.
        const int Capabilities = 16;

        readonly string token;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        ClientWebSocket ws;
        CancellationTokenSource cts;
        string sessionId, resumeUrl;
        long seq = -1;
        bool acked = true;
        volatile bool stopped;
        Func<JObj> presenceSource;

        public event Action<string, JObj> Dispatch;    // event name, data (background thread)
        public event Action<string> StateChanged;      // human-readable connection state
        public event Action AuthFailed;                // token rejected; do not reconnect
        public event Action<int> Closed;               // close code from Discord (background thread)

        public Gateway(string token, Func<JObj> presenceSource)
        {
            this.token = token;
            this.presenceSource = presenceSource;
        }

        public bool Connected { get; private set; }

        public void Start()
        {
            stopped = false;
            Task.Run(() => RunLoop());
        }

        public void Stop()
        {
            stopped = true;
            try { if (cts != null) cts.Cancel(); } catch (Exception) { }
            try { if (ws != null) ws.Abort(); } catch (Exception) { }
        }

        async Task RunLoop()
        {
            int failures = 0;
            while (!stopped)
            {
                bool resume = sessionId != null && seq >= 0;
                string url = (resume && resumeUrl != null ? resumeUrl : DefaultUrl) + "/?encoding=json&v=9";
                int closeCode = 0;
                try
                {
                    Raise(resume ? "Reconnecting..." : "Connecting...");
                    cts = new CancellationTokenSource();
                    ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Origin", "https://discord.com");
                    ws.Options.KeepAliveInterval = TimeSpan.Zero;
                    await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
                    closeCode = await ReceiveLoop(resume).ConfigureAwait(false);
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (stopped) break;
                    Program.Log("gateway: " + ex.Message);
                    failures++;
                }
                finally
                {
                    Connected = false;
                    try { cts.Cancel(); } catch (Exception) { }
                    try { ws.Dispose(); } catch (Exception) { }
                }
                if (stopped) break;
                if (closeCode != 0 && Closed != null) Closed(closeCode);
                if (closeCode == 4004)
                {
                    Raise("Signed out (token no longer valid)");
                    if (AuthFailed != null) AuthFailed();
                    return;
                }
                // These codes mean the session can't be resumed.
                if (closeCode == 4007 || closeCode == 4009 || closeCode == 4003) { sessionId = null; seq = -1; }
                Raise("Disconnected, retrying...");
                await Task.Delay(Math.Min(30000, 1000 * (1 << Math.Min(failures, 5))) + new Random().Next(500)).ConfigureAwait(false);
            }
            Raise("Offline");
        }

        async Task<int> ReceiveLoop(bool resume)
        {
            var buffer = new byte[64 * 1024];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        return ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
                    msg.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                string text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                if (msg.Capacity > 4 * 1024 * 1024) msg = new MemoryStream(); // READY can be huge; don't keep it
                JObj p;
                try { p = Json.ParseObj(text); }
                catch (Exception ex) { Program.Log("gateway parse: " + ex.Message); continue; }
                if (p == null) continue;
                int op = (int)p.Long("op", -1);
                switch (op)
                {
                    case 10: // hello
                        var hello = p.Obj("d");
                        StartHeartbeat((int)hello.Long("heartbeat_interval", 41250));
                        if (resume) await Send(Json.O("op", 6, "d", Json.O("token", token, "session_id", sessionId, "seq", seq))).ConfigureAwait(false);
                        else await Identify().ConfigureAwait(false);
                        break;
                    case 11:
                        acked = true;
                        break;
                    case 1:
                        await Send(Json.O("op", 1, "d", seq >= 0 ? (object)seq : null)).ConfigureAwait(false);
                        break;
                    case 7: // server asks us to reconnect and resume
                        await CloseQuietly().ConfigureAwait(false);
                        return 0;
                    case 9: // invalid session
                        bool canResume = p.ContainsKey("d") && p["d"] is bool && (bool)p["d"];
                        if (!canResume) { sessionId = null; seq = -1; }
                        await Task.Delay(1000 + new Random().Next(4000)).ConfigureAwait(false);
                        if (canResume) await Send(Json.O("op", 6, "d", Json.O("token", token, "session_id", sessionId, "seq", seq))).ConfigureAwait(false);
                        else await Identify().ConfigureAwait(false);
                        break;
                    case 0:
                        if (p["s"] is long) seq = (long)p["s"];
                        string t = p.Str("t");
                        var d = p.Obj("d");
                        // A few events (SESSIONS_REPLACE) carry a bare array; hand it over as d.list.
                        if (d == null && p["d"] is System.Collections.Generic.List<object>) d = Json.O("list", p["d"]);
                        if (t == "READY")
                        {
                            sessionId = d.Str("session_id");
                            resumeUrl = d.Str("resume_gateway_url");
                            Connected = true;
                            Raise("Online");
                        }
                        else if (t == "RESUMED")
                        {
                            Connected = true;
                            Raise("Online");
                        }
                        if (Dispatch != null)
                        {
                            try { Dispatch(t, d); }
                            catch (Exception ex) { Program.Log("dispatch " + t + ": " + ex); }
                        }
                        break;
                }
            }
            return ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
        }

        Task Identify()
        {
            var presence = presenceSource != null ? presenceSource() : null;
            if (presence == null) presence = Json.O("status", "online", "since", 0, "activities", new object[0], "afk", false);
            return Send(Json.O("op", 2, "d", Json.O(
                "token", token,
                "capabilities", Capabilities,
                "properties", ClientInfo.Properties(),
                "presence", presence,
                "compress", false,
                "client_state", Json.O("guild_versions", new JObj()))));
        }

        void StartHeartbeat(int interval)
        {
            acked = true;
            var token = cts.Token;
            var socket = ws;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay((int)(interval * new Random().NextDouble()), token).ConfigureAwait(false);
                    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        if (!acked)
                        {
                            // No answer to the last beat: the connection is dead. Drop it and resume.
                            Program.Log("gateway: heartbeat not acknowledged, reconnecting");
                            socket.Abort();
                            return;
                        }
                        acked = false;
                        await Send(Json.O("op", 1, "d", seq >= 0 ? (object)seq : null)).ConfigureAwait(false);
                        await Task.Delay(interval, token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { }
            });
        }

        public async Task Send(JObj payload)
        {
            var socket = ws;
            if (socket == null || socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(Json.Write(payload));
            await sendLock.WaitAsync().ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Program.Log("gateway send: " + ex.Message); }
            finally { sendLock.Release(); }
        }

        public Task UpdatePresence(JObj presence)
        {
            return Send(Json.O("op", 3, "d", presence));
        }

        async Task CloseQuietly()
        {
            // A 1000 close would end the session; any 4000-range code keeps it resumable.
            try { await ws.CloseOutputAsync((WebSocketCloseStatus)4000, "reconnect", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
        }

        void Raise(string state)
        {
            if (StateChanged != null) StateChanged(state);
        }
    }
}
