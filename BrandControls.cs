using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ProjektovePoznamky
{
    /// <summary>Win32 messages connecting custom scrollbars to native controls.</summary>
    internal static class ScrollNative
    {
        /// <summary>Send a synchronous message to read or update a native editor or list position.</summary>
        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        /// <summary>Hide the native ListBox scrollbar so only the custom control remains visible.</summary>
        [DllImport("user32.dll")]
        internal static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
    }

    /// <summary>Vector icon for adding videos from the bottom toolbar.</summary>
    internal sealed class VideoButton : Button
    {
        /// <summary>Draw a scaled playback icon using the standard button theme.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = Math.Min(Width, Height) / 32f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TranslateTransform((Width - 32 * scale) / 2, (Height - 32 * scale) / 2);
            e.Graphics.ScaleTransform(scale, scale);
            using (Pen frame = new Pen(Enabled ? MainForm.Ink : Color.Gray, 1.5f)) e.Graphics.DrawRectangle(frame, 6, 7, 20, 18);
            using (SolidBrush play = new SolidBrush(Enabled ? MainForm.Accent : Color.Gray))
                e.Graphics.FillPolygon(play, new PointF[] { new PointF(13, 11), new PointF(13, 21), new PointF(21, 16) });
        }
    }

    /// <summary>Project list using native ListBox selection and custom scrolling.</summary>
    internal sealed class ProjectListBox : ListBox
    {
        /// <summary>Notify subscribers when the item count, size, or scroll position changes.</summary>
        internal event Action ViewChanged;
        private bool updating;
        private int wheelDelta;
        /// <summary>Number of complete rows fitting inside the client area.</summary>
        internal int VisibleItems { get { return Math.Max(1, ClientSize.Height / ItemHeight); } }

        /// <summary>Remove the native vertical scrollbar style replaced by BrandScrollBar.</summary>
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.Style &= ~0x00200000; return parameters; }
        }

        /// <summary>Handle the mouse wheel, hide the native scrollbar, and report viewport changes without recursion.</summary>
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x020a)
            {
                wheelDelta += unchecked((short)((long)message.WParam >> 16));
                int steps = wheelDelta / 120;
                wheelDelta %= 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                if (Items.Count > 0) TopIndex = Math.Max(0, Math.Min(Math.Max(0, Items.Count - VisibleItems), TopIndex - steps * (lines < 0 ? VisibleItems : lines)));
                return;
            }
            base.WndProc(ref message);
            if (updating || !IsHandleCreated) return;
            updating = true;
            try
            {
                // ListBox otherwise recreates its native scrollbar as items or its size change.
                ScrollNative.ShowScrollBar(Handle, 1, false);
                if (ViewChanged != null) ViewChanged();
            }
            finally { updating = false; }
        }
    }

    /// <summary>Vertical scrollbar for projects or visual note lines; callers define the units.</summary>
    internal sealed class BrandScrollBar : Control
    {
        /// <summary>Request scrolling of the connected editor or list.</summary>
        internal event Action<int> ValueChanged;
        /// <summary>Content position clamped between zero and Maximum.</summary>
        internal int Value { get; private set; }
        /// <summary>Highest first visible item after accounting for the page size.</summary>
        internal int Maximum { get; private set; }
        /// <summary>Number of visible content units, at least one.</summary>
        internal int Page { get; private set; }
        private int dragOffset;
        private int wheelDelta;
        private bool dragging;
        private bool hovering;

        /// <summary>Create a metallic vertical scrollbar with an accessible name and role.</summary>
        internal BrandScrollBar(string name)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Dock = DockStyle.Right;
            Width = 19;
            BackColor = MainForm.Surface;
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = name;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        /// <summary>Update the item count, page size, and position; hide the scrollbar for short content.</summary>
        /// <param name="total">Total number of content units.</param>
        /// <param name="page">Number of units fitting on one page.</param>
        /// <param name="value">Index of the first visible unit.</param>
        internal void Configure(int total, int page, int value)
        {
            int maximum = Math.Max(0, total - page);
            value = Math.Max(0, Math.Min(maximum, value));
            if (Maximum == maximum && Page == page && Value == value) return;
            Maximum = maximum;
            Page = Math.Max(1, page);
            Value = value;
            Visible = maximum > 0;
            RefreshLanguage();
            Invalidate();
        }

        /// <summary>Update the accessible position description in the current language.</summary>
        internal void RefreshLanguage() { AccessibleDescription = Ui.T("Position {0} of {1}", Value + 1, Maximum + 1); }

        /// <summary>Clamp the new position, redraw the thumb, and notify the connected control.</summary>
        internal void ScrollTo(int value)
        {
            value = Math.Max(0, Math.Min(Maximum, value));
            if (Value == value) return;
            Value = value;
            RefreshLanguage();
            Invalidate();
            if (ValueChanged != null) ValueChanged(value);
        }

        /// <summary>Thumb rectangle calculated from the total range and current position.</summary>
        internal Rectangle Thumb
        {
            get
            {
                int track = Math.Max(1, Height - 4);
                int size = Math.Min(track, Math.Max(Width * 2, (int)((long)track * Page / Math.Max(1, Maximum + Page))));
                int y = 2 + (Maximum == 0 ? 0 : (int)((long)(track - size) * Value / Maximum));
                return new Rectangle(3, y, Math.Max(1, Width - 6), size);
            }
        }

        /// <summary>Draw the metallic thumb with red accents for focus, hover, and dragging.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(18, 21, 25));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle thumb = Thumb;
            using (Pen inset = new Pen(Color.FromArgb(42, 45, 51)))
                e.Graphics.DrawLine(inset, Width / 2, 3, Width / 2, Height - 3);
            float corner = Math.Min(thumb.Width / 3f, 5f);
            using (GraphicsPath shape = new GraphicsPath())
            {
                shape.AddPolygon(new PointF[] {
                    new PointF(thumb.Left + corner, thumb.Top), new PointF(thumb.Right - corner, thumb.Top),
                    new PointF(thumb.Right, thumb.Top + corner), new PointF(thumb.Right, thumb.Bottom - corner),
                    new PointF(thumb.Right - corner, thumb.Bottom), new PointF(thumb.Left + corner, thumb.Bottom),
                    new PointF(thumb.Left, thumb.Bottom - corner), new PointF(thumb.Left, thumb.Top + corner) });
                bool active = dragging || hovering || Focused;
                using (Pen glow = new Pen(Color.FromArgb(active ? 75 : 30, MainForm.Accent), 4)) e.Graphics.DrawPath(glow, shape);
                using (LinearGradientBrush metal = new LinearGradientBrush(thumb, Color.Black, Color.White, LinearGradientMode.Horizontal))
                {
                    metal.InterpolationColors = new ColorBlend {
                        Positions = new float[] { 0, .18f, .45f, .75f, 1 },
                        Colors = new Color[] { Color.FromArgb(57, 63, 72), Color.FromArgb(140, 148, 159), Color.FromArgb(71, 77, 88), Color.FromArgb(44, 49, 58), Color.FromArgb(105, 111, 123) } };
                    e.Graphics.FillPath(metal, shape);
                }
                using (Pen edge = new Pen(active ? MainForm.Accent : Color.FromArgb(139, 146, 157))) e.Graphics.DrawPath(edge, shape);
                float center = thumb.Top + thumb.Height / 2f;
                for (int i = -1; i <= 1; i++)
                {
                    float y = center + i * Math.Max(4, Width / 5f);
                    using (Pen recess = new Pen(Color.FromArgb(24, 26, 31), 3)) e.Graphics.DrawLine(recess, thumb.Left + 4, y, thumb.Right - 4, y);
                    using (Pen red = new Pen(i == 0 || active ? MainForm.Accent : Color.FromArgb(128, 49, 44), 1.5f))
                        e.Graphics.DrawLine(red, thumb.Left + 4, y + 1, thumb.Right - 4, y + 1);
                }
            }
        }

        /// <summary>Start dragging the thumb or scroll by one page when the track is clicked.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Rectangle thumb = Thumb;
            if (thumb.Contains(e.Location))
            {
                dragOffset = e.Y - thumb.Top;
                dragging = true;
                Capture = true;
                Invalidate();
            }
            else ScrollTo(Value + (e.Y < thumb.Top ? -Page : Page));
        }

        /// <summary>Convert thumb dragging into a content position.</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            int travel = Height - 4 - Thumb.Height;
            if (travel > 0) ScrollTo((int)Math.Round((double)(e.Y - dragOffset - 2) * Maximum / travel));
        }

        /// <summary>End dragging and release mouse capture.</summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            dragging = false;
            Capture = false;
            Invalidate();
        }

        /// <summary>End dragging when capture is lost so the thumb cannot remain active.</summary>
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; Invalidate(); } }
        /// <summary>Activate the thumb appearance when the pointer enters.</summary>
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        /// <summary>Restore the normal appearance when the pointer leaves.</summary>
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        /// <summary>Redraw the focus indicator for keyboard operation.</summary>
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        /// <summary>Remove the focus indicator when another control receives focus.</summary>
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        /// <summary>Reserve arrow and paging keys for the scrollbar.</summary>
        protected override bool IsInputKey(Keys key) { return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(key); }
        /// <summary>Handle arrows, Page Up/Down, and Home/End to update the content position.</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up: ScrollTo(Value - 1); break;
                case Keys.Down: ScrollTo(Value + 1); break;
                case Keys.PageUp: ScrollTo(Value - Page); break;
                case Keys.PageDown: ScrollTo(Value + Page); break;
                case Keys.Home: ScrollTo(0); break;
                case Keys.End: ScrollTo(Maximum); break;
                default: return;
            }
            e.Handled = true;
        }
        /// <summary>Accumulate partial wheel deltas and honor the system line or page scroll setting.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            wheelDelta += e.Delta;
            int steps = wheelDelta / 120;
            wheelDelta %= 120;
            int lines = SystemInformation.MouseWheelScrollLines;
            ScrollTo(Value - steps * (lines < 0 ? Page : lines));
        }
    }

    /// <summary>Vector pencil icon for renaming projects and attachments.</summary>
    internal sealed class RenameButton : Button
    {
        /// <summary>Draw a scaled pencil icon using the standard button theme.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(Width, Height) / 38f;
            e.Graphics.TranslateTransform((Width - 24 * scale) / 2, (Height - 24 * scale) / 2);
            e.Graphics.ScaleTransform(scale, scale);
            using (Pen silver = new Pen(MainForm.Ink, 1.5f))
                e.Graphics.DrawPolygon(silver, new Point[] { new Point(5, 16), new Point(15, 6), new Point(20, 11), new Point(10, 21), new Point(3, 23) });
            using (SolidBrush red = new SolidBrush(MainForm.Accent))
                e.Graphics.FillPolygon(red, new Point[] { new Point(16, 5), new Point(19, 2), new Point(24, 7), new Point(21, 10) });
            e.Graphics.Restore(state);
        }
    }

    /// <summary>Vector trash icon requiring no additional bitmap assets.</summary>
    internal sealed class DeleteButton : Button
    {
        /// <summary>Draw a scaled trash icon using the standard button theme.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(Width, Height) / 38f;
            e.Graphics.TranslateTransform((Width - 24 * scale) / 2, (Height - 24 * scale) / 2);
            e.Graphics.ScaleTransform(scale, scale);
            using (Pen silver = new Pen(MainForm.Ink, 1.5f))
            {
                e.Graphics.DrawLines(silver, new Point[] { new Point(6, 7), new Point(7, 22), new Point(18, 22), new Point(19, 7) });
                e.Graphics.DrawLine(silver, 10, 11, 10, 18);
                e.Graphics.DrawLine(silver, 15, 11, 15, 18);
            }
            using (Pen red = new Pen(MainForm.Accent, 2))
            {
                e.Graphics.DrawLine(red, 4, 6, 21, 6);
                e.Graphics.DrawLines(red, new Point[] { new Point(9, 5), new Point(9, 2), new Point(16, 2), new Point(16, 5) });
            }
            e.Graphics.Restore(state);
        }
    }

    /// <summary>Vector icon for exporting a project to ZIP.</summary>
    internal sealed class ExportButton : Button
    {
        /// <summary>Draw a scaled export icon using the standard button theme.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(Width, Height) / 38f;
            e.Graphics.TranslateTransform((Width - 24 * scale) / 2, (Height - 24 * scale) / 2);
            e.Graphics.ScaleTransform(scale, scale);
            using (Pen silver = new Pen(MainForm.Ink, 1.5f))
                e.Graphics.DrawLines(silver, new Point[] { new Point(4, 11), new Point(4, 22), new Point(21, 22), new Point(21, 11) });
            using (Pen red = new Pen(MainForm.Accent, 2))
            {
                e.Graphics.DrawLine(red, 12, 16, 12, 3);
                e.Graphics.DrawLines(red, new Point[] { new Point(7, 8), new Point(12, 3), new Point(17, 8) });
            }
            e.Graphics.Restore(state);
        }
    }
}
