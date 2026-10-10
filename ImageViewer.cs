using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ProjektovePoznamky
{
    /// <summary>Own an image bitmap, scale, and offset independently of the source file.</summary>
    internal sealed class ZoomCanvas : Control
    {
        /// <summary>Bitmap owned by the canvas; LoadImage releases the original file.</summary>
        internal Image Image { get; private set; }
        /// <summary>Screen units per image pixel.</summary>
        internal float Zoom { get; private set; }
        /// <summary>Offset from the centered image in control coordinates.</summary>
        internal PointF Pan { get; private set; }
        /// <summary>Notify subscribers when the scale or offset changes.</summary>
        internal event Action ViewChanged;
        private Point lastMouse;
        private bool fitting = true;

        /// <summary>Create a focusable drawing surface supporting zoom and image dragging.</summary>
        internal ZoomCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(18, 21, 25);
            AccessibleName = "Image; scroll to zoom, drag to pan";
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        /// <summary>Load an independent bitmap before replacing and disposing the previous image.</summary>
        internal void LoadImage(string file)
        {
            Bitmap next;
            using (Image source = System.Drawing.Image.FromFile(file)) next = new Bitmap(source);
            Image old = Image;
            Image = next;
            if (old != null) old.Dispose();
            Fit();
        }

        /// <summary>Dispose the deleted image and clear the canvas before updating the attachment list.</summary>
        internal void ClearImage()
        {
            if (Image != null) Image.Dispose();
            Image = null;
            Fit();
        }

        /// <summary>Fit the entire image to the canvas and center it.</summary>
        internal void Fit()
        {
            fitting = true;
            Pan = PointF.Empty;
            Zoom = Image == null ? 1 : Math.Max(.0001f, Math.Min((float)ClientSize.Width / Image.Width, (float)ClientSize.Height / Image.Height));
            Changed();
        }

        /// <summary>Use the original 1:1 scale and center the image.</summary>
        internal void OriginalSize()
        {
            fitting = false;
            Zoom = 1;
            Pan = PointF.Empty;
            Changed();
        }

        /// <summary>Current image rectangle after applying zoom and pan.</summary>
        internal RectangleF ImageBounds
        {
            get
            {
                if (Image == null) return RectangleF.Empty;
                float width = Image.Width * Zoom, height = Image.Height * Zoom;
                return new RectangleF((Width - width) / 2 + Pan.X, (Height - height) / 2 + Pan.Y, width, height);
            }
        }

        /// <summary>Zoom around the image point under the cursor and constrain the offset.</summary>
        internal void ZoomAt(Point point, int wheelDelta)
        {
            if (Image == null || Zoom <= 0) return;
            RectangleF old = ImageBounds;
            float x = (point.X - old.X) / Zoom, y = (point.Y - old.Y) / Zoom;
            float next = Math.Max(.0001f, Math.Min(32, Zoom * (float)Math.Pow(1.15, wheelDelta / 120d)));
            Zoom = next;
            fitting = false;
            Pan = new PointF(point.X - x * next - (Width - Image.Width * next) / 2,
                point.Y - y * next - (Height - Image.Height * next) / 2);
            ClampPan();
            Changed();
        }

        /// <summary>Constrain pan so the enlarged image cannot leave the canvas entirely.</summary>
        private void ClampPan()
        {
            if (Image == null) return;
            float x = Math.Max(0, (Image.Width * Zoom - Width) / 2);
            float y = Math.Max(0, (Image.Height * Zoom - Height) / 2);
            Pan = new PointF(Math.Max(-x, Math.Min(x, Pan.X)), Math.Max(-y, Math.Min(y, Pan.Y)));
        }

        /// <summary>Request repainting and notify subscribers to update the scale label.</summary>
        private void Changed() { Invalidate(); if (ViewChanged != null) ViewChanged(); }
        /// <summary>Recalculate fit or constrain the existing offset after resizing.</summary>
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (fitting) Fit(); else { ClampPan(); Changed(); } }
        /// <summary>Draw the owned bitmap with interpolation appropriate to the zoom level.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Image == null) return;
            e.Graphics.InterpolationMode = Zoom >= 4 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(Image, ImageBounds);
        }
        /// <summary>Zoom in or out around the mouse pointer.</summary>
        protected override void OnMouseWheel(MouseEventArgs e) { ZoomAt(e.Location, e.Delta); }
        /// <summary>Focus the canvas and start dragging with mouse capture.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus(); lastMouse = e.Location; Capture = true; Cursor = Cursors.SizeAll;
        }
        /// <summary>Move the captured image while constraining its edges.</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture) return;
            Pan = new PointF(Pan.X + e.X - lastMouse.X, Pan.Y + e.Y - lastMouse.Y);
            lastMouse = e.Location;
            ClampPan(); Changed();
        }
        /// <summary>End dragging and restore the hand cursor.</summary>
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { Capture = false; Cursor = Cursors.Hand; } }
        /// <summary>Dispose the owned bitmap before releasing native Control resources.</summary>
        protected override void Dispose(bool disposing) { if (disposing && Image != null) { Image.Dispose(); Image = null; } base.Dispose(disposing); }
    }

    /// <summary>Modal image viewer with navigation and project modification callbacks.</summary>
    internal sealed class ImageViewer : Form
    {
        internal readonly ZoomCanvas Canvas = new ZoomCanvas();
        /// <summary>Index of the selected path in the viewer&#x27;s local list.</summary>
        internal int CurrentIndex { get; private set; }
        internal readonly Button Previous = MainForm.MakeButton("‹");
        internal readonly Button Next = MainForm.MakeButton("›");
        internal readonly Button Delete = MainForm.MakeButton("Delete image");
        /// <summary>Small rename pencil; disabled when no rename callback is supplied.</summary>
        internal readonly Button Rename = new RenameButton();
        /// <summary>Replaceable error handler for isolated tests without modal dialogs.</summary>
        internal Action<string> ErrorReported = null;
        private readonly Label caption = new SingleLineLabel();
        private readonly Func<string, bool> deleteImage;
        private readonly Func<string, string> renameImage;
        private string[] files;

        /// <summary>Open an independent viewer over a nonempty file list.</summary>
        /// <param name="files">Nonempty local paths in the desired order.</param>
        /// <param name="initial">Initial path; use the first file if it is absent.</param>
        /// <param name="deleteImage">Confirmation and deletion callback; null disables deletion.</param>
        /// <param name="renameImage">Return the new path or null on cancellation/failure; null disables renaming.</param>
        internal ImageViewer(string[] files, string initial, Func<string, bool> deleteImage, Func<string, string> renameImage = null)
        {
            if (files.Length == 0) throw new InvalidOperationException(Ui.T("The project has no images."));
            SuspendLayout();
            this.files = files;
            this.deleteImage = deleteImage;
            this.renameImage = renameImage;
            Size = new Size(900, 680);
            MinimumSize = new Size(520, 320);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = MainForm.Surface;
            ForeColor = MainForm.Ink;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;
            Panel toolbar = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 6, 8, 6), BackColor = MainForm.Surface };
            Previous.Width = Next.Width = 34;
            Previous.Dock = Next.Dock = DockStyle.Left;
            Previous.AccessibleName = "Previous image";
            Next.AccessibleName = "Next image";
            Previous.Click += delegate { ShowAt(CurrentIndex - 1); };
            Next.Click += delegate { ShowAt(CurrentIndex + 1); };
            Button fit = MainForm.MakeButton("Fit");
            fit.Dock = DockStyle.Right; fit.Width = 100;
            fit.Click += delegate { Canvas.Fit(); };
            Button original = MainForm.MakeButton("100 %");
            original.Dock = DockStyle.Right; original.Width = 64;
            original.Click += delegate { Canvas.OriginalSize(); };
            Delete.Dock = DockStyle.Right; Delete.Width = 124; Delete.Enabled = deleteImage != null;
            Delete.Click += delegate { DeleteCurrent(); };
            MainForm.ApplyButtonTheme(Rename); Rename.Dock = DockStyle.Right; Rename.Width = 34;
            Rename.Enabled = renameImage != null; Rename.AccessibleName = "Rename image";
            Rename.Click += delegate { RenameCurrent(); };
            caption.Dock = DockStyle.Fill;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.ForeColor = MainForm.Ink;
            caption.BackColor = MainForm.Surface;
            toolbar.Controls.Add(caption);
            toolbar.Controls.Add(Next);
            toolbar.Controls.Add(Previous);
            toolbar.Controls.Add(original);
            toolbar.Controls.Add(fit);
            toolbar.Controls.Add(Delete);
            toolbar.Controls.Add(Rename);
            Controls.Add(Canvas);
            Controls.Add(toolbar);
            Canvas.ViewChanged += UpdateCaption;
            Shown += delegate { Canvas.Focus(); };
            int start = Array.FindIndex(files, f => string.Equals(f, initial, StringComparison.OrdinalIgnoreCase));
            ShowAt(Math.Max(0, start));
            Ui.LanguageChanged += ApplyLanguage;
            ApplyLanguage();
            ResumeLayout(true);
        }

        /// <summary>Translate the open viewer&#x27;s controls without changing attachment names.</summary>
        private void ApplyLanguage() { Ui.Apply(this); UpdateCaption(); }

        /// <summary>Unsubscribe from language events and release viewer resources.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) Ui.LanguageChanged -= ApplyLanguage;
            base.Dispose(disposing);
        }

        /// <summary>Update information about the selected attachment and display state.</summary>
        private void UpdateCaption()
        {
            if (files.Length == 0) { caption.Text = Ui.T("No images"); return; }
            caption.Text = (CurrentIndex + 1) + " / " + files.Length + "  ·  " + Math.Round(Canvas.Zoom * 100) + " %  ·  " + Path.GetFileName(files[CurrentIndex]);
        }

        /// <summary>Open the indexed file; return false for invalid indices or loading errors.</summary>
        internal bool ShowAt(int index)
        {
            if (index < 0 || index >= files.Length) return false;
            int previous = CurrentIndex;
            CurrentIndex = index;
            try { Canvas.LoadImage(files[index]); }
            catch (Exception error)
            {
                CurrentIndex = previous;
                if (ErrorReported != null) ErrorReported(error.Message);
                else MessageBox.Show(this, Ui.T("Cannot open the image.\n\n") + error.Message, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            CurrentIndex = index;
            Text = Path.GetFileName(files[index]);
            Previous.Enabled = index > 0;
            Next.Enabled = index < files.Length - 1;
            UpdateCaption();
            return true;
        }

        /// <summary>Delegate deletion to the confirmation callback and select a remaining attachment on success.</summary>
        internal bool DeleteCurrent()
        {
            if (deleteImage == null || CurrentIndex < 0 || CurrentIndex >= files.Length || !deleteImage(files[CurrentIndex])) return false;
            files = files.Where(File.Exists).ToArray();
            if (files.Length == 0) Close();
            else
            {
                CurrentIndex = Math.Min(CurrentIndex, files.Length - 1);
                Text = Path.GetFileName(files[CurrentIndex]);
                Previous.Enabled = CurrentIndex > 0;
                Next.Enabled = CurrentIndex < files.Length - 1;
                Canvas.ClearImage();
                if (!ShowAt(CurrentIndex)) UpdateCaption();
            }
            return true;
        }

        /// <summary>Delegate renaming and apply the returned path; null means cancellation or failure.</summary>
        /// <remarks>The bitmap is detached from its file, so renaming preserves zoom and pan without reloading.</remarks>
        internal bool RenameCurrent()
        {
            if (renameImage == null || CurrentIndex < 0 || CurrentIndex >= files.Length) return false;
            string next = renameImage(files[CurrentIndex]);
            if (next == null) return false;
            files[CurrentIndex] = next;
            Text = Path.GetFileName(next);
            UpdateCaption();
            return true;
        }

        /// <summary>Apply the dark title bar after the HWND is created.</summary>
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }
        /// <summary>Handle navigation, deletion, F2, scale shortcuts, and Escape.</summary>
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { Close(); return true; }
            if (keys == Keys.Left) { ShowAt(CurrentIndex - 1); return true; }
            if (keys == Keys.Right) { ShowAt(CurrentIndex + 1); return true; }
            if (keys == Keys.Delete) { DeleteCurrent(); return true; }
            if (keys == Keys.F2) { RenameCurrent(); return true; }
            if (keys == Keys.D0 || keys == Keys.NumPad0) { Canvas.Fit(); return true; }
            if (keys == (Keys.Control | Keys.Add) || keys == (Keys.Control | Keys.Oemplus)) { Canvas.ZoomAt(new Point(Canvas.Width / 2, Canvas.Height / 2), 120); return true; }
            if (keys == (Keys.Control | Keys.Subtract) || keys == (Keys.Control | Keys.OemMinus)) { Canvas.ZoomAt(new Point(Canvas.Width / 2, Canvas.Height / 2), -120); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
    }
}
