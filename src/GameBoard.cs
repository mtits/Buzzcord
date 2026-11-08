using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Buzzcord
{
    // Discord's profile game widgets ("Board"): Favorite, Games in Rotation, Games I Like and
    // Want to Play. Read from a profile's "widgets"; changed by replacing the whole set with
    // PUT /users/@me/widgets.
    public static class GameBoard
    {
        public class Kind
        {
            public string Type, Title;
            public int Max;
        }

        public static readonly Kind[] Kinds = {
            new Kind { Type = "favorite_games", Title = "Favorite", Max = 1 },
            new Kind { Type = "current_games", Title = "Games in Rotation", Max = 5 },
            new Kind { Type = "played_games", Title = "Games I Like", Max = 20 },
            new Kind { Type = "want_to_play_games", Title = "Want to Play", Max = 20 } };

        public static Kind KindOf(string type)
        {
            foreach (var k in Kinds) if (k.Type == type) return k;
            return null;
        }

        public class Game
        {
            public string Id, Name, Icon;
            public string IconUrl { get { return Icon != null ? "https://cdn.discordapp.com/app-icons/" + Id + "/" + Icon + ".png?size=128" : null; } }
        }

        static readonly Dictionary<string, Game> games = new Dictionary<string, Game>();

        // Name and pictures of a game, from Discord's public application info. Cached.
        public static async Task<Game> Info(string appId)
        {
            Game g;
            lock (games) if (games.TryGetValue(appId, out g)) return g;
            g = new Game { Id = appId };
            try
            {
                using (var http = Api.NewHttp())
                {
                    var j = Json.ParseObj(await http.GetStringAsync(Api.Base + "/applications/" + appId + "/rpc").ConfigureAwait(false));
                    g.Name = j.Str("name");
                    g.Icon = j.Str("icon");
                }
            }
            catch (Exception ex) { Program.Log("game info " + appId + ": " + ex.Message); }
            if (g.Name == null && PresenceManager.Instance != null) g.Name = PresenceManager.Instance.Detector.NameOf(appId);
            lock (games) games[appId] = g;
            return g;
        }

        public static Game Cached(string appId)
        {
            Game g;
            lock (games) return games.TryGetValue(appId, out g) ? g : null;
        }

        public static Task FetchAll(IEnumerable<string> appIds)
        {
            var jobs = new List<Task>();
            foreach (var id in appIds) if (Cached(id) == null) jobs.Add(Info(id));
            return Task.WhenAll(jobs);
        }

        // Game ids in each widget of a profile's "widgets" list.
        public static IEnumerable<string> GameIds(List<object> widgets)
        {
            if (widgets == null) yield break;
            foreach (var o in widgets)
            {
                var data = o is JObj ? ((JObj)o).Obj("data") : null;
                if (data == null) continue;
                foreach (var e in data.Objs("games")) if (e.Str("game_id") != null) yield return e.Str("game_id");
            }
        }

        public static List<JObj> Entries(List<object> widgets, string type)
        {
            if (widgets != null)
                foreach (var o in widgets)
                {
                    var data = o is JObj ? ((JObj)o).Obj("data") : null;
                    if (data != null && data.Str("type") == type) return new List<JObj>(data.Objs("games"));
                }
            return new List<JObj>();
        }

        public static string TagText(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return tag;
            string t = tag.Replace('_', ' ');
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        // ---- changing your own board ----

        public static async Task<List<object>> Load(Session s)
        {
            var p = await s.Api.Get("/users/" + s.Me.Id + "/profile") as JObj;
            return (p != null ? p.Arr("widgets") : null) ?? new List<object>();
        }

        // Applies one change to the widget list. Returns null when it worked, else why not.
        public static string Change(List<object> widgets, string type, string appId, bool add)
        {
            var kind = KindOf(type);
            JObj data = null;
            foreach (var o in widgets)
            {
                var d = o is JObj ? ((JObj)o).Obj("data") : null;
                if (d != null && d.Str("type") == type) data = d;
            }
            if (data == null)
            {
                if (!add) return null;
                data = Json.O("type", type, "games", new List<object>());
                widgets.Add(Json.O("data", data));
            }
            var list = data.Arr("games");
            if (list == null) { list = new List<object>(); data["games"] = list; }
            int at = list.FindIndex(o => o is JObj && ((JObj)o).Str("game_id") == appId);
            if (!add)
            {
                if (at >= 0) list.RemoveAt(at);
                return null;
            }
            if (at >= 0) return "It's already in " + kind.Title + ".";
            if (kind.Max == 1) list.Clear();              // a new favorite replaces the old one
            else if (list.Count >= kind.Max) return kind.Title + " is full (" + kind.Max + " games). Remove one first.";
            list.Insert(0, Json.O("game_id", appId));
            return null;
        }

        public static async Task Save(Session s, List<object> widgets)
        {
            var body = new List<object>();
            foreach (var o in widgets)
            {
                var w = o as JObj;
                if (w == null || w.Obj("data") == null) continue;
                var outW = Json.O("data", w.Obj("data"));
                if (w.Str("id") != null) outW["id"] = w.Str("id");
                body.Add(outW);
            }
            await s.Api.Put("/users/@me/widgets", Json.O("widgets", body)).ConfigureAwait(false);
        }

        // Loads, changes and saves in one go. Returns null when it worked, else a message.
        public static async Task<string> Update(Session s, string type, string appId, bool add)
        {
            try
            {
                var widgets = await Load(s).ConfigureAwait(false);
                string problem = Change(widgets, type, appId, add);
                if (problem != null) return problem;
                await Save(s, widgets).ConfigureAwait(false);
                if (Changed != null) Changed(s);
                return null;
            }
            catch (Exception ex)
            {
                Program.Log("game board: " + ex.Message);
                return ex.Message;
            }
        }

        public static event Action<Session> Changed;   // after a successful save (any thread)

        // "Add to Favorite / Games in Rotation / ..." for a game, used by the header and profiles.
        public static ToolStripMenuItem AddMenu(Control owner, Session s, string appId, string name, Action<string> report)
        {
            var root = new ToolStripMenuItem("Add " + (name ?? "this game") + " to my profile");
            foreach (var k in Kinds)
            {
                var kind = k;
                root.DropDownItems.Add(kind.Title, null, async (o, e) =>
                {
                    string problem = await Update(s, kind.Type, appId, true);
                    string text = problem ?? (name ?? "The game") + " added to " + kind.Title + ".";
                    if (owner.IsDisposed) return;
                    if (report != null) report(text);
                    else MessageBox.Show(owner, text, "Buzzcord");
                });
            }
            return root;
        }
    }

    // Picks a game by name from Discord's list of detectable games.
    public class GamePickerForm : Form
    {
        readonly TextBox search = new TextBox { Dock = DockStyle.Top };
        readonly ListBox results = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        public string AppId, GameName;

        public GamePickerForm(string title)
        {
            Text = title;
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            ClientSize = new Size(320, 360);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
            body.Controls.Add(results);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
            body.Controls.Add(search);
            Controls.Add(body);
            Controls.Add(buttons);
            Native.SetCueBanner(search, "Search games...");

            search.TextChanged += (o, e) =>
            {
                results.BeginUpdate();
                results.Items.Clear();
                foreach (var g in PresenceManager.Instance.Detector.Search(search.Text, 60)) results.Items.Add(g);
                results.EndUpdate();
                if (results.Items.Count > 0) results.SelectedIndex = 0;
            };
            results.SelectedIndexChanged += (o, e) => ok.Enabled = results.SelectedItem != null;
            results.DoubleClick += (o, e) => { if (results.SelectedItem != null) { DialogResult = DialogResult.OK; Close(); } };
            search.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Down && results.SelectedIndex < results.Items.Count - 1) { results.SelectedIndex++; e.Handled = true; }
                if (e.KeyCode == Keys.Up && results.SelectedIndex > 0) { results.SelectedIndex--; e.Handled = true; }
            };
            FormClosing += (o, e) =>
            {
                var g = results.SelectedItem as GameDetector.Game;
                if (DialogResult == DialogResult.OK && g != null) { AppId = g.AppId; GameName = g.Name; }
            };
            Theme.Attach(this, null);
        }
    }
}
