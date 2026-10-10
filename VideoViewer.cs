using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ProjektovePoznamky
{
    /// <summary>Host WPF MediaElement inside WinForms using Windows system codecs.</summary>
    internal sealed class VideoViewer : Form
    {
        private readonly System.Windows.Controls.MediaElement player = new System.Windows.Controls.MediaElement();
        private readonly System.Windows.Controls.Grid surface = new System.Windows.Controls.Grid();
        private readonly System.Windows.Media.ScaleTransform zoom = new System.Windows.Media.ScaleTransform(1, 1);
        private readonly System.Windows.Media.TranslateTransform pan = new System.Windows.Media.TranslateTransform();
        private readonly ElementHost host = new ElementHost { Dock = DockStyle.Fill, BackColor = Color.Black };
        private readonly Label caption = new SingleLineLabel();
        private readonly VideoSeekBar seek = new VideoSeekBar();
        private readonly Button play = MainForm.MakeButton("");
        private readonly Button sound = MainForm.MakeButton("");
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly Func<string, bool> deleteVideo;
        private readonly Func<string, string> renameVideo;
        private string[] files;
        private bool playing;
        private string failure;
        private TimeSpan pendingPosition;
        private bool pendingPlay = true;
        private System.Windows.Point mouse;
        internal readonly Button Previous = MainForm.MakeButton("‹");
        internal readonly Button Next = MainForm.MakeButton("›");
        internal readonly Button Delete = new DeleteButton();
        /// <summary>Small rename pencil; disabled when no rename callback is supplied.</summary>
        internal readonly Button Rename = new RenameButton();
        /// <summary>Index of the selected path in the viewer&#x27;s local list.</summary>
        internal int CurrentIndex { get; private set; }
        /// <summary>True only after MediaOpened; position and duration are zero until then.</summary>
        internal bool Ready { get; private set; }
        /// <summary>Opened video duration in seconds, or zero when unavailable.</summary>
        internal double Duration { get { return Ready && player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0; } }
        /// <summary>Current playback position in seconds.</summary>
        internal double Position { get { return Ready ? player.Position.TotalSeconds : 0; } }
        /// <summary>Requested playback state managed by the viewer.</summary>
        internal bool Playing { get { return playing; } }
        /// <summary>Scale relative to fitting the entire video into the viewport.</summary>
        internal double Zoom { get { return zoom.ScaleX; } }
        /// <summary>Replaceable error handler for isolated tests without modal dialogs.</summary>
        internal Action<string> ErrorReported = null;

        /// <summary>Open an independent video viewer over a nonempty file list.</summary>
        /// <param name="files">Nonempty local paths in the desired order.</param>
        /// <param name="initial">Initial path; use the first file if it is absent.</param>
        /// <param name="deleteVideo">Confirmation and deletion callback; null disables deletion.</param>
        /// <param name="renameVideo">Return the new path or null on cancellation/failure; null disables renaming.</param>
        internal VideoViewer(string[] files, string initial, Func<string, bool> deleteVideo, Func<string, string> renameVideo = null)
        {
            if (files.Length == 0) throw new InvalidOperationException(Ui.T("The project has no videos."));
            this.files = files;
            this.deleteVideo = deleteVideo;
            this.renameVideo = renameVideo;
            SuspendLayout();
            Size = new Size(900, 680);
            MinimumSize = new Size(640, 400);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            ForeColor = MainForm.Ink;
            BackColor = MainForm.Surface;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;
            player.LoadedBehavior = System.Windows.Controls.MediaState.Manual;
            player.UnloadedBehavior = System.Windows.Controls.MediaState.Close;
            player.ScrubbingEnabled = true;
            player.Stretch = System.Windows.Media.Stretch.Uniform;
            player.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            System.Windows.Media.TransformGroup transform = new System.Windows.Media.TransformGroup();
            transform.Children.Add(zoom); transform.Children.Add(pan); player.RenderTransform = transform;
            surface.Background = System.Windows.Media.Brushes.Black;
            surface.ClipToBounds = true;
            surface.Children.Add(player);
            host.Child = surface;
            player.Cursor = System.Windows.Input.Cursors.Hand;
            player.MediaOpened += delegate
            {
                if (IsDisposed) return;
                Ready = true; failure = null;
                player.Position = pendingPosition;
                SetPlaying(pendingPlay);
                timer.Start(); UpdateCaption();
            };
            player.MediaEnded += delegate { SetPlaying(false); UpdateCaption(); };
            player.MediaFailed += delegate(object sender, System.Windows.ExceptionRoutedEventArgs e)
            {
                if (IsDisposed) return;
                Ready = false; playing = false; timer.Stop();
                failure = Ui.T("Cannot play video. The format or codec may not be supported by Windows.\n\n") + e.ErrorException.Message;
                UpdateCaption(); ShowError(failure);
            };
            player.PreviewMouseWheel += delegate(object sender, System.Windows.Input.MouseWheelEventArgs e) { SetZoom(Zoom * Math.Pow(1.15, e.Delta / 120d)); e.Handled = true; };
            player.MouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) { mouse = e.GetPosition(surface); player.CaptureMouse(); e.Handled = true; };
            player.MouseMove += delegate(object sender, System.Windows.Input.MouseEventArgs e)
            {
                if (!player.IsMouseCaptured) return;
                System.Windows.Point next = e.GetPosition(surface);
                pan.X += next.X - mouse.X; pan.Y += next.Y - mouse.Y; mouse = next; ClampPan();
            };
            player.MouseLeftButtonUp += delegate { player.ReleaseMouseCapture(); };
            surface.SizeChanged += delegate { ClampPan(); };
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(8), BackColor = MainForm.Surface, ColumnCount = 1, RowCount = 3 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            seek.Dock = DockStyle.Fill; seek.Margin = Padding.Empty;
            seek.Seek += SeekTo;
            caption.Dock = DockStyle.Fill; caption.Margin = Padding.Empty; caption.TextAlign = ContentAlignment.MiddleLeft; caption.BackColor = MainForm.Surface; caption.ForeColor = MainForm.Ink;
            Panel buttons = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = MainForm.Surface, Padding = new Padding(0, 4, 0, 0) };
            Previous.AccessibleName = "Previous video"; Next.AccessibleName = "Next video";
            Previous.Dock = Next.Dock = DockStyle.Left; Previous.Width = Next.Width = 34;
            Previous.Click += delegate { ShowAt(CurrentIndex - 1); }; Next.Click += delegate { ShowAt(CurrentIndex + 1); };
            play.Tag = sound.Tag = null;
            play.Dock = sound.Dock = DockStyle.Left; play.Width = 110; sound.Width = 110;
            play.Click += delegate { SetPlaying(!playing); }; sound.Click += delegate { player.IsMuted = !player.IsMuted; UpdateCaption(); };
            Button fit = MainForm.MakeButton("Fit"); fit.Dock = DockStyle.Left; fit.Width = 100; fit.Click += delegate { SetZoom(1); };
            MainForm.ApplyButtonTheme(Delete); Delete.Dock = DockStyle.Right; Delete.Width = 34; Delete.Enabled = deleteVideo != null; Delete.AccessibleName = "Delete video";
            Delete.Click += delegate { DeleteCurrent(); };
            MainForm.ApplyButtonTheme(Rename); Rename.Dock = DockStyle.Right; Rename.Width = 34;
            Rename.Enabled = renameVideo != null; Rename.AccessibleName = "Rename video";
            Rename.Click += delegate { RenameCurrent(); };
            buttons.Controls.Add(fit); buttons.Controls.Add(sound); buttons.Controls.Add(play); buttons.Controls.Add(Next); buttons.Controls.Add(Previous); buttons.Controls.Add(Delete);
            buttons.Controls.Add(Rename);
            footer.Controls.Add(seek, 0, 0); footer.Controls.Add(caption, 0, 1); footer.Controls.Add(buttons, 0, 2);
            Controls.Add(host); Controls.Add(footer);
            timer.Tick += delegate { UpdateCaption(); };
            Ui.LanguageChanged += ApplyLanguage;
            ApplyLanguage();
            int start = Array.FindIndex(files, f => string.Equals(f, initial, StringComparison.OrdinalIgnoreCase));
            ShowAt(Math.Max(0, start));
            ResumeLayout(true);
        }

        /// <summary>Translate the open viewer&#x27;s controls without changing attachment names.</summary>
        private void ApplyLanguage()
        {
            Ui.Apply(this);
            System.Windows.Automation.AutomationProperties.SetName(player, Ui.T("Video; use the mouse wheel to zoom and drag to pan"));
            UpdateCaption();
        }

        /// <summary>Update information about the selected attachment and display state.</summary>
        private void UpdateCaption()
        {
            if (IsDisposed) return;
            play.Text = Ui.T(playing ? "Pause" : "Play"); play.Enabled = Ready;
            sound.Text = Ui.T(player.IsMuted ? "Unmute" : "Mute");
            caption.Text = failure != null ? Ui.T("Cannot play video") : (CurrentIndex + 1) + " / " + files.Length + "  ·  " + TimeSpan.FromSeconds(Position).ToString(@"hh\:mm\:ss") + " / " + TimeSpan.FromSeconds(Duration).ToString(@"hh\:mm\:ss") + "  ·  " + Math.Round(Zoom * 100) + " %";
            seek.SetProgress(Duration, Position);
        }

        /// <summary>Toggle manual playback or pause only after MediaOpened.</summary>
        internal void SetPlaying(bool value)
        {
            if (!Ready) return;
            playing = value;
            if (value) player.Play(); else player.Pause();
            UpdateCaption();
        }

        /// <summary>Clamp the requested time to the opened duration and update the label and timeline.</summary>
        internal void SeekTo(double seconds)
        {
            if (!Ready || Duration <= 0) return;
            player.Position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(Duration, seconds)));
            UpdateCaption();
        }

        /// <summary>Clamp zoom to 25-400 percent and center the image when returning to 100 percent.</summary>
        internal void SetZoom(double value)
        {
            zoom.ScaleX = zoom.ScaleY = Math.Max(.25, Math.Min(4, value));
            if (Zoom == 1) pan.X = pan.Y = 0;
            ClampPan(); UpdateCaption();
        }

        /// <summary>Constrain the enlarged video&#x27;s offset to the viewport edges.</summary>
        private void ClampPan()
        {
            double x = Math.Max(0, surface.ActualWidth * (Zoom - 1) / 2), y = Math.Max(0, surface.ActualHeight * (Zoom - 1) / 2);
            pan.X = Math.Max(-x, Math.Min(x, pan.X)); pan.Y = Math.Max(-y, Math.Min(y, pan.Y));
        }

        /// <summary>Stop the update timer and close MediaElement to release its Source before changing files.</summary>
        private void ReleaseVideo()
        {
            timer.Stop(); Ready = false; playing = false;
            player.Close(); player.Source = null;
        }

        /// <summary>Open the indexed file; return false for invalid indices or loading errors.</summary>
        internal bool ShowAt(int index)
        {
            if (index < 0 || index >= files.Length) return false;
            try { VideoFormats.Validate(files[index]); }
            catch (Exception error) { ShowError(Ui.T("Cannot open video.\n\n") + error.Message); return false; }
            ReleaseVideo();
            CurrentIndex = index; failure = null; pendingPosition = TimeSpan.Zero; pendingPlay = true;
            Text = Path.GetFileName(files[index]);
            Previous.Enabled = index > 0; Next.Enabled = index < files.Length - 1;
            SetZoom(1);
            player.Source = new Uri(Path.GetFullPath(files[index]), UriKind.Absolute);
            player.Play();
            UpdateCaption();
            return true;
        }

        /// <summary>Delegate deletion to the confirmation callback and select a remaining attachment on success.</summary>
        internal bool DeleteCurrent()
        {
            if (deleteVideo == null || CurrentIndex < 0 || CurrentIndex >= files.Length) return false;
            double position = Position; bool wasPlaying = playing;
            ReleaseVideo();
            if (!deleteVideo(files[CurrentIndex]))
            {
                ShowAt(CurrentIndex); pendingPosition = TimeSpan.FromSeconds(position); pendingPlay = wasPlaying;
                return false;
            }
            files = files.Where(File.Exists).ToArray();
            if (files.Length == 0) Close();
            else
            {
                CurrentIndex = Math.Min(CurrentIndex, files.Length - 1);
                Text = Path.GetFileName(files[CurrentIndex]);
                Previous.Enabled = CurrentIndex > 0;
                Next.Enabled = CurrentIndex < files.Length - 1;
                if (!ShowAt(CurrentIndex)) { failure = Ui.T("Cannot play video"); UpdateCaption(); }
            }
            return true;
        }

        /// <summary>Delegate renaming and apply the returned path; null means cancellation or failure.</summary>
        /// <remarks>Close the video before the dialog, reopen afterward, and restore position, pause, zoom, and pan.</remarks>
        internal bool RenameCurrent()
        {
            if (renameVideo == null || CurrentIndex < 0 || CurrentIndex >= files.Length) return false;
            double position = Position, scale = Zoom, x = pan.X, y = pan.Y; bool wasPlaying = playing;
            ReleaseVideo();
            string next = renameVideo(files[CurrentIndex]);
            if (next != null) files[CurrentIndex] = next;
            if (ShowAt(CurrentIndex))
            {
                pendingPosition = TimeSpan.FromSeconds(position); pendingPlay = wasPlaying;
                SetZoom(scale); pan.X = x; pan.Y = y; ClampPan();
            }
            return next != null;
        }

        /// <summary>Show decoder errors or pass them to the test callback.</summary>
        private void ShowError(string text)
        {
            if (ErrorReported != null) ErrorReported(text);
            else MessageBox.Show(this, text, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>Apply the dark title bar after the HWND is created.</summary>
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }
        /// <summary>Handle navigation, deletion, F2, scale shortcuts, and Escape.</summary>
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { Close(); return true; }
            if (keys == Keys.Space) { SetPlaying(!playing); return true; }
            if (keys == Keys.Left) { if (seek.Focused) SeekTo(Position - 5); else ShowAt(CurrentIndex - 1); return true; }
            if (keys == Keys.Right) { if (seek.Focused) SeekTo(Position + 5); else ShowAt(CurrentIndex + 1); return true; }
            if (keys == Keys.Delete) { DeleteCurrent(); return true; }
            if (keys == Keys.F2) { RenameCurrent(); return true; }
            if (keys == Keys.D0 || keys == Keys.NumPad0) { SetZoom(1); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }

        /// <summary>Unsubscribe from language events and release viewer resources.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) { Ui.LanguageChanged -= ApplyLanguage; ReleaseVideo(); timer.Dispose(); host.Child = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>Drawn horizontal playback timeline measured in seconds.</summary>
    internal sealed class VideoSeekBar : Control
    {
        /// <summary>User-requested playback position in seconds.</summary>
        internal event Action<double> Seek;
        private double duration, position;
        /// <summary>Create a focusable timeline supporting mouse capture and keyboard seeking.</summary>
        internal VideoSeekBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = MainForm.Surface; Cursor = Cursors.Hand; TabStop = true; AccessibleName = "Playback position";
        }
        /// <summary>Update duration and position for drawing without seeking the player.</summary>
        internal void SetProgress(double total, double current) { duration = total; position = current; Invalidate(); }
        /// <summary>Convert the X coordinate into a valid time and raise Seek.</summary>
        private void MoveTo(int x)
        {
            if (duration <= 0 || Seek == null) return;
            int margin = Math.Max(6, Height / 3);
            Seek(duration * Math.Max(0, Math.Min(1, (x - margin) / (double)Math.Max(1, Width - margin * 2))));
        }
        /// <summary>Focus the timeline, start dragging, and request a position change.</summary>
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Capture = true; MoveTo(e.X); } }
        /// <summary>Continuously request seeking while the mouse is captured.</summary>
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) MoveTo(e.X); }
        /// <summary>End dragging by releasing mouse capture.</summary>
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) Capture = false; }
        /// <summary>Draw a dark track, red played section, and metallic thumb.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int margin = Math.Max(6, Height / 3), center = Height / 2;
            int x = margin + (int)((Width - margin * 2) * (duration <= 0 ? 0 : Math.Max(0, Math.Min(1, position / duration))));
            using (Pen track = new Pen(Color.FromArgb(70, 73, 80), 3)) e.Graphics.DrawLine(track, margin, center, Width - margin, center);
            using (Pen active = new Pen(MainForm.Accent, 3)) e.Graphics.DrawLine(active, margin, center, x, center);
            using (GraphicsPath thumb = new GraphicsPath())
            {
                thumb.AddPolygon(new Point[] { new Point(x - margin, 4), new Point(x, 1), new Point(x + margin, 4), new Point(x + margin, Height - 4), new Point(x, Height - 1), new Point(x - margin, Height - 4) });
                using (LinearGradientBrush metal = new LinearGradientBrush(new Rectangle(x - margin, 0, margin * 2, Height), Color.FromArgb(148, 154, 163), Color.FromArgb(44, 49, 58), LinearGradientMode.Horizontal)) e.Graphics.FillPath(metal, thumb);
                using (Pen edge = new Pen(MainForm.Accent)) e.Graphics.DrawPath(edge, thumb);
                using (Pen grip = new Pen(MainForm.Ink)) e.Graphics.DrawLine(grip, x, 5, x, Height - 5);
            }
        }
    }
}
