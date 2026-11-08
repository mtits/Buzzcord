using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Buzzcord
{
    // settings.ini in %APPDATA%\Buzzcord: plain key=value lines.
    public static class Settings
    {
        // BUZZCORD_DATA points somewhere else for the self-test, so it never touches real data.
        public static readonly string Folder = Environment.GetEnvironmentVariable("BUZZCORD_DATA") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buzzcord");
        static readonly string FilePath = Path.Combine(Folder, "settings.ini");
        static readonly Dictionary<string, string> values = Load();
        static readonly object gate = new object();

        static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && !line.StartsWith("#")) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch (Exception) { }
            return d;
        }

        public static string Get(string key, string fallback)
        {
            lock (gate)
            {
                string v;
                return values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static bool Has(string key) { lock (gate) return values.ContainsKey(key); }

        public static bool GetBool(string key, bool fallback)
        {
            string v = Get(key, null);
            return v == null ? fallback : v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static void Set(string key, string value) { lock (gate) values[key] = value; }
        public static void SetBool(string key, bool value) { Set(key, value ? "1" : "0"); }

        public static int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static void SetInt(string key, int value) { Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        public static void Save()
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var sb = new StringBuilder("# Buzzcord settings\r\n");
                    var keys = new List<string>(values.Keys);
                    keys.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in keys) sb.Append(k).Append('=').Append(values[k]).Append("\r\n");
                    File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { Program.Log("settings: " + ex.Message); }
            }
        }

        public static string SaveFolder
        {
            get
            {
                string def = Environment.GetEnvironmentVariable("BUZZCORD_DATA") != null
                    ? Path.Combine(Folder, "Saved")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Buzzcord Saved");
                return Get("SaveFolder", def);
            }
        }
    }

    // Account tokens, encrypted with Windows DPAPI so only this Windows user can read them.
    public static class Accounts
    {
        static readonly string FilePath = Path.Combine(Settings.Folder, "accounts.dat");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Buzzcord.Accounts.v1");

        static readonly string BackupPath = FilePath + ".bak";

        public static List<string> Load()
        {
            foreach (var path in new[] { FilePath, BackupPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var list = new List<string>();
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                    foreach (var line in Encoding.UTF8.GetString(plain).Split('\n'))
                        if (line.Trim().Length > 0 && !list.Contains(line.Trim())) list.Add(line.Trim());
                    Program.Log("accounts: loaded " + Describe(list) + (path == BackupPath ? " from the backup" : ""));
                    return list;
                }
                catch (Exception ex) { Program.Log("accounts: couldn't read " + Path.GetFileName(path) + ": " + ex.Message); }
            }
            return new List<string>();
        }

        // Writes the full list of signed-in accounts. A temp file plus File.Replace means a crash
        // mid-write can never leave a half-written file, and the previous list stays as .bak.
        public static void Save(List<string> tokens)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(string.Join("\n", tokens)), Entropy, DataProtectionScope.CurrentUser);
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, BackupPath);
                else File.Move(tmp, FilePath);
                Program.Log("accounts: saved " + Describe(tokens));
            }
            catch (Exception ex) { Program.Log("accounts: SAVE FAILED: " + ex); }
        }

        // User ids only (the part of a token before the first dot); never the token itself.
        static string Describe(List<string> tokens)
        {
            var ids = new List<string>();
            foreach (var t in tokens)
            {
                string id = t.Split('.')[0];
                try { id = Encoding.ASCII.GetString(Convert.FromBase64String(id + new string('=', (4 - id.Length % 4) % 4))); }
                catch (Exception) { }
                ids.Add(id);
            }
            return tokens.Count + " (" + string.Join(", ", ids) + ")";
        }
    }
}
