using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Updates from GitHub Releases. The newest release that has a Windows zip is downloaded in the background,
    // checked against the SHA-256 file published beside it, and unpacked into a staging folder.
    // Installing swaps the files in place: Windows lets a running exe be renamed, so each old file
    // becomes "<name>.old", the new one is copied in, and the restarted Buzzcord deletes the .old
    // files. Anything that fails part-way is rolled back.
    public static class Updater
    {
        public const string Repo = "mtits/Buzzcord";
        public static string ReleasesUrl { get { return "https://github.com/" + Repo + "/releases"; } }

        // The list, not /releases/latest: a release can be for one platform only (v1.3.2 was Mac
        // only), and GitHub's "latest" would then hide the newest Windows release.
        public const string ListApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";

        public class Release
        {
            public Version Version;
            public string Tag, Notes, PageUrl, ZipUrl, ShaUrl;
        }

        public static Version Current { get { return typeof(Updater).Assembly.GetName().Version; } }

        // "v1.3.0" -> 1.3.0.0. Null for tags that aren't versions.
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int dash = t.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) return null;   // pre-releases are skipped
            Version v;
            if (!Version.TryParse(t, out v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        // Reads one release from GET /repos/{repo}/releases. Null for drafts, pre-releases and odd
        // tags. ZipUrl and ShaUrl are only set for Buzzcord-<version>-windows.zip and its .sha256.
        public static Release ParseRelease(JObj j)
        {
            if (j == null || j.Bool("draft") || j.Bool("prerelease")) return null;
            var r = new Release { Tag = j.Str("tag_name"), Notes = j.Str("body"), PageUrl = j.Str("html_url") };
            r.Version = ParseTag(r.Tag);
            if (r.Version == null) return null;
            string zipName = "Buzzcord-" + r.Tag.Trim().TrimStart('v', 'V') + "-windows.zip";
            foreach (var a in j.Objs("assets"))
            {
                string name = a.Str("name") ?? "";
                string url = a.Str("browser_download_url");
                if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase)) r.ZipUrl = url;
                else if (string.Equals(name, zipName + ".sha256", StringComparison.OrdinalIgnoreCase)) r.ShaUrl = url;
            }
            return r;
        }

        // The highest-version release with a Windows zip and its checksum, from the release list
        // (or a single release object). GitHub lists by date, not version, so every entry is compared.
        public static Release NewestForWindows(object feed)
        {
            var list = feed as List<object>;
            if (list == null && feed is JObj) list = new List<object> { feed };
            Release best = null;
            if (list == null) return null;
            foreach (var o in list)
            {
                var r = ParseRelease(o as JObj);
                if (r == null || r.ZipUrl == null || r.ShaUrl == null) continue;
                if (best == null || r.Version > best.Version) best = r;
            }
            return best;
        }

        static HttpClient Http()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Buzzcord/" + Current + " (+https://github.com/" + Repo + ")");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return c;
        }

        // The newest release with a Windows zip if it's newer than this copy, else null.
        public static async Task<Release> Check()
        {
            using (var http = Http())
            {
                string json = await http.GetStringAsync(ListApi).ConfigureAwait(false);
                var r = NewestForWindows(Json.Parse(json));
                return r != null && r.Version > Current ? r : null;
            }
        }

        static string Root { get { return Path.Combine(Settings.Folder, "update"); } }

        // Downloads, verifies and unpacks the release; returns the folder holding the new files.
        public static async Task<string> Download(Release r)
        {
            if (r.ShaUrl == null) throw new InvalidOperationException("The release has no checksum file, so it can't be verified.");
            byte[] zip, sha;
            using (var http = Http())
            {
                zip = await http.GetByteArrayAsync(r.ZipUrl).ConfigureAwait(false);
                sha = await http.GetByteArrayAsync(r.ShaUrl).ConfigureAwait(false);
            }
            string expected = System.Text.Encoding.ASCII.GetString(sha).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            if (expected.Length != 64 || Hash(zip) != expected)
                throw new InvalidOperationException("The download didn't match its published checksum, so it was thrown away.");
            string dir = Path.Combine(Root, r.Version.ToString());
            Unpack(zip, dir);
            if (!File.Exists(Path.Combine(dir, "Buzzcord.exe"))) throw new InvalidOperationException("The download has no Buzzcord.exe in it.");
            Settings.Set("UpdateStaged", dir);
            Settings.Set("UpdateStagedVersion", r.Version.ToString());
            Settings.Save();
            return dir;
        }

        public static string Hash(byte[] data)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        // Unzips into dir (replacing it), refusing entries that would land outside it.
        public static void Unpack(byte[] zip, string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;   // a folder entry
                    string dest = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The download has a bad file path: " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var src = e.Open())
                    using (var dst = File.Create(dest)) src.CopyTo(dst);
                }
        }

        // A downloaded update waiting to be installed, newer than this copy; else null.
        public static string Staged
        {
            get
            {
                string dir = Settings.Get("UpdateStaged", null);
                Version v;
                if (string.IsNullOrEmpty(dir) || !Version.TryParse(Settings.Get("UpdateStagedVersion", ""), out v)) return null;
                if (v <= Current || !File.Exists(Path.Combine(dir, "Buzzcord.exe"))) return null;
                return dir;
            }
        }

        public static string StagedVersion { get { return Settings.Get("UpdateStagedVersion", null); } }

        // Only a real Buzzcord.exe replaces itself; a copy hosted by PowerShell (run.ps1, from
        // source) just hears that an update is out.
        public static bool CanInstall { get { return Shortcuts.RunningAsExe; } }

        // Copies the staged files over the ones in binDir. On failure everything is put back and the
        // exception is rethrown.
        public static void Apply(string staged, string binDir)
        {
            var moved = new List<KeyValuePair<string, string>>();   // live file -> its .old copy
            var added = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(staged, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(staged.TrimEnd('\\').Length + 1);
                    string dest = Path.Combine(binDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest))
                    {
                        string old = dest + ".old";
                        if (File.Exists(old)) { try { File.Delete(old); } catch (Exception) { old = dest + "." + DateTime.Now.Ticks + ".old"; } }
                        File.Move(dest, old);
                        moved.Add(new KeyValuePair<string, string>(dest, old));
                    }
                    File.Copy(f, dest);
                    added.Add(dest);
                }
            }
            catch (Exception)
            {
                foreach (var a in added) { try { File.Delete(a); } catch (Exception) { } }
                for (int i = moved.Count - 1; i >= 0; i--) { try { File.Move(moved[i].Value, moved[i].Key); } catch (Exception) { } }
                throw;
            }
        }

        // Deletes the .old files a previous install left behind (and the staging folders). Files still
        // locked by the exiting old copy are retried a few times.
        public static void CleanupOld(string binDir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool left = false;
                foreach (string f in Directory.GetFiles(binDir, "*.old", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch (Exception) { left = true; }
                }
                if (!left) break;
                System.Threading.Thread.Sleep(1000);
            }
            try { if (Directory.Exists(Root) && Staged == null) Directory.Delete(Root, true); } catch (Exception) { }
        }

        // Installs the staged update and starts the new copy. The caller exits right after.
        public static void InstallAndRestart(bool background)
        {
            string staged = Staged;
            if (staged == null || !CanInstall) throw new InvalidOperationException("No update is ready to install.");
            string version = StagedVersion;
            Apply(staged, Shortcuts.BinDir);
            Settings.Set("UpdateStaged", "");
            Settings.Set("UpdatedFrom", Current.ToString());
            Settings.Save();
            Program.Log("update: installed " + version + " over " + Current);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Shortcuts.BinDir, "Buzzcord.exe"),
                "--updated" + (background ? " --background" : "")) { UseShellExecute = false, WorkingDirectory = Shortcuts.BinDir });
        }

        // Short release notes for a dialog: the first few lines of the release text.
        public static string Summary(Release r)
        {
            if (string.IsNullOrEmpty(r.Notes)) return "";
            var lines = new List<string>();
            foreach (var line in r.Notes.Replace("\r", "").Split('\n'))
            {
                string l = line.Trim().TrimStart('#', ' ').Replace("**", "");
                if (l.Length == 0) continue;
                lines.Add(l);
                if (lines.Count == 8) { lines.Add("..."); break; }
            }
            return string.Join("\n", lines);
        }
    }
}
