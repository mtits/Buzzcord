using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Buzzcord
{
    // A scrolling list of variable-height rows, painted off-screen. Replacing the rows only
    // repaints, so the list never blinks, loses its scroll position or drops the selection.
    public class RowList : Control
    {
        public delegate void DrawRowHandler(Graphics g, object row, Rectangle bounds, bool selected, bool hover);

        readonly VScrollBar bar = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        readonly Timer scrollAnim = new Timer { Interval = 10 };
        List<object> rows = new List<object>();
        List<int> tops = new List<int>();
        int total, offset, target, selected = -1, hover = -1;

        public Func<object, int> RowHeight = r => 20;
        public Func<object, string> RowKey = r => null;
        public event DrawRowHandler DrawRow;
        public event Action<object, MouseEventArgs> RowClick;
        public event Action<object> RowActivate;     // double-click or Enter
        public event Action SelectionChanged;

        public RowList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Window;
            Controls.Add(bar);
            bar.Scroll += (s, e) => { target = offset = e.NewValue; Invalidate(); };
            scrollAnim.Tick += (s, e) => StepScroll();
        }

        public object SelectedRow { get { return selected >= 0 && selected < rows.Count ? rows[selected] : null; } }

        public void SetRows(List<object> newRows)
        {
            string selKey = SelectedRow != null ? RowKey(SelectedRow) : null;
            rows = newRows;
            MeasureRows();
            int sel = -1;
            if (selKey != null)
                for (int i = 0; i < rows.Count; i++)
                    if (RowKey(rows[i]) == selKey) { sel = i; break; }
            bool changed = sel != selected;
            selected = sel;
            hover = -1;
            Invalidate();
            if (changed && SelectionChanged != null) SelectionChanged();
        }

        void MeasureRows()
        {
            tops = new List<int>(rows.Count);
            total = 0;
            foreach (var r in rows) { tops.Add(total); total += RowHeight(r); }
            UpdateBar();
        }

        void UpdateBar()
        {
            int view = ClientSize.Height;
            bool need = total > view;
            bar.Visible = need;
            if (!need) { offset = target = 0; return; }
            bar.Minimum = 0;
            bar.LargeChange = Math.Max(1, view);
            bar.SmallChange = 20;
            bar.Maximum = total - 1;
            offset = Clamp(offset);
            target = Clamp(target);
            bar.Value = offset;
        }

        int Clamp(int v) { return Math.Max(0, Math.Min(v, Math.Max(0, total - ClientSize.Height))); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateBar();
        }

        int RowWidth { get { return ClientSize.Width - (bar.Visible ? bar.Width : 0); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (DrawRow == null) return;
            int w = RowWidth;
            for (int i = FirstVisible(); i < rows.Count; i++)
            {
                int y = tops[i] - offset;
                if (y > ClientSize.Height) break;
                var r = new Rectangle(0, y, w, RowHeight(rows[i]));
                if (!r.IntersectsWith(e.ClipRectangle)) continue;
                DrawRow(g, rows[i], r, i == selected, i == hover);
            }
        }

        int FirstVisible()
        {
            int lo = 0, hi = rows.Count - 1, ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (tops[mid] <= offset) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        public object SelectFirst(Func<object, bool> match)
        {
            for (int i = 0; i < rows.Count; i++)
                if (match(rows[i])) { Select(i); return rows[i]; }
            return null;
        }

        // Where a row is drawn right now, in this control's coordinates.
        public Rectangle BoundsOf(object row)
        {
            int i = rows.IndexOf(row);
            return i < 0 ? Rectangle.Empty : new Rectangle(0, tops[i] - offset, RowWidth, RowHeight(row));
        }

        public object RowAt(Point p)
        {
            int i = IndexAt(p);
            return i >= 0 ? rows[i] : null;
        }

        public int IndexAt(Point p)
        {
            if (p.X >= RowWidth) return -1;
            int y = p.Y + offset;
            for (int i = FirstVisible(); i < rows.Count; i++)
            {
                if (y >= tops[i] && y < tops[i] + RowHeight(rows[i])) return i;
                if (tops[i] > y) break;
            }
            return -1;
        }

        void Select(int i)
        {
            if (i == selected || i < -1 || i >= rows.Count) return;
            selected = i;
            if (i >= 0) EnsureVisible(i);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        void EnsureVisible(int i)
        {
            int h = RowHeight(rows[i]);
            if (tops[i] < offset) ScrollTo(tops[i], false);
            else if (tops[i] + h > offset + ClientSize.Height) ScrollTo(tops[i] + h - ClientSize.Height, false);
        }

        void ScrollTo(int y, bool animate)
        {
            target = Clamp(y);
            if (!animate) { offset = target; SyncBar(); Invalidate(); return; }
            if (!scrollAnim.Enabled) scrollAnim.Start();
        }

        void StepScroll()
        {
            int diff = target - offset;
            if (Math.Abs(diff) <= 1) { offset = target; scrollAnim.Stop(); }
            else offset += (int)Math.Round(diff * 0.4);
            SyncBar();
            Invalidate();
        }

        void SyncBar()
        {
            if (bar.Visible) bar.Value = Math.Max(bar.Minimum, Math.Min(offset, bar.Maximum));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // About three rows per notch, eased.
            ScrollTo(target - e.Delta * 60 / 120 * SystemInformation.MouseWheelScrollLines / 3, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = IndexAt(e.Location);
            if (i < 0) return;
            Select(i);
            if (RowClick != null) RowClick(rows[i], e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = IndexAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left && RowActivate != null) RowActivate(rows[i]);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Enter: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (rows.Count == 0) return;
            int page = Math.Max(1, ClientSize.Height / 24);
            switch (e.KeyCode)
            {
                case Keys.Up: Select(Math.Max(0, selected - 1)); break;
                case Keys.Down: Select(Math.Min(rows.Count - 1, selected + 1)); break;
                case Keys.PageUp: Select(Math.Max(0, selected - page)); break;
                case Keys.PageDown: Select(Math.Min(rows.Count - 1, selected + page)); break;
                case Keys.Home: Select(0); break;
                case Keys.End: Select(rows.Count - 1); break;
                case Keys.Enter: if (SelectedRow != null && RowActivate != null) RowActivate(SelectedRow); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
