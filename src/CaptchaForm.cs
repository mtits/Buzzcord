using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Buzzcord
{
    // Shows Discord's hCaptcha so the user can solve it. The chat view's IE engine is too old for
    // hCaptcha, so this window uses Edge WebView2 (part of Windows 11), only while it's open.
    // The page is served as https://discord.com/... from inside the app: hCaptcha only works on
    // the site its key belongs to, and nothing is actually fetched from that address.
    public class CaptchaForm : Form
    {
        const string PageUrl = "https://discord.com/buzzcord-captcha";
        static readonly SemaphoreSlim one = new SemaphoreSlim(1, 1);
        static bool loaderSet;

        readonly JObj challenge;
        readonly TaskCompletionSource<string> result = new TaskCompletionSource<string>();
        readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill };
        readonly Label note = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0) };

        // One captcha at a time; called from any thread.
        public static async Task<string> Ask(JObj challenge, SynchronizationContext ui)
        {
            await one.WaitAsync().ConfigureAwait(false);
            try
            {
                var tcs = new TaskCompletionSource<string>();
                ui.Post(_ =>
                {
                    try
                    {
                        var f = new CaptchaForm(challenge);
                        f.result.Task.ContinueWith(t => tcs.TrySetResult(t.Result));
                        f.Show();
                        f.Activate();
                    }
                    catch (Exception ex)
                    {
                        Program.Log("captcha window: " + ex);
                        MessageBox.Show("Discord asked for a captcha, but the captcha window couldn't open:\n" + ex.Message, "Buzzcord");
                        tcs.TrySetResult(null);
                    }
                }, null);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally { one.Release(); }
        }

        // The solved key, or null if the window was closed first.
        public Task<string> Result { get { return result.Task; } }

        public CaptchaForm(JObj challenge)
        {
            this.challenge = challenge;
            Text = "Buzzcord - Quick check";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            ClientSize = new Size(420, 640);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            note.Text = "Discord wants to make sure you're a person before it sends this. Solve the puzzle below; Buzzcord sends your request as soon as you're done.";

            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Icons.PurpleLight };
            border.Controls.Add(view);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
            host.Controls.Add(border);
            Controls.Add(host);
            Controls.Add(note);

            Shown += async (o, e) => await Start();
            FormClosed += (o, e) => result.TrySetResult(null);
            Theme.Attach(this, null);
        }

        async Task Start()
        {
            try
            {
                if (!loaderSet)
                {
                    // WebView2Loader.dll ships next to Buzzcord.exe in bin\.
                    CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(Shortcuts.Home, "bin"));
                    loaderSet = true;
                }
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.AddWebResourceRequestedFilter(PageUrl + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, e) =>
                {
                    var bytes = Encoding.UTF8.GetBytes(Page());
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                core.WebMessageReceived += (s, e) =>
                {
                    string msg = e.TryGetWebMessageAsString();
                    if (string.IsNullOrEmpty(msg)) return;
                    if (msg.StartsWith("error:")) { note.Text = "The captcha hit a problem (" + msg.Substring(6) + "). Close this window and try again."; return; }
                    result.TrySetResult(msg);
                    Close();
                };
                core.NewWindowRequested += (s, e) =>
                {
                    // hCaptcha's privacy/help links: open in the normal browser.
                    e.Handled = true;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch (Exception) { }
                };
                core.Navigate(PageUrl);
            }
            catch (Exception ex)
            {
                Program.Log("captcha: " + ex);
                note.Text = "Couldn't start the captcha (Edge WebView2): " + ex.Message;
            }
        }

        string Page()
        {
            if ((challenge.Str("captcha_service") ?? "hcaptcha") != "hcaptcha")
                return "<p style='font:13px Tahoma;padding:16px'>Discord asked for a kind of captcha Buzzcord doesn't support (" + MessageHtml.E(challenge.Str("captcha_service")) + ").</p>";
            Func<string, string> js = v => Json.Write(v).Replace("</", "<\\/");
            return @"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width"">
<style>html,body{margin:0;height:100%;background:" + (Theme.IsDark ? "#17141f" : "#fff") + @";font:13px Tahoma,Arial,sans-serif;}
body{display:flex;align-items:center;justify-content:center;}</style>
<script>
function send(m){window.chrome.webview.postMessage(m);}
function go(){
  var id=hcaptcha.render('box',{sitekey:" + js(challenge.Str("captcha_sitekey")) + @",
    callback:function(t){send(t);},
    'error-callback':function(e){send('error:'+e);},
    'expired-callback':function(){hcaptcha.reset(id);}, theme:" + (Theme.IsDark ? "'dark'" : "'light'") + @"});
  var rq=" + js(challenge.Str("captcha_rqdata")) + @";
  if(rq) hcaptcha.setData(id,{rqdata:rq});
}
</script>
<script src=""https://js.hcaptcha.com/1/api.js?render=explicit&onload=go&recaptchacompat=off"" async defer></script>
</head><body><div id=""box""></div></body></html>";
        }
    }
}
