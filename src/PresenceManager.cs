using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // Decides what "I'm doing" looks like for every account: rich presence sent by games over the
    // pipe, else games found running. Changes are pushed to all signed-in accounts.
    public class PresenceManager
    {
        public static PresenceManager Instance;

        class RpcEntry { public string AppId; public JObj Activity; public int Order; }

        public readonly List<Session> Sessions = new List<Session>();
        public readonly GameDetector Detector = new GameDetector();
        readonly Dictionary<int, RpcEntry> rpc = new Dictionary<int, RpcEntry>();
        readonly Dictionary<string, string> appNames = new Dictionary<string, string>();
        readonly Dictionary<string, Dictionary<string, string>> assetIds = new Dictionary<string, Dictionary<string, string>>();
        readonly Dictionary<string, string> externalAssets = new Dictionary<string, string>();
        readonly SynchronizationContext ui;
        int order;

        public event Action Changed;   // UI thread

        public PresenceManager()
        {
            ui = SynchronizationContext.Current;
            Detector.Changed += Push;
        }

        public static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }

        public bool Shares(Session s) { return Settings.GetBool("ShareActivity." + s.TokenKey, true); }

        public void SetShares(Session s, bool on)
        {
            Settings.SetBool("ShareActivity." + s.TokenKey, on);
            Settings.Save();
            s.PushPresence();
        }

        // The activities to send for one account, newest rich presence first.
        public List<JObj> ActivitiesFor(Session s)
        {
            var result = new List<JObj>();
            if (s != null && !Shares(s)) return result;
            var apps = new HashSet<string>();
            var entries = new List<RpcEntry>(rpc.Values);
            entries.Sort((a, b) => b.Order.CompareTo(a.Order));
            foreach (var e in entries)
            {
                if (e.Activity == null || apps.Contains(e.AppId)) continue;
                apps.Add(e.AppId);
                result.Add(e.Activity);
            }
            foreach (var g in Detector.Running)
            {
                if (apps.Contains(g.AppId)) continue;
                apps.Add(g.AppId);
                result.Add(Json.O("name", g.Name, "type", 0, "application_id", g.AppId, "timestamps", Json.O("start", g.StartMs)));
            }
            return result;
        }

        public JObj RpcUser()
        {
            User me = null;
            Run(() => { if (Sessions.Count > 0) me = Sessions[0].Me; });
            if (me == null) me = new User { Id = "0", Username = "buzzcord", Discriminator = "0" };
            return Json.O("id", me.Id, "username", me.Username, "discriminator", me.Discriminator ?? "0",
                "global_name", me.GlobalName, "avatar", me.Avatar, "bot", false, "flags", 0, "premium_type", 0);
        }

        // Called from pipe threads.
        public void SetRpc(int connId, string appId, JObj activity, long pid)
        {
            if (activity == null)
            {
                Post(() => { rpc.Remove(connId); Push(); });
                return;
            }
            Task.Run(async () =>
            {
                JObj converted;
                try { converted = await Convert(appId, activity).ConfigureAwait(false); }
                catch (Exception ex) { Program.Log("rpc convert: " + ex.Message); converted = null; }
                if (converted == null) return;
                Post(() =>
                {
                    rpc[connId] = new RpcEntry { AppId = appId, Activity = converted, Order = ++order };
                    Push();
                });
            });
        }

        public void ClearRpc(int connId)
        {
            Post(() => { if (rpc.Remove(connId)) Push(); });
        }

        void Push()
        {
            foreach (var s in Sessions) if (s.IsReady) s.PushPresence();
            if (Changed != null) Changed();
        }

        void Post(Action a)
        {
            if (ui == null) a();
            else ui.Post(_ => a(), null);
        }

        void Run(Action a)
        {
            if (ui == null || SynchronizationContext.Current == ui) { a(); return; }
            ui.Send(_ => a(), null);
        }

        Api AnyApi()
        {
            Api api = null;
            Run(() => { foreach (var s in Sessions) if (s.IsReady) { api = s.Api; break; } });
            return api;
        }

        // Turns what a game sends over the pipe into what the gateway expects: app name filled in,
        // timestamps in milliseconds, image keys and URLs turned into Discord asset ids.
        async Task<JObj> Convert(string appId, JObj a)
        {
            var api = AnyApi();
            var outA = new JObj();
            outA["application_id"] = appId;
            outA["type"] = a.Has("type") ? a.Long("type") : 0L;
            outA["name"] = await AppName(appId).ConfigureAwait(false);
            foreach (var key in new[] { "details", "state", "instance", "flags", "status_display_type", "details_url", "state_url" })
                if (a.Has(key) && a[key] != null) outA[key] = a[key];

            var ts = a.Obj("timestamps");
            if (ts != null)
            {
                var t = new JObj();
                foreach (var k in new[] { "start", "end" })
                    if (ts.Has(k) && ts[k] != null)
                    {
                        long v = ts.Long(k);
                        t[k] = v < 100000000000L ? v * 1000 : v;   // seconds -> ms
                    }
                outA["timestamps"] = t;
            }

            var party = a.Obj("party");
            if (party != null) outA["party"] = party;

            var assets = a.Obj("assets");
            if (assets != null)
            {
                var o = new JObj();
                foreach (var k in new[] { "large_text", "small_text" })
                    if (assets.Str(k) != null) o[k] = assets.Str(k);
                foreach (var k in new[] { "large_image", "small_image" })
                {
                    string v = assets.Str(k);
                    if (v == null) continue;
                    o[k] = api != null ? await ResolveAsset(api, appId, v).ConfigureAwait(false) : v;
                }
                outA["assets"] = o;
            }

            var buttons = a.Arr("buttons");
            if (buttons != null && buttons.Count > 0)
            {
                var labels = new List<object>();
                var urls = new List<object>();
                foreach (var b in buttons)
                {
                    var bj = b as JObj;
                    if (bj == null) continue;
                    labels.Add(bj.Str("label"));
                    urls.Add(bj.Str("url"));
                }
                outA["buttons"] = labels;
                outA["metadata"] = Json.O("button_urls", urls);
            }
            return outA;
        }

        async Task<string> AppName(string appId)
        {
            string name;
            lock (appNames) if (appNames.TryGetValue(appId, out name)) return name;
            name = Detector.NameOf(appId);
            if (name == null)
            {
                try
                {
                    using (var http = Api.NewHttp())
                    {
                        var j = Json.ParseObj(await http.GetStringAsync(Api.Base + "/applications/" + appId + "/rpc").ConfigureAwait(false));
                        name = j.Str("name");
                    }
                }
                catch (Exception ex) { Program.Log("app name " + appId + ": " + ex.Message); }
            }
            if (string.IsNullOrEmpty(name)) name = "a game";
            lock (appNames) appNames[appId] = name;
            return name;
        }

        async Task<string> ResolveAsset(Api api, string appId, string value)
        {
            if (value.StartsWith("mp:") || value.StartsWith("spotify:") || value.StartsWith("twitch:")) return value;
            if (value.StartsWith("http://") || value.StartsWith("https://"))
            {
                string cached;
                lock (externalAssets) if (externalAssets.TryGetValue(value, out cached)) return cached;
                try
                {
                    var r = await api.Post("/applications/" + appId + "/external-assets", Json.O("urls", new List<object> { value })).ConfigureAwait(false) as List<object>;
                    var first = r != null && r.Count > 0 ? r[0] as JObj : null;
                    if (first != null && first.Str("external_asset_path") != null)
                    {
                        string mp = "mp:" + first.Str("external_asset_path");
                        lock (externalAssets) externalAssets[value] = mp;
                        return mp;
                    }
                }
                catch (Exception ex) { Program.Log("external asset: " + ex.Message); }
                return value;
            }
            Dictionary<string, string> map;
            lock (assetIds) assetIds.TryGetValue(appId, out map);
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var list = await api.Get("/oauth2/applications/" + appId + "/assets").ConfigureAwait(false) as List<object>;
                    if (list != null)
                        foreach (var o in list)
                        {
                            var j = o as JObj;
                            if (j != null && j.Str("name") != null) map[j.Str("name")] = j.Str("id");
                        }
                }
                catch (Exception ex) { Program.Log("assets " + appId + ": " + ex.Message); }
                lock (assetIds) assetIds[appId] = map;
            }
            string id;
            return map.TryGetValue(value, out id) ? id : value;
        }

        // Image URL for an activity asset, for showing friends' rich presence.
        public static string AssetUrl(string appId, string asset)
        {
            if (string.IsNullOrEmpty(asset)) return null;
            if (asset.StartsWith("mp:")) return "https://media.discordapp.net/" + asset.Substring(3) + (asset.Contains("?") ? "&" : "?") + "format=png";
            if (asset.StartsWith("spotify:")) return "https://i.scdn.co/image/" + asset.Substring(8);
            if (asset.StartsWith("http")) return asset;
            if (appId == null) return null;
            return "https://cdn.discordapp.com/app-assets/" + appId + "/" + asset + ".png";
        }

        static readonly Dictionary<string, string> appIcons = new Dictionary<string, string>();

        // The game's own icon, for activities that have no images of their own.
        public static async Task<string> AppIconUrl(string appId)
        {
            if (appId == null) return null;
            string url;
            lock (appIcons) if (appIcons.TryGetValue(appId, out url)) return url;
            try
            {
                using (var http = Api.NewHttp())
                {
                    var j = Json.ParseObj(await http.GetStringAsync(Api.Base + "/applications/" + appId + "/rpc").ConfigureAwait(false));
                    url = j.Str("icon") != null ? "https://cdn.discordapp.com/app-icons/" + appId + "/" + j.Str("icon") + ".png?size=128" : null;
                }
            }
            catch (Exception) { url = null; }
            lock (appIcons) appIcons[appId] = url;
            return url;
        }
    }
}
