using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    public class ApiException : Exception
    {
        public readonly int Status;
        public readonly JObj Body;
        public ApiException(int status, JObj body, string raw)
            : base(Describe(status, body, raw))
        {
            Status = status;
            Body = body;
        }

        public bool IsCaptcha { get { return Body != null && Body.Has("captcha_sitekey"); } }

        // Discord said the file is too big: HTTP 413, or error 40005 / 50045. The chat window
        // words it for the chat's limit (Uploads.Refused).
        public bool IsTooLarge { get { long code = Body != null ? Body.Long("code") : 0; return Status == 413 || code == 40005 || code == 50045; } }

        static string Describe(int status, JObj body, string raw)
        {
            if (body != null && body.Has("captcha_sitekey"))
                return "Discord asked for a captcha and it wasn't solved, so nothing was sent. Try again to get a new one.";
            long code = body != null ? body.Long("code") : 0;
            if (status == 413 || code == 40005 || code == 50045) return "Discord refused the file as too big, so nothing was sent.";
            string msg = body != null ? body.Str("message") : null;
            if (string.IsNullOrEmpty(msg)) msg = raw != null && raw.Length < 200 ? raw : "request failed";
            return "Discord said: " + msg + " (" + status + ")";
        }
    }

    // Who answers Discord's captchas: AppContext plugs in the captcha window. Gets the challenge
    // Discord sent, returns the solved key, or null if the user closed the window.
    public static class Captcha
    {
        public static Func<JObj, Task<string>> Solve;
    }

    // Identity of this client. Discord expects every request to describe the client it comes
    // from, so Buzzcord reports itself the way the Discord web app on Windows does.
    public static class ClientInfo
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
        const int FallbackBuild = 657850;
        static int build = FallbackBuild;

        public static int BuildNumber { get { return build; } }

        public static JObj Properties()
        {
            return Json.O(
                "os", "Windows",
                "browser", "Chrome",
                "device", "",
                "system_locale", System.Globalization.CultureInfo.CurrentUICulture.Name.Length > 0
                    ? System.Globalization.CultureInfo.CurrentUICulture.Name : "en-US",
                "browser_user_agent", UserAgent,
                "browser_version", "140.0.0.0",
                "os_version", "10",
                "referrer", "",
                "referring_domain", "",
                "referrer_current", "",
                "referring_domain_current", "",
                "release_channel", "stable",
                "client_build_number", build,
                "client_event_source", null);
        }

        public static string SuperProperties()
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(Properties())));
        }

        // The web app's build number changes every few days. It is read from the web app's own
        // files once a day; the last known value is kept in settings.
        public static void LoadBuildNumber()
        {
            int saved;
            if (int.TryParse(Settings.Get("BuildNumber", ""), out saved) && saved > 0) build = saved;
            DateTime checkedAt;
            if (DateTime.TryParse(Settings.Get("BuildNumberChecked", ""), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out checkedAt)
                && DateTime.UtcNow - checkedAt < TimeSpan.FromHours(20)) return;
            try
            {
                using (var http = Api.NewHttp())
                {
                    string html = http.GetStringAsync("https://discord.com/app").Result;
                    foreach (Match m in Regex.Matches(html, "src=\"(/assets/sentry\\.[^\"]+\\.js)\""))
                    {
                        string js = http.GetStringAsync("https://discord.com" + m.Groups[1].Value).Result;
                        var b = Regex.Match(js, "\"buildNumber\",\"(\\d+)\"");
                        if (b.Success)
                        {
                            build = int.Parse(b.Groups[1].Value);
                            Settings.Set("BuildNumber", build.ToString());
                            break;
                        }
                    }
                }
                Settings.Set("BuildNumberChecked", DateTime.UtcNow.ToString("o"));
                Settings.Save();
            }
            catch (Exception ex) { Program.Log("build number: " + ex.Message); }
        }
    }

    public class Api
    {
        public const string Base = "https://discord.com/api/v9";
        readonly HttpClient http;
        readonly string token;

        public Api(string token)
        {
            this.token = token;
            http = NewHttp();
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Super-Properties", ClientInfo.SuperProperties());
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Discord-Locale", "en-US");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Discord-Timezone", TimeZoneId());
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://discord.com");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://discord.com/channels/@me");
        }

        public string Token { get { return token; } }

        public static HttpClient NewHttp()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientInfo.UserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return c;
        }

        static string TimeZoneId()
        {
            // Discord wants an IANA zone name; Windows only knows its own names. Map the common ones.
            string id = TimeZoneInfo.Local.Id;
            switch (id)
            {
                case "Singapore Standard Time": return "Asia/Manila";
                case "Taipei Standard Time": return "Asia/Taipei";
                case "China Standard Time": return "Asia/Shanghai";
                case "Tokyo Standard Time": return "Asia/Tokyo";
                case "AUS Eastern Standard Time": return "Australia/Sydney";
                case "Pacific Standard Time": return "America/Los_Angeles";
                case "Eastern Standard Time": return "America/New_York";
                case "GMT Standard Time": return "Europe/London";
                default: return "Etc/UTC";
            }
        }

        public Task<object> Get(string path) { return Send(HttpMethod.Get, path, null); }
        public Task<object> Post(string path, object body) { return Send(HttpMethod.Post, path, body); }
        public Task<object> Patch(string path, object body) { return Send(new HttpMethod("PATCH"), path, body); }
        public Task<object> Put(string path, object body) { return Send(HttpMethod.Put, path, body); }
        public Task<object> Delete(string path) { return Send(HttpMethod.Delete, path, null); }

        public Task<object> Send(HttpMethod method, string path, object body)
        {
            return Send(method, path, body, null);
        }

        // Friend actions carry X-Context-Properties: where in the app they came from.
        public Task<object> SendWithContext(HttpMethod method, string path, object body, string location)
        {
            string ctx = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(Json.O("location", location))));
            return Send(method, path, body, new Dictionary<string, string> { { "X-Context-Properties", ctx } });
        }

        public async Task<object> Send(HttpMethod method, string path, object body, Dictionary<string, string> headers)
        {
            bool captchaTried = false;
            for (int attempt = 0; ; attempt++)
            {
                var req = new HttpRequestMessage(method, path.StartsWith("http") ? path : Base + path);
                if (headers != null)
                    foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (body != null)
                    req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                else if (method == HttpMethod.Post || method == HttpMethod.Put)
                    req.Content = new StringContent("", Encoding.UTF8, "application/json");
                using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int code = (int)resp.StatusCode;
                    if (code == 429 && attempt < 3)
                    {
                        double wait = 1;
                        try { var j = Json.ParseObj(text); if (j != null && j.Has("retry_after")) wait = Convert.ToDouble(j["retry_after"]); }
                        catch (Exception) { }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Max(wait, 0.5), 30))).ConfigureAwait(false);
                        continue;
                    }
                    if (code >= 200 && code < 300)
                        return string.IsNullOrEmpty(text) ? null : Json.Parse(text);
                    JObj err = null;
                    try { err = Json.ParseObj(text); }
                    catch (Exception) { }
                    if (code == 400 && err != null && err.Has("captcha_sitekey") && !captchaTried && Captcha.Solve != null)
                    {
                        // Show Discord's captcha to the user, then send the same request again
                        // with their answer, exactly as the official app does.
                        captchaTried = true;
                        string answer = await Captcha.Solve(err).ConfigureAwait(false);
                        if (answer != null)
                        {
                            headers = headers != null ? new Dictionary<string, string>(headers) : new Dictionary<string, string>();
                            headers["X-Captcha-Key"] = answer;
                            if (err.Str("captcha_rqtoken") != null) headers["X-Captcha-Rqtoken"] = err.Str("captcha_rqtoken");
                            if (err.Str("captcha_session_id") != null) headers["X-Captcha-Session-Id"] = err.Str("captcha_session_id");
                            continue;
                        }
                    }
                    throw new ApiException(code, err, text);
                }
            }
        }

        public static string Nonce()
        {
            long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            return ((ms - 1420070400000L) << 22).ToString();
        }

        public Task<object> SendMessage(string channelId, string content, string replyTo, List<JObj> attachments)
        {
            var body = Json.O("content", content ?? "", "nonce", Nonce(), "tts", false, "flags", 0);
            if (replyTo != null)
                body["message_reference"] = Json.O("channel_id", channelId, "message_id", replyTo);
            if (attachments != null && attachments.Count > 0)
                body["attachments"] = attachments;
            return Post("/channels/" + channelId + "/messages", body);
        }

        // Discord's upload flow: ask for an upload slot, PUT the bytes there, then send a message
        // that names the uploaded file. progress gets values 0..1.
        public async Task<JObj> UploadFile(string channelId, string path, int index, Action<double> progress)
        {
            var info = new FileInfo(path);
            var slot = await Post("/channels/" + channelId + "/attachments", Json.O("files", new List<object> {
                Json.O("filename", info.Name, "file_size", info.Length, "id", index.ToString(), "is_clip", false)
            })).ConfigureAwait(false) as JObj;
            JObj att = null;
            foreach (var a in slot.Objs("attachments")) { att = a; break; }
            if (att == null) throw new InvalidOperationException("Discord did not give an upload slot.");

            using (var file = new ProgressStream(File.OpenRead(path), info.Length, progress))
            using (var put = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                var content = new StreamContent(file, 81920);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var resp = await put.PutAsync(att.Str("upload_url"), content).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new ApiException((int)resp.StatusCode, null, "Upload failed");
            }
            return Json.O("id", index.ToString(), "filename", info.Name, "uploaded_filename", att.Str("upload_filename"));
        }

        public static async Task Download(string url, string dest)
        {
            using (var c = NewHttp())
            using (var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string tmp = dest + ".part";
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
        }

        static readonly HttpClient shared = NewHttp();

        public static Task<byte[]> GetBytes(string url) { return shared.GetByteArrayAsync(url); }
    }

    class ProgressStream : Stream
    {
        readonly Stream inner;
        readonly long length;
        readonly Action<double> progress;
        long done;

        public ProgressStream(Stream inner, long length, Action<double> progress)
        {
            this.inner = inner;
            this.length = Math.Max(1, length);
            this.progress = progress;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            done += n;
            if (progress != null) progress((double)done / length);
            return n;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return inner.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; done = value; } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
