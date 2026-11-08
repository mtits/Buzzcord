using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Buzzcord
{
    // "Playing X" without rich presence: compares running process names with Discord's public
    // list of detectable games. Only process names are read; no game process is ever opened.
    public class GameDetector
    {
        public class Game
        {
            public string AppId, Name;
            public long StartMs;
            public override string ToString() { return Name; }
        }

        static readonly string CachePath = Path.Combine(Settings.Folder, "detectable.tsv");
        // Names too generic to trust without the full path (which would mean opening the process).
        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "game.exe", "launcher.exe", "start.exe", "play.exe", "client.exe", "main.exe", "app.exe", "run.exe",
            "setup.exe", "update.exe", "updater.exe", "game_x64.exe", "win64.exe", "player.exe", "bin.exe",
            "unitycrashhandler64.exe", "unitycrashhandler32.exe", "crashreporter.exe", "javaw.exe", "java.exe",
            "python.exe", "pythonw.exe", "node.exe", "electron.exe", "nw.exe", "dosbox.exe", "retroarch.exe" };

        Dictionary<string, KeyValuePair<string, string>> byExe = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Game> running = new Dictionary<string, Game>();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15000 };

        public event Action Changed;
        public bool Enabled = true;

        public IEnumerable<Game> Running { get { return running.Values; } }

        public string NameOf(string appId)
        {
            foreach (var kv in byExe.Values) if (kv.Key == appId) return kv.Value;
            return null;
        }

        // Games whose name contains the text, names starting with it first.
        public List<Game> Search(string text, int max)
        {
            var result = new List<Game>();
            text = (text ?? "").Trim();
            if (text.Length == 0) return result;
            var seen = new HashSet<string>();
            foreach (var kv in byExe.Values)
            {
                if (kv.Value.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) < 0 || !seen.Add(kv.Key)) continue;
                result.Add(new Game { AppId = kv.Key, Name = kv.Value });
            }
            result.Sort((a, b) =>
            {
                bool sa = a.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase), sb = b.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase);
                if (sa != sb) return sa ? -1 : 1;
                int byLength = a.Name.Length.CompareTo(b.Name.Length);
                return byLength != 0 ? byLength : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        public void Start()
        {
            LoadCache();
            timer.Tick += (s, e) => Scan();
            timer.Start();
            bool stale = !File.Exists(CachePath) || DateTime.Now - File.GetLastWriteTime(CachePath) > TimeSpan.FromDays(7);
            if (stale) Task.Run(() => Refresh()).ContinueWith(t => { LoadCache(); Scan(); }, TaskScheduler.FromCurrentSynchronizationContext());
            Scan();
        }

        void LoadCache()
        {
            var map = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(CachePath))
                    foreach (var line in File.ReadAllLines(CachePath, Encoding.UTF8))
                    {
                        var p = line.Split('\t');
                        if (p.Length == 3) map[p[0]] = new KeyValuePair<string, string>(p[1], p[2]);
                    }
            }
            catch (Exception ex) { Program.Log("detectable cache: " + ex.Message); }
            byExe = map;
        }

        // Downloads Discord's detectable list (~13 MB) and keeps only exe name -> app, so the
        // big download never stays in memory.
        static void Refresh()
        {
            try
            {
                string text;
                using (var http = Api.NewHttp()) text = http.GetStringAsync(Api.Base + "/applications/detectable").Result;
                var list = Json.Parse(text) as List<object>;
                text = null;
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var names = new Dictionary<string, string>();
                var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in list)
                {
                    var app = o as JObj;
                    if (app == null) continue;
                    string id = app.Str("id"), name = app.Str("name");
                    if (id == null || name == null) continue;
                    foreach (var exe in app.Objs("executables"))
                    {
                        if (exe.Str("os") != "win32" || exe.Bool("is_launcher")) continue;
                        string n = (exe.Str("name") ?? "").TrimStart('>').Replace('\\', '/');
                        int slash = n.LastIndexOf('/');
                        if (slash >= 0) n = n.Substring(slash + 1);
                        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Generic.Contains(n)) continue;
                        string prev;
                        if (seen.TryGetValue(n, out prev) && prev != id) ambiguous.Add(n);
                        seen[n] = id;
                        names[id] = name.Replace('\t', ' ').Replace('\n', ' ');
                    }
                }
                list = null;
                var sb = new StringBuilder();
                foreach (var kv in seen)
                    if (!ambiguous.Contains(kv.Key))
                        sb.Append(kv.Key.ToLowerInvariant()).Append('\t').Append(kv.Value).Append('\t').Append(names[kv.Value]).Append('\n');
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(CachePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Program.Log("detectable refresh: " + ex.Message); }
            GC.Collect();
        }

        public void Scan()
        {
            var now = new Dictionary<string, KeyValuePair<string, string>>();
            if (Enabled && byExe.Count > 0)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        KeyValuePair<string, string> app;
                        if (byExe.TryGetValue(p.ProcessName + ".exe", out app)) now[app.Key] = app;
                    }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
            }
            bool changed = false;
            foreach (var id in new List<string>(running.Keys))
                if (!now.ContainsKey(id)) { running.Remove(id); changed = true; }
            foreach (var kv in now)
                if (!running.ContainsKey(kv.Key))
                {
                    running[kv.Key] = new Game { AppId = kv.Key, Name = kv.Value.Value, StartMs = PresenceManager.NowMs() };
                    changed = true;
                }
            if (changed && Changed != null) Changed();
        }
    }
}
