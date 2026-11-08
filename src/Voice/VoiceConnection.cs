using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // One connection to a Discord voice server (voice gateway v8): the websocket for signalling
    // and DAVE, and UDP for RTP audio with aead_aes256_gcm_rtpsize transport encryption. Opus
    // frames go in and out; audio devices live in AudioEngine.
    public sealed class VoiceConnection : IDisposable
    {
        public const string Mode = "aead_aes256_gcm_rtpsize";
        const byte PayloadType = 0x78;   // Opus

        readonly string url, serverId, sessionId, token;
        readonly ulong userId;
        public readonly DaveSession Dave;

        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly object rtpLock = new object();
        readonly Dictionary<uint, ulong> ssrcUsers = new Dictionary<uint, ulong>();
        ClientWebSocket ws;
        CancellationTokenSource cts;
        UdpClient udp;
        IPEndPoint server;
        AesGcm transport;
        uint ssrc, sendNonce, rtpTimestamp;
        ushort rtpSeq;
        long lastSeq = -1;
        bool resumable;
        volatile bool stopped;

        public event Action<string> StateChanged;           // human-readable
        public event Action Ready;                         // audio can flow
        public event Action<int> Closed;                   // voice gateway close code; no more reconnects
        public event Action<ulong, ushort, byte[]> OpusReceived;   // user, RTP sequence, Opus frame (UDP thread)
        public event Action<ulong, bool> Speaking;         // user, speaking
        public event Action<ulong> UserJoined, UserLeft;
        public event Action<int> Ping;                      // ms

        public uint Ssrc { get { return ssrc; } }
        public bool IsReady { get { return transport != null; } }

        public VoiceConnection(string endpoint, string serverId, ulong userId, ulong channelId, string sessionId, string token)
        {
            string host = endpoint.StartsWith("ws://") || endpoint.StartsWith("wss://") ? endpoint.TrimEnd('/') : "wss://" + endpoint.TrimEnd('/');
            url = host + "/?v=8";
            this.serverId = serverId;
            this.userId = userId;
            this.sessionId = sessionId;
            this.token = token;
            Dave = new DaveSession(userId, channelId);
            Dave.SendBinary += (op, payload) => SendBinary(op, payload);
            Dave.SendJson += (op, data) => Send(op, data);
            Dave.Log += Program.Log;
        }

        void Raise(string state) { if (StateChanged != null) StateChanged(state); }

        public void Start() { Task.Run(() => RunLoop()); }

        public void Stop()
        {
            stopped = true;
            try { if (cts != null) cts.Cancel(); } catch (Exception) { }
            try { if (ws != null) ws.Abort(); } catch (Exception) { }
            try { if (udp != null) udp.Close(); } catch (Exception) { }
        }

        public void Dispose() { Stop(); }

        // Codes after which reconnecting can't help.
        static bool Fatal(int code)
        {
            return code == 4004 || code == 4006 || code == 4011 || code == 4012 || code == 4014 || code == 4016 || code == 4021 || code == 4022;
        }

        async Task RunLoop()
        {
            int attempts = 0;
            while (!stopped)
            {
                int code = 0;
                try
                {
                    Raise(resumable ? "Reconnecting..." : "Connecting...");
                    cts = new CancellationTokenSource();
                    ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Origin", "https://discord.com");
                    ws.Options.KeepAliveInterval = TimeSpan.Zero;
                    Trace((resumable ? "resuming " : "connecting to ") + new Uri(url).Host);
                    await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
                    if (resumable) await Send(7, Json.O("server_id", serverId, "session_id", sessionId, "token", token, "seq_ack", lastSeq)).ConfigureAwait(false);
                    else await Send(0, Json.O("server_id", serverId, "user_id", userId.ToString(), "session_id", sessionId, "token", token,
                        "max_dave_protocol_version", (long)DaveSession.MaxProtocolVersion)).ConfigureAwait(false);
                    code = await ReceiveLoop().ConfigureAwait(false);
                    attempts = 0;
                }
                catch (Exception ex)
                {
                    if (stopped) break;
                    Program.Log("voice: " + ex.Message);
                }
                finally
                {
                    try { cts.Cancel(); } catch (Exception) { }
                    try { ws.Dispose(); } catch (Exception) { }
                }
                if (stopped) break;
                Trace("voice server closed the connection (code " + code + ")");
                if (Fatal(code) || ++attempts > 3 || transport == null)
                {
                    Raise("Disconnected");
                    if (Closed != null) Closed(code);
                    Stop();
                    return;
                }
                resumable = true;
                await Task.Delay(1000 * attempts).ConfigureAwait(false);
            }
        }

        async Task<int> ReceiveLoop()
        {
            var buffer = new byte[16 * 1024];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
                    msg.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                var data = msg.ToArray();
                try
                {
                    if (r.MessageType == WebSocketMessageType.Binary) HandleBinary(data);
                    else await HandleJson(Json.ParseObj(Encoding.UTF8.GetString(data))).ConfigureAwait(false);
                }
                catch (Exception ex) { Program.Log("voice message: " + ex.Message); }
            }
            return ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
        }

        // ---- diagnostics: every step goes to the log (never the token, the media key or audio) ----

        static void Trace(string s) { Program.Log("voice: " + s); }

        static string HexPreview(byte[] b, int offset)
        {
            int n = Math.Min(b.Length - offset, 6000);
            return Hex.Encode(Bytes.Slice(b, offset, Math.Max(0, n))) + (b.Length - offset > n ? "..." : "");
        }

        long sentFrames, sentEncrypted, receivedPackets, played, unknownSsrc, transportFailed, daveFailed;
        DateTime lastStats = DateTime.UtcNow;
        readonly HashSet<uint> reportedSsrcs = new HashSet<uint>();

        void LogStats()
        {
            if (DateTime.UtcNow - lastStats < TimeSpan.FromSeconds(10)) return;
            lastStats = DateTime.UtcNow;
            if (sentFrames == 0 && receivedPackets == 0) return;
            Trace("audio last 10 s: sent " + sentFrames + " frames (" + sentEncrypted + " end-to-end encrypted); received " + receivedPackets +
                " packets: " + played + " played, " + unknownSsrc + " from unknown SSRCs, " + transportFailed + " failed transport decryption, " + daveFailed + " failed DAVE decryption");
            sentFrames = sentEncrypted = receivedPackets = played = unknownSsrc = transportFailed = daveFailed = 0;
        }

        // Binary DAVE messages: sequence number, opcode, payload.
        void HandleBinary(byte[] data)
        {
            if (data.Length < 3) return;
            Trace("received DAVE op " + data[2] + " (seq " + ((data[0] << 8) | data[1]) + ", " + (data.Length - 3) + " bytes): " + HexPreview(data, 3));
            lastSeq = (data[0] << 8) | data[1];
            var payload = new byte[data.Length - 3];
            Buffer.BlockCopy(data, 3, payload, 0, payload.Length);
            Dave.OnBinary(data[2], payload);
        }

        async Task HandleJson(JObj p)
        {
            if (p == null) return;
            if (p.Has("seq") && p["seq"] is long) lastSeq = (long)p["seq"];
            var d = p.Obj("d");
            int op = (int)p.Long("op", -1);
            if (op == 4) Trace("received op 4 (session description): mode " + d.Str("mode") + ", DAVE version " + d.Long("dave_protocol_version"));
            else if (op != 6) Trace("received op " + op + ": " + Json.Write(d));
            switch (op)
            {
                case 12:  // a client's audio/video SSRCs; sent for people already in the call
                    {
                        ulong uid;
                        if (!ulong.TryParse(d.Str("user_id"), out uid)) break;
                        long audio = d.Long("audio_ssrc");
                        if (audio > 0) lock (ssrcUsers) ssrcUsers[(uint)audio] = uid;
                        Dave.SetMember(uid, true);
                        break;
                    }
                case 8:   // hello
                    StartHeartbeat((int)d.Long("heartbeat_interval", 13750));
                    break;
                case 2:   // ready
                    ssrc = (uint)d.Long("ssrc");
                    var modes = d.Arr("modes");
                    if (modes != null && !modes.Contains(Mode)) throw new InvalidOperationException("voice server doesn't offer " + Mode);
                    await SetUpUdp(d.Str("ip"), (int)d.Long("port")).ConfigureAwait(false);
                    break;
                case 4:   // session description
                    var key = d.Arr("secret_key");
                    var bytes = new byte[key.Count];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Convert.ToInt32(key[i]);
                    if (transport != null) transport.Dispose();
                    transport = new AesGcm(bytes);
                    Dave.Start((int)d.Long("dave_protocol_version"));
                    new Thread(ReceiveUdp) { IsBackground = true, Name = "Buzzcord voice UDP" }.Start();
                    Raise("Connected");
                    if (Ready != null) Ready();
                    break;
                case 9:   // resumed
                    Raise("Connected");
                    break;
                case 6:   // heartbeat ack
                    if (d != null && d.Has("t") && Ping != null) Ping((int)(PresenceManager.NowMs() - d.Long("t")));
                    break;
                case 5:   // someone's speaking state, which also tells us their SSRC
                    {
                        ulong uid;
                        if (!ulong.TryParse(d.Str("user_id"), out uid)) break;
                        lock (ssrcUsers) ssrcUsers[(uint)d.Long("ssrc")] = uid;
                        if (Speaking != null) Speaking(uid, d.Long("speaking") != 0);
                        break;
                    }
                case 11:  // clients connected
                    foreach (var o in d.Arr("user_ids") ?? new List<object>())
                    {
                        ulong uid;
                        if (!ulong.TryParse(Convert.ToString(o), out uid)) continue;
                        Dave.SetMember(uid, true);
                        if (UserJoined != null) UserJoined(uid);
                    }
                    break;
                case 13:  // client disconnected
                    {
                        ulong uid;
                        if (!ulong.TryParse(d.Str("user_id"), out uid)) break;
                        Dave.SetMember(uid, false);
                        lock (ssrcUsers)
                        {
                            var gone = new List<uint>();
                            foreach (var kv in ssrcUsers) if (kv.Value == uid) gone.Add(kv.Key);
                            foreach (var s in gone) ssrcUsers.Remove(s);
                        }
                        if (UserLeft != null) UserLeft(uid);
                        break;
                    }
                case 21: Dave.OnPrepareTransition((int)d.Long("transition_id"), (int)d.Long("protocol_version")); break;
                case 22: Dave.OnExecuteTransition((int)d.Long("transition_id")); break;
                case 24: Dave.OnPrepareEpoch((int)d.Long("epoch"), (int)d.Long("protocol_version")); break;
            }
        }

        void StartHeartbeat(int interval)
        {
            var token = cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Send(3, Json.O("t", PresenceManager.NowMs(), "seq_ack", lastSeq)).ConfigureAwait(false);
                        await Task.Delay(interval, token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { }
            });
        }

        public Task Send(int op, JObj d)
        {
            if (op == 0) Trace("sent op 0 (identify), max DAVE version " + d.Long("max_dave_protocol_version"));
            else if (op == 7) Trace("sent op 7 (resume)");
            else if (op != 3 && !(op == 5 && sentSpeaking++ > 0)) Trace("sent op " + op + ": " + Json.Write(d));
            return SendRaw(Encoding.UTF8.GetBytes(Json.Write(Json.O("op", (long)op, "d", d))), WebSocketMessageType.Text);
        }

        int sentSpeaking;

        Task SendBinary(int op, byte[] payload)
        {
            Trace("sent DAVE op " + op + " (" + payload.Length + " bytes): " + HexPreview(payload, 0));
            var b = new byte[payload.Length + 1];
            b[0] = (byte)op;
            Buffer.BlockCopy(payload, 0, b, 1, payload.Length);
            return SendRaw(b, WebSocketMessageType.Binary);
        }

        async Task SendRaw(byte[] bytes, WebSocketMessageType type)
        {
            var socket = ws;
            if (socket == null || socket.State != WebSocketState.Open) return;
            await sendLock.WaitAsync().ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Program.Log("voice send: " + ex.Message); }
            finally { sendLock.Release(); }
        }

        // ---- UDP ----

        async Task SetUpUdp(string ip, int port)
        {
            if (udp != null) try { udp.Close(); } catch (Exception) { }
            udp = new UdpClient(0, AddressFamily.InterNetwork);
            server = new IPEndPoint(IPAddress.Parse(ip), port);
            // IP discovery: ask the server what address and port it sees us on.
            var probe = new byte[74];
            probe[1] = 1;
            probe[3] = 70;
            WriteU32(probe, 4, ssrc);
            string address = null;
            int ourPort = 0;
            for (int attempt = 0; attempt < 3 && address == null; attempt++)
            {
                await udp.SendAsync(probe, probe.Length, server).ConfigureAwait(false);
                var receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(2000)).ConfigureAwait(false) != receive) continue;
                var reply = receive.Result.Buffer;
                if (reply.Length < 74 || reply[1] != 2) continue;
                int end = 8;
                while (end < 72 && reply[end] != 0) end++;
                address = Encoding.ASCII.GetString(reply, 8, end - 8);
                ourPort = (reply[72] << 8) | reply[73];
            }
            if (address == null) throw new InvalidOperationException("voice server didn't answer IP discovery");
            await Send(1, Json.O("protocol", "udp", "data", Json.O("address", address, "port", (long)ourPort, "mode", Mode))).ConfigureAwait(false);
        }

        void ReceiveUdp()
        {
            var socket = udp;
            var lastKeepAlive = DateTime.UtcNow;
            uint keepAlive = 0;
            while (!stopped && socket == udp)
            {
                byte[] packet;
                LogStats();
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    if (DateTime.UtcNow - lastKeepAlive > TimeSpan.FromSeconds(5))
                    {
                        // Keeps NAT mappings open while nobody talks.
                        lastKeepAlive = DateTime.UtcNow;
                        var ka = BitConverter.GetBytes((ulong)keepAlive++);
                        socket.Send(ka, ka.Length, server);
                    }
                    if (!socket.Client.Poll(250000, SelectMode.SelectRead)) continue;
                    packet = socket.Receive(ref from);
                }
                catch (Exception) { if (stopped) return; Thread.Sleep(50); continue; }
                try { HandlePacket(packet); }
                catch (Exception ex) { Program.Log("voice packet: " + ex.Message); }
            }
        }

        void HandlePacket(byte[] packet)
        {
            ushort seq;
            uint packetSsrc;
            if (packet.Length <= 8 || packet[1] >= 200 && packet[1] <= 204) return;   // keepalive echoes and RTCP
            if ((packet[1] & 0x7F) != PayloadType) return;   // WebRTC clients also send video/RTX keepalives and padding probes
            receivedPackets++;
            var payload = OpenRtp(transport, packet, out seq, out packetSsrc);
            if (payload == null) { transportFailed++; return; }
            ulong uid;
            bool known;
            lock (ssrcUsers) known = ssrcUsers.TryGetValue(packetSsrc, out uid);
            if (!known)
            {
                unknownSsrc++;
                if (reportedSsrcs.Add(packetSsrc)) Trace("audio from SSRC " + packetSsrc + ", which no op 5/12 has named yet");
                return;
            }
            var frame = Dave.Decrypt(uid, payload);
            if (frame == null) { daveFailed++; return; }
            played++;
            if (OpusReceived != null) OpusReceived(uid, seq, frame);
        }

        // ---- RTP with aead_aes256_gcm_rtpsize ----

        // Sends one Opus frame (20 ms): DAVE first, then RTP and transport encryption.
        public void SendOpus(byte[] opus)
        {
            var t = transport;
            var socket = udp;
            if (t == null || socket == null) return;
            byte[] packet;
            lock (rtpLock)
            {
                var frame = Dave.Encrypt(opus);
                sentFrames++;
                if (!ReferenceEquals(frame, opus)) sentEncrypted++;
                packet = SealRtp(t, rtpSeq++, rtpTimestamp, ssrc, ++sendNonce, frame);
                rtpTimestamp += 960;
            }
            try { socket.Send(packet, packet.Length, server); }
            catch (Exception ex) { Program.Log("voice udp send: " + ex.Message); }
        }

        public void SetSpeaking(bool speaking)
        {
            Send(5, Json.O("speaking", speaking ? 1L : 0L, "delay", 0L, "ssrc", (long)ssrc)).ContinueWith(x => { var ignored = x.Exception; });
        }

        static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static uint ReadU32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }

        static byte[] Nonce(byte[] counter, int offset)
        {
            var n = new byte[12];
            Buffer.BlockCopy(counter, offset, n, 0, 4);
            return n;
        }

        // Header (the AAD), then ciphertext and tag, then the 4-byte nonce counter.
        public static byte[] SealRtp(AesGcm key, ushort seq, uint timestamp, uint ssrc, uint nonce, byte[] payload)
        {
            var header = new byte[12];
            header[0] = 0x80;
            header[1] = PayloadType;
            header[2] = (byte)(seq >> 8);
            header[3] = (byte)seq;
            WriteU32(header, 4, timestamp);
            WriteU32(header, 8, ssrc);
            var counter = new byte[4];
            WriteU32(counter, 0, nonce);
            var sealedPayload = key.Seal(Nonce(counter, 0), payload, header, 16);
            return Bytes.Concat(header, sealedPayload, counter);
        }

        // The RTP payload of a voice packet, or null for RTCP, junk or anything that fails to decrypt.
        // The header and any extension preamble are the AAD; the extension body is encrypted.
        public static byte[] OpenRtp(AesGcm key, byte[] p, out ushort seq, out uint ssrc)
        {
            seq = 0;
            ssrc = 0;
            if (key == null || p.Length < 12 + 16 + 4 || (p[0] >> 6) != 2) return null;
            if (p[1] >= 200 && p[1] <= 204) return null; // RTCP
            int header = 12 + 4 * (p[0] & 0x0F);
            bool extension = (p[0] & 0x10) != 0;
            int extensionWords = 0;
            if (extension)
            {
                header += 4;
                if (p.Length < header + 16 + 4) return null;
                extensionWords = (p[header - 2] << 8) | p[header - 1];
            }
            seq = (ushort)((p[2] << 8) | p[3]);
            ssrc = ReadU32(p, 8);
            var aad = Bytes.Slice(p, 0, header);
            var plain = key.Open(Nonce(p, p.Length - 4), p, header, p.Length - header - 4, aad, 16);
            if (plain == null) return null;
            int skip = extension ? extensionWords * 4 : 0;
            int end = plain.Length;
            if ((p[0] & 0x20) != 0 && end > 0) end -= plain[end - 1];   // RTP padding
            if (skip > end) return null;
            return Bytes.Slice(plain, skip, end - skip);
        }
    }
}
