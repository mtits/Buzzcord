using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Buzzcord
{
    // Voice transport, Opus and a whole call against a fake voice server on localhost (websocket
    // and UDP), with DAVE: a pretend second caller decrypts what we send and talks back.
    static partial class SelfTest
    {
        static string RtpTest()
        {
            var keyBytes = Mls.Random(32);
            using (var key = new AesGcm(keyBytes))
            {
                var payload = Encoding.ASCII.GetBytes("opus frame");
                var packet = VoiceConnection.SealRtp(key, 65535, 123456, 0xDEADBEEF, 7, payload);
                Assert(packet.Length == 12 + payload.Length + 16 + 4 && packet[0] == 0x80 && packet[1] == 0x78, "packet layout");
                Assert(Hex.Encode(Bytes.Slice(packet, packet.Length - 4, 4)) == "00000007", "nonce counter at the end");
                ushort seq; uint ssrc;
                var back = VoiceConnection.OpenRtp(key, packet, out seq, out ssrc);
                Assert(back != null && Encoding.ASCII.GetString(back) == "opus frame" && seq == 65535 && ssrc == 0xDEADBEEF, "round trip");
                packet[14] ^= 1;
                Assert(VoiceConnection.OpenRtp(key, packet, out seq, out ssrc) == null, "tampered packet opened");
                var rtcp = (byte[])packet.Clone();
                rtcp[1] = 200;
                Assert(VoiceConnection.OpenRtp(key, rtcp, out seq, out ssrc) == null, "RTCP treated as audio");

                // With a header extension: the 4-byte preamble is authenticated, the body encrypted.
                var header = new byte[] { 0x90, 0x78, 0, 1, 0, 0, 0, 0, 0, 0, 0, 9, 0xBE, 0xDE, 0, 1 };
                var body = Bytes.Concat(new byte[] { 0x10, 0xAA, 0, 0 }, payload);
                var counter = new byte[] { 0, 0, 0, 3 };
                var nonce = new byte[12];
                Buffer.BlockCopy(counter, 0, nonce, 0, 4);
                var withExt = Bytes.Concat(header, key.Seal(nonce, body, header, 16), counter);
                back = VoiceConnection.OpenRtp(key, withExt, out seq, out ssrc);
                Assert(back != null && Encoding.ASCII.GetString(back) == "opus frame" && ssrc == 9, "extension body not stripped");
            }
            return null;
        }

        static short[] Tone(int frame, double freq, double amplitude)
        {
            var pcm = new short[AudioEngine.FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                double t = (frame * AudioEngine.FrameSamples + i) / (double)AudioEngine.Rate;
                pcm[i] = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * freq * t));
            }
            return pcm;
        }

        static double Rms(short[] pcm, int start, int count)
        {
            double sum = 0;
            for (int i = start; i < start + count; i++) sum += (double)pcm[i] * pcm[i];
            return Math.Sqrt(sum / count);
        }

        static string OpusTest()
        {
            var engine = new AudioEngine();
            var frames = new List<byte[]>();
            var talking = new List<bool>();
            engine.FrameReady += f => frames.Add(f);
            engine.TransmittingChanged += t => talking.Add(t);
            var now = new DateTime(2026, 1, 1);
            for (int i = 0; i < 50; i++) engine.ProcessCaptured(Tone(i, 440, 0.3), now.AddMilliseconds(20 * i));     // 1 s of tone
            int toneFrames = frames.Count;
            for (int i = 50; i < 80; i++) engine.ProcessCaptured(new short[AudioEngine.FrameSamples], now.AddMilliseconds(20 * i));   // then quiet
            Assert(toneFrames == 50, "expected 50 tone frames, got " + toneFrames);
            Assert(talking.Count == 2 && talking[0] && !talking[1], "talking didn't start and stop once");
            int silence = 0;
            foreach (var f in frames) if (Bytes.Equal(f, AudioEngine.SilenceFrame)) silence++;
            Assert(silence == 5, "expected 5 silence frames, got " + silence);

            // Play it back through a jitter buffer and the mixer, one packet per 20 ms as on the wire.
            ushort seq = 65530;   // wraps past 65535 on the way
            var mixed = new List<short>();
            foreach (var f in frames) { engine.Receive(77, seq++, f); mixed.AddRange(engine.Mix()); }
            for (int i = 0; i < 5; i++) mixed.AddRange(engine.Mix());
            var all = mixed.ToArray();
            double inRms = Rms(Tone(10, 440, 0.3), 0, AudioEngine.FrameSamples);
            double outRms = Rms(all, 2 * AudioEngine.FrameSamples * 10, 2 * AudioEngine.FrameSamples * 30);
            Assert(outRms > inRms * 0.6 && outRms < inRms * 1.4, "decoded level " + (int)outRms + " vs " + (int)inRms);

            // A lost packet is concealed, and a stream goes quiet when packets stop.
            var s = new PlaybackStream();
            s.Push(1, frames[0]); s.Push(2, frames[1]); s.Push(4, frames[3]);
            int played = 0;
            for (int i = 0; i < 12; i++) if (s.Next() != null) played++;
            Assert(played == 4 + 5 && !s.Playing, "jitter buffer played " + played + " frames");

            engine.Muted = true;
            frames.Clear();
            engine.ProcessCaptured(Tone(0, 440, 0.3), now.AddSeconds(5));
            Assert(frames.Count == 0, "muted mic still sent audio");
            return toneFrames + " frames, level " + (int)outRms + "/" + (int)inRms + "; " + AudioEngine.InputDevices().Count + " mics, " + AudioEngine.OutputDevices().Count + " outputs";
        }

        // A minimal Discord voice server: websocket (text and binary frames) and UDP.
        sealed class FakeVoiceServer : IDisposable
        {
            readonly TcpListener tcp = new TcpListener(IPAddress.Loopback, 0);
            readonly UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            NetworkStream stream;
            IPEndPoint client;
            public readonly BlockingCollection<JObj> Text = new BlockingCollection<JObj>();
            public readonly BlockingCollection<byte[]> Binary = new BlockingCollection<byte[]>();
            public readonly BlockingCollection<byte[]> Udp = new BlockingCollection<byte[]>();
            public int WsPort, UdpPort;
            ushort seq;
            volatile bool done;

            public FakeVoiceServer()
            {
                tcp.Start();
                WsPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
                UdpPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                new Thread(UdpLoop) { IsBackground = true }.Start();
            }

            public void Accept()
            {
                var accept = tcp.AcceptTcpClientAsync();
                Assert(accept.Wait(5000), "client never connected to the voice server");
                stream = accept.Result.GetStream();
                var request = new StringBuilder();
                while (!request.ToString().EndsWith("\r\n\r\n")) request.Append((char)stream.ReadByte());
                string key = null;
                foreach (var line in request.ToString().Split('\n'))
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
                string acceptKey;
                using (var sha = SHA1.Create()) acceptKey = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + acceptKey + "\r\n\r\n");
                stream.Write(reply, 0, reply.Length);
                new Thread(ReadLoop) { IsBackground = true }.Start();
            }

            byte[] Read(int n)
            {
                var b = new byte[n];
                int got = 0;
                while (got < n) { int r = stream.Read(b, got, n - got); if (r <= 0) throw new Exception("closed"); got += r; }
                return b;
            }

            void ReadLoop()
            {
                try
                {
                    while (!done)
                    {
                        var h = Read(2);
                        int op = h[0] & 0x0F;
                        long len = h[1] & 0x7F;
                        if (len == 126) { var l = Read(2); len = (l[0] << 8) | l[1]; }
                        else if (len == 127) { var l = Read(8); len = 0; foreach (var x in l) len = (len << 8) | x; }
                        var mask = (h[1] & 0x80) != 0 ? Read(4) : null;
                        var payload = Read((int)len);
                        if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                        if (op == 1) Text.Add(Json.ParseObj(Encoding.UTF8.GetString(payload)));
                        else if (op == 2) Binary.Add(payload);
                        else if (op == 8) return;
                    }
                }
                catch (Exception) { }
            }

            void Frame(int op, byte[] payload)
            {
                var w = new TlsWriter().U8(0x80 | op);
                if (payload.Length < 126) w.U8(payload.Length);
                else if (payload.Length < 65536) w.U8(126).U16(payload.Length);
                else w.U8(127).U64((ulong)payload.Length);
                w.Raw(payload);
                var b = w.ToArray();
                lock (this) stream.Write(b, 0, b.Length);
            }

            public void SendJson(int op, JObj d) { Frame(1, Encoding.UTF8.GetBytes(Json.Write(Json.O("op", (long)op, "d", d)))); }
            public void SendBinary(int op, byte[] payload) { Frame(2, new TlsWriter().U16(seq++).U8(op).Raw(payload).ToArray()); }

            public JObj Expect(int op)
            {
                JObj m;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (Text.TryTake(out m, Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                    if (m.Long("op", -1) == op) return m.Obj("d");
                throw new Exception("voice client never sent op " + op);
            }

            public byte[] ExpectBinary(int op)
            {
                byte[] b;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (Binary.TryTake(out b, Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                    if (b[0] == op) return Bytes.Slice(b, 1, b.Length - 1);
                throw new Exception("voice client never sent binary op " + op);
            }

            public byte[] ExpectAudio()
            {
                byte[] p;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (Udp.TryTake(out p, Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                    if (p.Length > 12 + 16 + 4) return p;   // skip keepalives
                throw new Exception("no audio packet arrived");
            }

            void UdpLoop()
            {
                try
                {
                    while (!done)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var p = udp.Receive(ref from);
                        client = from;
                        if (p.Length == 74 && p[1] == 1)
                        {
                            var reply = new byte[74];
                            reply[1] = 2; reply[3] = 70;
                            Buffer.BlockCopy(p, 4, reply, 4, 4);
                            var addr = Encoding.ASCII.GetBytes("127.0.0.1");
                            Buffer.BlockCopy(addr, 0, reply, 8, addr.Length);
                            reply[72] = (byte)(from.Port >> 8); reply[73] = (byte)from.Port;
                            udp.Send(reply, reply.Length, from);
                        }
                        else Udp.Add(p);
                    }
                }
                catch (Exception) { }
            }

            public void SendUdp(byte[] p) { udp.Send(p, p.Length, client); }
            public int ClientPort { get { return client != null ? client.Port : 0; } }

            public void Dispose()
            {
                done = true;
                try { tcp.Stop(); } catch (Exception) { }
                try { udp.Close(); } catch (Exception) { }
            }
        }

        // Discord's call events: ringing us, then not; who's in the call.
        static string CallEventsTest()
        {
            var s = FakeSession();
            Assert(s.VoiceMembers("914").Count == 2, "voice states from READY");
            var rang = new List<string>();
            s.IncomingCall += cid => rang.Add(cid);
            s.Inject("CALL_CREATE", Json.O("channel_id", "500", "ringing", new List<object> { "100" },
                "voice_states", new List<object> { Json.O("user_id", "200", "channel_id", "500", "session_id", "x") }));
            Assert(rang.Count == 1 && rang[0] == "500" && s.IsRingingMe("500"), "incoming call not raised");
            Assert(s.VoiceMembers("500").Contains("200"), "caller not in the call");
            s.Inject("CALL_UPDATE", Json.O("channel_id", "500", "ringing", new List<object>()));
            Assert(!s.IsRingingMe("500") && rang.Count == 1, "still ringing after the update");
            s.Inject("VOICE_STATE_UPDATE", Json.O("user_id", "200", "channel_id", null, "session_id", "x"));
            Assert(s.VoiceMembers("500").Count == 0, "caller didn't leave");
            s.Inject("CALL_DELETE", Json.O("channel_id", "500"));
            Assert(!s.Calls.ContainsKey("500"), "call not removed");
            return null;
        }

        // The whole join path without audio devices: main gateway voice events, then the voice
        // server, then mute, speaking and leaving.
        static string VoiceManagerTest()
        {
            VoiceManager.OpenDevices = false;
            var s = FakeSession();
            var lounge = s.Guilds["900"].Channels["914"];
            using (var server = new FakeVoiceServer())
            {
                var join = VoiceManager.Join(s, lounge);
                Assert(VoiceManager.IsIn(s, "914"), "call not current while joining");
                s.Inject("VOICE_STATE_UPDATE", Json.O("user_id", "100", "channel_id", "914", "guild_id", "900", "session_id", "vs1"));
                s.Inject("VOICE_SERVER_UPDATE", Json.O("token", "vt", "guild_id", "900", "endpoint", "ws://127.0.0.1:" + server.WsPort));
                server.Accept();
                var identify = server.Expect(0);
                Assert(identify.Str("session_id") == "vs1" && identify.Str("token") == "vt" && identify.Str("server_id") == "900", "identify: " + Json.Write(identify));
                server.SendJson(8, Json.O("heartbeat_interval", 30000L));
                server.SendJson(2, Json.O("ssrc", 31L, "ip", "127.0.0.1", "port", (long)server.UdpPort, "modes", new List<object> { VoiceConnection.Mode }));
                server.Expect(1);
                var key = new List<object>();
                for (int i = 0; i < 32; i++) key.Add((long)i);
                server.SendJson(4, Json.O("mode", VoiceConnection.Mode, "secret_key", key, "dave_protocol_version", 0L));
                Assert(join.Wait(5000), "join never finished");
                var call = VoiceManager.Current;
                for (int i = 0; i < 100 && !call.Connected; i++) Thread.Sleep(50);
                Assert(call.Connected && call.State == "Voice connected", "not connected: " + call.State);
                Assert(call.Participants.Contains("100") && call.Participants.Contains("200"), "participants: " + string.Join(",", call.Participants));
                server.SendJson(5, Json.O("user_id", "200", "ssrc", 99L, "speaking", 1L));
                for (int i = 0; i < 100 && !call.IsSpeaking("200"); i++) Thread.Sleep(50);
                Assert(call.IsSpeaking("200"), "speaking indicator");
                call.SetDeafened(true);
                Assert(call.Deafened && call.Muted, "deafen should also mute");
                call.SetMuted(false);
                Assert(!call.Muted && !call.Deafened, "unmute should undeafen");
                call.Leave();
                Assert(VoiceManager.Current == null && !call.Connected, "still in the call after leaving");
            }
            VoiceManager.OpenDevices = true;
            return null;
        }

        static string VoiceCallTest()
        {
            const ulong me = 1001, friend = 42, channel = 555;
            var key = Mls.Random(32);
            using (var server = new FakeVoiceServer())
            using (var conn = new VoiceConnection("ws://127.0.0.1:" + server.WsPort, "999", me, channel, "sess", "tok"))
            using (var transport = new AesGcm(key))
            {
                var ready = new ManualResetEvent(false);
                var speaking = new ManualResetEvent(false);
                var received = new BlockingCollection<KeyValuePair<ulong, byte[]>>();
                conn.Ready += () => ready.Set();
                conn.Speaking += (u, on) => { if (u == friend) speaking.Set(); };
                conn.OpusReceived += (u, s, f) => received.Add(new KeyValuePair<ulong, byte[]>(u, f));
                conn.Start();
                server.Accept();

                var identify = server.Expect(0);
                Assert(identify.Str("server_id") == "999" && identify.Str("user_id") == "1001" && identify.Long("max_dave_protocol_version") == 1, "identify: " + Json.Write(identify));
                server.SendJson(8, Json.O("heartbeat_interval", 30000L));
                server.SendJson(2, Json.O("ssrc", 777L, "ip", "127.0.0.1", "port", (long)server.UdpPort, "modes", new List<object> { VoiceConnection.Mode }));
                var select = server.Expect(1);
                var data = select.Obj("data");
                Assert(select.Str("protocol") == "udp" && data.Str("address") == "127.0.0.1" && data.Long("port") == server.ClientPort && data.Str("mode") == VoiceConnection.Mode, "select protocol: " + Json.Write(select));

                // DAVE: the gateway's identity, the session description, then who's here.
                var gatewayPriv = new byte[1][];
                byte[] gatewayPub;
                P256.Generate(out gatewayPriv[0], out gatewayPub);
                var external = new ExternalSender { SignatureKey = gatewayPub, Credential = Credential.Basic(Encoding.ASCII.GetBytes("gw")) };
                server.SendBinary(25, external.Bytes());
                var keyList = new List<object>();
                foreach (var b in key) keyList.Add((long)b);
                server.SendJson(4, Json.O("mode", VoiceConnection.Mode, "secret_key", keyList, "dave_protocol_version", 1L));
                Assert(ready.WaitOne(5000), "connection never became ready");
                server.SendJson(11, Json.O("user_ids", new List<object> { friend.ToString() }));
                var kpMine = KeyPackage.Read(new TlsReader(server.ExpectBinary(26)));
                Assert(DaveSession.FromBigEndian(kpMine.LeafNode.Credential.Identity) == me, "key package identity");

                // Before the group exists, audio goes out with transport encryption only.
                conn.SetSpeaking(true);
                var sp = server.Expect(5);
                Assert(sp.Long("speaking") == 1 && sp.Long("ssrc") == 777, "speaking: " + Json.Write(sp));
                var early = Encoding.ASCII.GetBytes("early opus");
                conn.SendOpus(early);
                ushort seq; uint ssrc;
                var got = VoiceConnection.OpenRtp(transport, server.ExpectAudio(), out seq, out ssrc);
                Assert(got != null && ssrc == 777 && Bytes.Equal(got, early), "passthrough audio didn't arrive intact");

                // The friend's key package; the gateway asks us to add them, we commit, it's announced.
                byte[] friendSig, friendSigPub, friendLeaf, friendInit;
                P256.Generate(out friendSig, out friendSigPub);
                var kpFriend = MlsGroup.MakeKeyPackage(Credential.Basic(DaveSession.BigEndian(friend)), friendSig, friendSigPub, out friendLeaf, out friendInit);
                var proposal = new TlsWriter().U8(0);
                proposal.Vector(x => x.Raw(MlsGroup.ExternalProposal(DaveSession.BigEndian(channel), 0, 0, new Proposal { Type = Proposal.Add, KeyPackage = kpFriend }, gatewayPriv[0])));
                server.SendBinary(27, proposal.ToArray());
                var op28 = server.ExpectBinary(28);
                var r28 = new TlsReader(op28);
                int start = r28.Position;
                MlsMessage.Read(r28);
                var commit = r28.Since(start);
                var welcome = Welcome.Read(r28);
                server.SendBinary(29, new TlsWriter().U16(3).Raw(commit).ToArray());
                Assert(server.Expect(23).Long("transition_id") == 3, "not ready for the transition");
                server.SendJson(22, Json.O("transition_id", 3L));

                // The friend joins from our welcome and checks they can hear us.
                var friendGroup = MlsGroup.Join(welcome, kpFriend, friendInit, friendLeaf, friendSig, null);
                var hearMe = new FrameDecryptor(new HashRatchet(friendGroup.Export("Discord Secure Frames v0", BitConverter.GetBytes(me), 16)), DateTime.UtcNow.AddSeconds(-5));
                var secret = Encoding.ASCII.GetBytes("encrypted opus");
                byte[] packet = null;
                for (int i = 0; i < 50 && packet == null; i++)
                {
                    conn.SendOpus(secret);
                    var payload = VoiceConnection.OpenRtp(transport, server.ExpectAudio(), out seq, out ssrc);
                    if (DaveFrame.Parse(payload) != null) packet = payload;   // the transition may take a moment
                }
                Assert(packet != null, "audio never became DAVE-encrypted");
                var heard = hearMe.Decrypt(DaveFrame.Parse(packet), packet, DateTime.UtcNow);
                Assert(heard != null && Bytes.Equal(heard, secret), "friend couldn't decrypt our audio");

                // And we hear them.
                server.SendJson(5, Json.O("user_id", friend.ToString(), "ssrc", 4242L, "speaking", 1L));
                Assert(speaking.WaitOne(5000), "never saw the friend's speaking state");
                var theirs = new FrameEncryptor(new HashRatchet(friendGroup.Export("Discord Secure Frames v0", BitConverter.GetBytes(friend), 16)));
                server.SendUdp(VoiceConnection.SealRtp(transport, 10, 960, 4242, 1, theirs.Encrypt(Encoding.ASCII.GetBytes("hello from 42"))));
                KeyValuePair<ulong, byte[]> heardFriend;
                Assert(received.TryTake(out heardFriend, 5000), "friend's audio never arrived");
                Assert(heardFriend.Key == friend && Encoding.ASCII.GetString(heardFriend.Value) == "hello from 42", "friend's audio came out wrong");
                return "IP discovery, select protocol, passthrough, DAVE join, audio both ways; privacy code " + conn.Dave.PrivacyCode.Substring(0, 11) + "...";
            }
        }
    }
}
