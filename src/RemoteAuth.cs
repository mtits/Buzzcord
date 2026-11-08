using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // "Log in with QR code": the same flow the Discord web app uses. The phone app scans a QR
    // code holding a fingerprint of a key made here; Discord then sends the account token back
    // encrypted with that key, so it never travels in the clear.
    public class RemoteAuth : IDisposable
    {
        const string Url = "wss://remote-auth-gateway.discord.gg/?v=2";

        public event Action<string> QrReady;                   // URL to put in the QR code
        public event Action<string, string> Scanned;           // username, avatar URL
        public event Action<string> Succeeded;                 // token
        public event Action<string> Failed;                    // message

        readonly RSACng rsa = new RSACng(2048);
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        ClientWebSocket ws;

        public void Start() { Task.Run(() => Run()); }

        public void Dispose()
        {
            try { cts.Cancel(); } catch (Exception) { }
            try { if (ws != null) ws.Abort(); } catch (Exception) { }
        }

        async Task Run()
        {
            bool finished = false;
            try
            {
                ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", "https://discord.com");
                await ws.ConnectAsync(new Uri(Url), cts.Token).ConfigureAwait(false);
                var buffer = new byte[16384];
                var msg = new MemoryStream();
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    msg.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        msg.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;

                    var p = Json.ParseObj(Encoding.UTF8.GetString(msg.ToArray()));
                    switch (p.Str("op"))
                    {
                        case "hello":
                            StartHeartbeat((int)p.Long("heartbeat_interval", 41250));
                            await Send(Json.O("op", "init", "encoded_public_key", Convert.ToBase64String(PublicKeyDer()))).ConfigureAwait(false);
                            break;
                        case "nonce_proof":
                            {
                                var nonce = Decrypt(p.Str("encrypted_nonce"));
                                byte[] hash;
                                using (var sha = SHA256.Create()) hash = sha.ComputeHash(nonce);
                                string proof = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                                await Send(Json.O("op", "nonce_proof", "proof", proof)).ConfigureAwait(false);
                                break;
                            }
                        case "pending_remote_init":
                            if (QrReady != null) QrReady("https://discord.com/ra/" + p.Str("fingerprint"));
                            break;
                        case "pending_ticket":
                            {
                                // "id:discriminator:avatar:username"
                                var parts = Encoding.UTF8.GetString(Decrypt(p.Str("encrypted_user_payload"))).Split(new[] { ':' }, 4);
                                if (parts.Length == 4 && Scanned != null)
                                {
                                    var u = new User { Id = parts[0], Discriminator = parts[1], Avatar = parts[2] == "0" ? null : parts[2], Username = parts[3] };
                                    Scanned(u.Username, u.AvatarUrl(128));
                                }
                                break;
                            }
                        case "pending_login":
                            {
                                string token = await ExchangeTicket(p.Str("ticket")).ConfigureAwait(false);
                                finished = true;
                                if (Succeeded != null) Succeeded(token);
                                return;
                            }
                        case "finish": // older protocol variant
                            finished = true;
                            if (Succeeded != null) Succeeded(Encoding.UTF8.GetString(Decrypt(p.Str("encrypted_token"))));
                            return;
                        case "cancel":
                            finished = true;
                            if (Failed != null) Failed("Login was cancelled on the phone.");
                            return;
                    }
                }
                if (!finished && !cts.IsCancellationRequested && Failed != null)
                    Failed("The QR code expired. Click \"New code\" to get another.");
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested && !finished && Failed != null) Failed(ex.Message);
            }
            finally
            {
                try { ws.Abort(); } catch (Exception) { }
            }
        }

        async Task<string> ExchangeTicket(string ticket)
        {
            using (var http = Api.NewHttp())
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Super-Properties", ClientInfo.SuperProperties());
                http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://discord.com");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://discord.com/login");
                var body = new System.Net.Http.StringContent(Json.Write(Json.O("ticket", ticket)), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(Api.Base + "/users/@me/remote-auth/login", body).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObj j = null;
                try { j = Json.ParseObj(text); } catch (Exception) { }
                if (!resp.IsSuccessStatusCode || j == null || j.Str("encrypted_token") == null)
                    throw new ApiException((int)resp.StatusCode, j, text);
                return Encoding.UTF8.GetString(Decrypt(j.Str("encrypted_token")));
            }
        }

        void StartHeartbeat(int interval)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                        await Send(Json.O("op", "heartbeat")).ConfigureAwait(false);
                    }
                }
                catch (Exception) { }
            });
        }

        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        async Task Send(JObj o)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(o));
            await sendLock.WaitAsync().ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }

        byte[] Decrypt(string b64)
        {
            return rsa.Decrypt(Convert.FromBase64String(b64), RSAEncryptionPadding.OaepSHA256);
        }

        // SubjectPublicKeyInfo DER, which .NET Framework can't export by itself.
        byte[] PublicKeyDer()
        {
            var p = rsa.ExportParameters(false);
            byte[] rsaKey = Der(0x30, Concat(DerInt(p.Modulus), DerInt(p.Exponent)));
            byte[] algo = Der(0x30, Concat(
                new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 }, // rsaEncryption
                new byte[] { 0x05, 0x00 }));
            byte[] bits = Der(0x03, Concat(new byte[] { 0 }, rsaKey));
            return Der(0x30, Concat(algo, bits));
        }

        static byte[] DerInt(byte[] value)
        {
            int start = 0;
            while (start < value.Length - 1 && value[start] == 0) start++;
            bool pad = (value[start] & 0x80) != 0;
            var v = new byte[value.Length - start + (pad ? 1 : 0)];
            Array.Copy(value, start, v, pad ? 1 : 0, value.Length - start);
            return Der(0x02, v);
        }

        static byte[] Der(byte tag, byte[] content)
        {
            var ms = new MemoryStream();
            ms.WriteByte(tag);
            int len = content.Length;
            if (len < 0x80) ms.WriteByte((byte)len);
            else if (len < 0x100) { ms.WriteByte(0x81); ms.WriteByte((byte)len); }
            else { ms.WriteByte(0x82); ms.WriteByte((byte)(len >> 8)); ms.WriteByte((byte)len); }
            ms.Write(content, 0, content.Length);
            return ms.ToArray();
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
