using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Buzzcord
{
    // Saves every file sent in DMs (and group chats) to Documents\Buzzcord Saved\<account>\<chat>.
    // An index of saved attachment ids makes it skip files it already has.
    public static class AutoSaver
    {
        static readonly string IndexPath = Path.Combine(Settings.Folder, "saved.idx");
        static readonly HashSet<string> saved = new HashSet<string>();
        static readonly BlockingCollection<Job> queue = new BlockingCollection<Job>();
        static bool started;

        class Job { public string Id, Url, Path; }

        public static bool SaveDms { get { return Settings.GetBool("AutoSaveDMs", true); } }
        public static bool SaveGroups { get { return Settings.GetBool("AutoSaveGroups", true); } }

        public static event Action<string> FileSaved; // path, on a worker thread

        static void EnsureStarted()
        {
            if (started) return;
            started = true;
            try
            {
                if (File.Exists(IndexPath))
                    foreach (var line in File.ReadAllLines(IndexPath)) if (line.Length > 0) saved.Add(line.Trim());
            }
            catch (Exception) { }
            var t = new Thread(Worker) { IsBackground = true, Name = "AutoSaver" };
            t.Start();
        }

        public static string FolderFor(Session s, Channel c)
        {
            string account = Clean(s.Me != null ? s.Me.Username : "account");
            string chat = Clean(c.IsDm ? s.ChannelTitle(c) : (s.ChannelTitle(c) + (c.IsGroup ? " (group)" : "")));
            return Path.Combine(Settings.SaveFolder, account, chat);
        }

        // Call for any message seen, live or from history. UI thread.
        public static void Consider(Session s, JObj m)
        {
            var atts = m.Arr("attachments");
            if (atts == null || atts.Count == 0) return;
            var c = s.FindChannel(m.Str("channel_id"));
            if (c == null || !(c.IsDm && SaveDms || c.IsGroup && SaveGroups)) return;
            EnsureStarted();
            string folder = FolderFor(s, c);
            DateTime when = Snowflake.Time(m.Str("id"));
            foreach (var o in atts)
            {
                var a = o as JObj;
                if (a == null || a.Str("id") == null || a.Str("url") == null) continue;
                lock (saved) if (saved.Contains(a.Str("id"))) continue;
                string name = when.ToString("yyyy-MM-dd HH.mm.ss") + " " + Clean(a.Str("filename") ?? "file");
                queue.Add(new Job { Id = a.Str("id"), Url = a.Str("url"), Path = Path.Combine(folder, name) });
            }
        }

        static void Worker()
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                lock (saved) if (saved.Contains(job.Id)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.Path));
                    string path = job.Path;
                    for (int n = 2; File.Exists(path); n++)
                        path = Path.Combine(Path.GetDirectoryName(job.Path), Path.GetFileNameWithoutExtension(job.Path) + " (" + n + ")" + Path.GetExtension(job.Path));
                    Api.Download(job.Url, path).Wait();
                    lock (saved)
                    {
                        saved.Add(job.Id);
                        File.AppendAllText(IndexPath, job.Id + "\n");
                    }
                    if (FileSaved != null) FileSaved(path);
                }
                catch (Exception ex) { Program.Log("autosave " + job.Url + ": " + (ex.InnerException ?? ex).Message); }
            }
        }

        public static string Clean(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name ?? "")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 80) r = r.Substring(0, 80);
            return r.Length == 0 ? "_" : r;
        }
    }
}
