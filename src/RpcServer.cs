using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // Stands in for the Discord app on the local pipe games use for rich presence
    // (\\.\pipe\discord-ipc-N). Games connect here instead and their activity goes to every
    // signed-in account. Nothing here touches the game process itself.
    public class RpcServer
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WaitNamedPipe(string name, int timeout);

        public string PipeName { get; private set; }
        volatile bool stopped;
        int nextId;

        public event Action<string> Log;

        public bool Start()
        {
            for (int i = 0; i < 10; i++)
            {
                string name = "discord-ipc-" + i;
                if (PipeExists(name)) continue;
                PipeName = name;
                Task.Run(() => AcceptLoop(name));
                return true;
            }
            return false;
        }

        // Starts listening on discord-ipc-0 too once nothing else holds it.
        public void TryClaimPrimary()
        {
            const string primary = "discord-ipc-0";
            if (PipeName == primary || PipeExists(primary)) return;
            PipeName = primary;
            if (Log != null) Log("Now receiving rich presence on " + primary);
            Task.Run(() => AcceptLoop(primary));
        }

        public void Stop() { stopped = true; }

        static bool PipeExists(string name)
        {
            if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
            return Marshal.GetLastWin32Error() != 2; // ERROR_FILE_NOT_FOUND means free
        }

        async Task AcceptLoop(string name)
        {
            while (!stopped)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(name,PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null).ConfigureAwait(false);
                    var p = pipe;
                    int id = Interlocked.Increment(ref nextId);
                    var ignored = Task.Run(() => Serve(p, id));
                }
                catch (Exception ex)
                {
                    if (pipe != null) pipe.Dispose();
                    Program.Log("rpc accept: " + ex.Message);
                    Thread.Sleep(1000); // C# 5 can't await inside catch
                }
            }
        }

        async Task Serve(NamedPipeServerStream pipe, int connId)
        {
            string clientId = null;
            try
            {
                using (pipe)
                {
                    var header = new byte[8];
                    while (pipe.IsConnected && !stopped)
                    {
                        if (!await ReadExactly(pipe, header, 8).ConfigureAwait(false)) break;
                        int op = BitConverter.ToInt32(header, 0);
                        int len = BitConverter.ToInt32(header, 4);
                        if (len < 0 || len > 1024 * 1024) break;
                        var body = new byte[len];
                        if (!await ReadExactly(pipe, body, len).ConfigureAwait(false)) break;
                        JObj msg = null;
                        try { msg = Json.ParseObj(Encoding.UTF8.GetString(body)); } catch (Exception) { }

                        if (op == 0) // handshake
                        {
                            clientId = msg != null ? msg.Str("client_id") : null;
                            if (Log != null) Log("Game connected (app " + clientId + ")");
                            await Write(pipe, 1, Json.O("cmd", "DISPATCH", "evt", "READY", "nonce", null, "data", Json.O(
                                "v", 1,
                                "config", Json.O("cdn_host", "cdn.discordapp.com", "api_endpoint", "//discord.com/api", "environment", "production"),
                                "user", PresenceManager.Instance.RpcUser()))).ConfigureAwait(false);
                        }
                        else if (op == 1 && msg != null)
                        {
                            await HandleCommand(pipe, connId, clientId, msg).ConfigureAwait(false);
                        }
                        else if (op == 3)
                        {
                            await WriteRaw(pipe, 4, body).ConfigureAwait(false);
                        }
                        else if (op == 2) break;
                    }
                }
            }
            catch (Exception ex) { Program.Log("rpc: " + ex.Message); }
            finally
            {
                PresenceManager.Instance.ClearRpc(connId);
                if (Log != null && clientId != null) Log("Game disconnected (app " + clientId + ")");
            }
        }

        async Task HandleCommand(Stream pipe, int connId, string clientId, JObj msg)
        {
            string cmd = msg.Str("cmd");
            object nonce = msg.ContainsKey("nonce") ? msg["nonce"] : null;
            var args = msg.Obj("args") ?? new JObj();
            switch (cmd)
            {
                case "SET_ACTIVITY":
                    {
                        var activity = args.Obj("activity");
                        PresenceManager.Instance.SetRpc(connId, clientId, activity, args.Long("pid"));
                        await Write(pipe, 1, Json.O("cmd", cmd, "evt", null, "nonce", nonce, "data", activity)).ConfigureAwait(false);
                        break;
                    }
                case "SUBSCRIBE":
                case "UNSUBSCRIBE":
                    await Write(pipe, 1, Json.O("cmd", cmd, "evt", null, "nonce", nonce, "data", Json.O("evt", msg.Str("evt")))).ConfigureAwait(false);
                    break;
                default:
                    await Write(pipe, 1, Json.O("cmd", cmd, "evt", "ERROR", "nonce", nonce,
                        "data", Json.O("code", 1000, "message", "Buzzcord only supports rich presence"))).ConfigureAwait(false);
                    break;
            }
        }

        static Task Write(Stream s, int op, JObj payload)
        {
            return WriteRaw(s, op, Encoding.UTF8.GetBytes(Json.Write(payload)));
        }

        static async Task WriteRaw(Stream s, int op, byte[] body)
        {
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            await s.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }

        static async Task<bool> ReadExactly(Stream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = await s.ReadAsync(buf, got, len - got).ConfigureAwait(false);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}
