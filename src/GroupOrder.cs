using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // The order of the buddy list's groups, shared by all accounts. Options > Arrange groups.
    public static class GroupOrder
    {
        public static readonly string[] Default = { "Favorites", "Recent", "Groups", "Friends", "Offline", "Servers", "Requests" };
        public static event Action Changed;

        public static string Label(string key)
        {
            switch (key)
            {
                case "Favorites": return "Favorites";
                case "Recent": return "Recent chats";
                case "Groups": return "Group chats";
                case "Friends": return "Friends (online)";
                case "Offline": return "Offline friends";
                case "Servers": return "Servers";
                case "Requests": return "Friend requests";
                default: return key;
            }
        }

        // The saved order, with unknown names dropped and any missing groups added in their
        // default place, so a new group never disappears.
        public static List<string> Current
        {
            get
            {
                var list = new List<string>();
                foreach (var k in Settings.Get("GroupOrder", "").Split(','))
                    if (Array.IndexOf(Default, k) >= 0 && !list.Contains(k)) list.Add(k);
                // A missing group goes right after the group it follows by default (Offline
                // stays with Friends), or first if nothing before it is there.
                for (int di = 0; di < Default.Length; di++)
                {
                    string k = Default[di];
                    if (list.Contains(k)) continue;
                    int at = 0;
                    for (int p = di - 1; p >= 0; p--)
                    {
                        int i = list.IndexOf(Default[p]);
                        if (i >= 0) { at = i + 1; break; }
                    }
                    list.Insert(at, k);
                }
                return list;
            }
        }

        public static void Save(List<string> order)
        {
            Settings.Set("GroupOrder", string.Join(",", order));
            Settings.Save();
            if (Changed != null) Changed();
        }
    }

    public class ArrangeGroupsForm : Form
    {
        readonly ListBox list = new ListBox { IntegralHeight = false };

        public static void Open(IWin32Window owner)
        {
            using (var f = new ArrangeGroupsForm()) f.ShowDialog(owner);
        }

        ArrangeGroupsForm()
        {
            Text = "Arrange groups";
            Icon = Icons.AppIcon(32);
            Font = new Font("Tahoma", 8.25f);
            BackColor = Icons.Panel;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(300, 250);

            var note = new Label { Text = "Order of the groups in the buddy list (top first):", Location = new Point(10, 10), AutoSize = true };
            list.Location = new Point(10, 30);
            list.Size = new Size(180, 170);
            Fill(GroupOrder.Current);

            var up = new Button { Text = "Move up", Location = new Point(200, 30), Width = 90 };
            var down = new Button { Text = "Move down", Location = new Point(200, 62), Width = 90 };
            var reset = new Button { Text = "Default", Location = new Point(200, 110), Width = 90 };
            up.Click += (s, e) => MoveItem(-1);
            down.Click += (s, e) => MoveItem(1);
            reset.Click += (s, e) => Fill(new List<string>(GroupOrder.Default));
            var ok = new Button { Text = "OK", Location = new Point(134, 214), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Location = new Point(215, 214), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                var order = new List<string>();
                foreach (Item it in list.Items) order.Add(it.Key);
                GroupOrder.Save(order);
            };
            Controls.AddRange(new Control[] { note, list, up, down, reset, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Attach(this, null);
        }

        class Item
        {
            public string Key;
            public override string ToString() { return GroupOrder.Label(Key); }
        }

        void Fill(List<string> order)
        {
            list.Items.Clear();
            foreach (var k in order) list.Items.Add(new Item { Key = k });
            list.SelectedIndex = 0;
        }

        void MoveItem(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var item = list.Items[i];
            list.Items.RemoveAt(i);
            list.Items.Insert(j, item);
            list.SelectedIndex = j;
        }
    }
}
