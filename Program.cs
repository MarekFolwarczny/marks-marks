using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Mark´s Marks")]
[assembly: System.Reflection.AssemblyProduct("Mark´s Marks")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Marek Folwarczny")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ProjektovePoznamky
{
    /// <summary>Minimal Win32 bindings for hotkeys, instance activation, and the title bar.</summary>
    internal static class Native
    {
        /// <summary>Private WM_APP message to open the running editor instance.</summary>
        internal const int ShowMessage = 0x8000 + 41;
        /// <summary>Register a global shortcut for an HWND; false means registration failed.</summary>
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        /// <summary>Release the shortcut owned by this window.</summary>
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        /// <summary>Find a window by class and title to activate an existing instance.</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        /// <summary>Queue a message to the target HWND without waiting for its handler.</summary>
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        /// <summary>Pass an integer attribute to DWM; unsupported attributes return an HRESULT.</summary>
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        /// <summary>Request a dark title bar and application colors after creating the HWND.</summary>
        internal static void DarkTitleBar(IntPtr window)
        {
            int dark = 1;
            DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
            int background = 26 | (29 << 8) | (34 << 16);
            DwmSetWindowAttribute(window, 35, ref background, sizeof(int));
            int foreground = 235 | (237 << 8) | (241 << 16);
            DwmSetWindowAttribute(window, 36, ref foreground, sizeof(int));
        }
    }

    /// <summary>Desktop entry point and user storage preparation.</summary>
    internal static class Program
    {
        internal const string Title = "Mark´s Marks";
        /// <summary>Main window caption derived from the EXE version, also used for instance activation.</summary>
        internal static readonly string WindowTitle = Title + " v" + typeof(Program).Assembly.GetName().Version.ToString(3);
        /// <summary>Run one instance in the current Windows session, load preferences, and start the STA message loop.</summary>
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (Mutex instance = new Mutex(true, @"Local\ProjektovePoznamky", out first))
            {
                if (!first)
                {
                    IntPtr window = Native.FindWindow(null, WindowTitle);
                    if (window != IntPtr.Zero) Native.PostMessage(window, Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try
                {
                    UpdateExistingStartup();
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjektovePoznamky");
                    Ui.SetLanguage(Ui.ReadLanguage(data));
                    StorageLocation settings = new StorageLocation(data);
                    string location = settings.Read();
                    NotesStore store;
                    NotesState state;
                    if (location != null && Directory.Exists(location))
                    {
                        store = new NotesStore(location);
                        if (!store.HasData) throw new IOException(Ui.T("The selected storage is missing its project list. The original folders were preserved:\n") + location);
                        state = store.Load();
                    }
                    else
                    {
                        if (location != null)
                            MessageBox.Show(Ui.T("Storage is unavailable. Connect its drive or choose another folder.\n\n") + location, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        string chosen = ChooseStorageRoot(null, location);
                        if (chosen == null) return;
                        store = PrepareStorage(location == null ? data : null, chosen, out state);
                        settings.Save(chosen);
                    }
                    using (MainForm form = new MainForm(store, state, settings.Save, delegate(string language) { Ui.SaveLanguage(data, language); })) Application.Run(form);
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { instance.ReleaseMutex(); }
            }
        }

        /// <summary>Open existing storage, migrate legacy XML, or initialize storage selected by the user.</summary>
        /// <param name="legacyDirectory">Original storage for a one-time migration, or null.</param>
        /// <param name="directory">Destination root selected by the user.</param>
        /// <param name="state">Loaded or newly prepared model.</param>
        /// <returns>Storage corresponding to the returned model.</returns>
        internal static NotesStore PrepareStorage(string legacyDirectory, string directory, out NotesState state)
        {
            NotesStore store = new NotesStore(directory);
            if (store.HasData) state = store.Load();
            else
            {
                NotesStore legacy = legacyDirectory == null ? null : new NotesStore(legacyDirectory);
                if (legacy != null && legacy.HasData) state = store.CopyFrom(legacy, legacy.Load());
                else { state = store.Load(); store.Save(state); }
            }
            return store;
        }

        /// <summary>Migrate only an exactly matching legacy startup entry to the current EXE filename.</summary>
        private static void UpdateExistingStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    string previous = "\"" + Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ProjektovePoznamky.exe") + "\"";
                    if (key != null && string.Equals(key.GetValue("ProjektovePoznamky") as string, previous, StringComparison.OrdinalIgnoreCase))
                        key.SetValue("ProjektovePoznamky", "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        /// <summary>Show the root folder picker; cancellation returns null without creating data.</summary>
        /// <returns>Absolute selected path, or null on cancellation.</returns>
        internal static string ChooseStorageRoot(Form owner, string current)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = Ui.T("Choose where to store notes. Each project will have its own folder.");
                dialog.SelectedPath = current != null && Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                dialog.ShowNewFolderButton = true;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }
    }

    /// <summary>Coordinate the editor, projects, attachments, and saving over one active NotesStore.</summary>
    internal sealed class MainForm : Form
    {
        internal static readonly Color Ink = Color.FromArgb(235, 237, 241);
        internal static readonly Color Accent = Color.FromArgb(243, 72, 56);
        internal static readonly Color Surface = Color.FromArgb(26, 29, 34);
        private NotesStore store;
        internal NotesState State;
        private readonly Action<string> commitLocation;
        private readonly Action<string> commitLanguage;
        internal readonly ProjectListBox Projects = new ProjectListBox();
        internal readonly NotesTextBox Editor = new NotesTextBox();
        internal readonly TextBox ProjectSearch = new TextBox();
        internal readonly BrandScrollBar ProjectScroll = new BrandScrollBar("Project scrollbar");
        internal readonly BrandScrollBar NoteScroll = new BrandScrollBar("Note scrollbar");
        internal readonly ToolTip tips = new ToolTip();
        internal readonly FlowLayoutPanel Pictures = new FlowLayoutPanel();
        /// <summary>Replaceable clipboard image reader; the caller owns and must dispose the returned Image.</summary>
        internal Func<Image> ReadClipboardImage = delegate { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; };
        /// <summary>Replaceable clipboard file reader so tests do not modify the system clipboard.</summary>
        internal Func<string[]> ReadClipboardFiles = delegate { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : new string[0]; };
        /// <summary>Test error callback replacing MessageBox; null uses the normal dialog.</summary>
        internal Action<string> ErrorReported = null;
        private readonly Label heading = new TwoLineLabel();
        private readonly Label status = new SingleLineLabel();
        private Button renameButton;
        private Button deleteButton;
        private Button exportButton;
        private Button videoButton;
        private bool rebuildingProjects;
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        internal readonly Button LanguageButton = MakeButton("");
        internal readonly ContextMenuStrip LanguageMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem languageTray = new ToolStripMenuItem("Language");
        private readonly FloatingButton floating;
        private readonly Icon appIcon;
        private readonly Image brandLogo;
        private readonly Image brandIcon;
        private readonly Image metal;
        private readonly Image banner;
        private Project selected;
        private bool loading;
        private bool dirty;
        private bool exiting;
        private bool hotKeyRegistered;
        private string saveFailure;

        /// <summary>Create the editor, tray, and overlay, and connect saving after a typing pause.</summary>
        internal MainForm(NotesStore store, NotesState state, Action<string> commitLocation = null, Action<string> commitLanguage = null)
        {
            SuspendLayout();
            this.store = store;
            State = state;
            this.commitLocation = commitLocation;
            this.commitLanguage = commitLanguage;
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            brandLogo = LoadDesignImage("MarksMarks.BrandLogo");
            brandIcon = LoadDesignImage("MarksMarks.BrandIcon");
            metal = LoadDesignImage("MarksMarks.Metal");
            banner = LoadDesignImage("MarksMarks.Header");
            Icon = appIcon;
            Text = Program.WindowTitle;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 640);
            MinimumSize = new Size(760, 500);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = Surface;
            BackgroundImage = metal;
            BackgroundImageLayout = ImageLayout.Stretch;
            ForeColor = Ink;
            TopMost = true;
            BuildLayout();
            StyleMenu(menu);

            saveTimer.Interval = 650;
            saveTimer.Tick += delegate { Flush(); };
            Projects.SelectedIndexChanged += delegate { SelectProject(); };
            Editor.TextChanged += delegate
            {
                if (loading || selected == null) return;
                selected.Text = Editor.Text;
                MarkDirty();
            };
            Editor.TryPasteAttachment = PasteAttachmentFromClipboard;
            RebuildProjects(State.SelectedId);

            menu.Items.Add("Open notes    Ctrl+Alt+N", null, delegate { OpenEditor(); });
            ToolStripMenuItem overlayItem = new ToolStripMenuItem("Floating button") { CheckOnClick = true, Checked = State.OverlayVisible };
            menu.Items.Add(overlayItem);
            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false, Checked = StartsWithWindows() };
            startup.Click += delegate { ToggleStartup(startup); };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Change storage…", null, delegate { ChooseStorage(); });
            menu.Items.Add("Open storage folder", null, delegate
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + this.store.DirectoryPath + "\"");
                }
                catch (Exception error) { ShowError(error.Message); }
            });
            menu.Items.Add("Exit", null, delegate { ExitApplication(); });
            StyleMenu(LanguageMenu);
            LanguageMenu.Items.Add("Czech", null, delegate { ChangeLanguage("cs"); });
            LanguageMenu.Items.Add("English", null, delegate { ChangeLanguage("en"); });
            languageTray.DropDownItems.Add("Czech", null, delegate { ChangeLanguage("cs"); });
            languageTray.DropDownItems.Add("English", null, delegate { ChangeLanguage("en"); });
            menu.Items.Insert(menu.Items.Count - 1, languageTray);

            floating = new FloatingButton(State, brandIcon, delegate { ToggleEditor(); }, delegate { MarkDirty(); Flush(); });
            floating.ContextMenuStrip = menu;
            overlayItem.CheckedChanged += delegate
            {
                State.OverlayVisible = overlayItem.Checked;
                floating.Visible = State.OverlayVisible;
                MarkDirty();
                Flush();
            };
            tray.Icon = appIcon;
            tray.Text = Program.Title + " · Ctrl+Alt+N";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleEditor(); };
            tray.Visible = true;
            Ui.LanguageChanged += ApplyLanguage;
            ApplyLanguage();
            ResumeLayout(true);
        }

        /// <summary>Build themed controls and handlers; delegate disk operations to their event handlers.</summary>
        private void BuildLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Panel sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(16) };
            PictureBox brand = new PictureBox { Image = brandLogo, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Top, Height = 106, BackColor = Color.Transparent, AccessibleName = "Logo Mark´s Marks", Margin = new Padding(0) };
            Button add = MakeButton("New project");
            add.Dock = DockStyle.Fill;
            add.Margin = new Padding(0);
            add.Click += delegate { NewProject(); };
            Button storage = MakeButton("Storage…");
            storage.Dock = DockStyle.Fill;
            storage.Margin = new Padding(0);
            storage.Click += delegate { ChooseStorage(); };
            Button import = MakeButton("Import project…");
            import.Dock = DockStyle.Fill;
            import.Margin = new Padding(0);
            import.Click += delegate { ChooseImport(); };
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 138, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 12, 0, 0), BackColor = Color.Transparent };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 38, 6, 38, 6, 38 }) actions.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            actions.Controls.Add(add, 0, 0);
            actions.Controls.Add(import, 0, 2);
            actions.Controls.Add(storage, 0, 4);
            ProjectSearch.Dock = DockStyle.Fill;
            ProjectSearch.Font = new Font("Segoe UI", 10);
            ProjectSearch.BackColor = Color.FromArgb(18, 21, 25);
            ProjectSearch.ForeColor = Ink;
            ProjectSearch.BorderStyle = BorderStyle.None;
            ProjectSearch.AccessibleName = "Search projects by name";
            ProjectSearch.TextChanged += delegate { RebuildProjects(State.SelectedId); };
            Panel searchArea = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(0, 6, 0, 10), BackColor = Color.Transparent };
            Panel searchBox = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(32, 7, 8, 7), BackColor = ProjectSearch.BackColor, Cursor = Cursors.IBeam };
            searchBox.Paint += delegate(object sender, PaintEventArgs e)
            {
                float scale = searchBox.ClientSize.Height / 34f;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Ink, 1.8f * scale))
                {
                    e.Graphics.DrawEllipse(pen, 8 * scale, 9 * scale, 11 * scale, 11 * scale);
                    e.Graphics.DrawLine(pen, 17 * scale, 18 * scale, 24 * scale, 25 * scale);
                }
            };
            searchBox.MouseDown += delegate { ProjectSearch.Focus(); };
            tips.SetToolTip(ProjectSearch, "Search projects by name");
            tips.SetToolTip(searchBox, "Search projects by name");
            searchBox.Controls.Add(ProjectSearch);
            searchArea.Controls.Add(searchBox);
            Projects.Dock = DockStyle.Fill;
            Projects.BorderStyle = BorderStyle.None;
            Projects.BackColor = Surface;
            Projects.ForeColor = Ink;
            Projects.Font = new Font("Segoe UI", 11);
            Projects.IntegralHeight = false;
            Projects.ItemHeight = 30;
            Projects.AccessibleName = "Projects";
            Projects.DrawMode = DrawMode.OwnerDrawFixed;
            Projects.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Projects.Items.Count) return;
                bool active = (e.State & DrawItemState.Selected) != 0;
                using (SolidBrush brush = new SolidBrush(active ? Color.FromArgb(82, 32, 31) : Projects.BackColor))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, Projects.Items[e.Index].ToString(), Projects.Font,
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height), active ? Color.White : Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            ContextMenuStrip projectMenu = new ContextMenuStrip();
            StyleMenu(projectMenu);
            projectMenu.Items.Add("Rename project    F2", null, delegate { RenameProject(); });
            projectMenu.Items.Add("Export project…", null, delegate { ChooseExport(); });
            projectMenu.Items.Add(new ToolStripSeparator());
            projectMenu.Items.Add("Delete project…", null, delegate { AskDeleteProject(); });
            Projects.ContextMenuStrip = projectMenu;
            Projects.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    int index = Projects.IndexFromPoint(e.Location);
                    if (index >= 0) Projects.SelectedIndex = index;
                }
            };
            Panel projectArea = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
            projectArea.Controls.Add(Projects);
            projectArea.Controls.Add(ProjectScroll);
            ProjectScroll.ValueChanged += delegate(int value) { Projects.TopIndex = value; };
            Projects.ViewChanged += delegate { ProjectScroll.Configure(Projects.Items.Count, Projects.VisibleItems, Projects.Items.Count == 0 ? 0 : Projects.TopIndex); };
            sidebar.Controls.Add(projectArea);
            sidebar.Controls.Add(actions);
            sidebar.Controls.Add(searchArea);
            sidebar.Controls.Add(brand);

            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 16, 18, 12), BackColor = Color.Transparent };
            Panel header = new Panel { Dock = DockStyle.Top, Height = 174, BackgroundImage = banner, BackgroundImageLayout = ImageLayout.Stretch, Padding = new Padding(22, 30, 22, 28) };
            Button rename = new RenameButton { Text = "", Dock = DockStyle.Top, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, AccessibleName = "Rename project", AccessibleDescription = "Rename selected project (F2)" };
            rename.FlatAppearance.BorderColor = Color.FromArgb(139, 47, 40);
            rename.FlatAppearance.MouseOverBackColor = Color.FromArgb(74, 35, 35);
            rename.FlatAppearance.MouseDownBackColor = Color.FromArgb(108, 38, 33);
            tips.SetToolTip(rename, "Rename project · F2");
            rename.Click += delegate { RenameProject(); };
            renameButton = rename;
            deleteButton = new DeleteButton { Text = "", Dock = DockStyle.Top, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, AccessibleName = "Delete project" };
            deleteButton.FlatAppearance.BorderColor = Color.FromArgb(139, 47, 40);
            deleteButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(74, 35, 35);
            deleteButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(108, 38, 33);
            tips.SetToolTip(deleteButton, "Delete project including its note, images and videos");
            deleteButton.Click += delegate { AskDeleteProject(); };
            exportButton = new ExportButton { AccessibleName = "Export project" };
            ApplyButtonTheme(exportButton);
            tips.SetToolTip(exportButton, "Export project including images and videos to ZIP");
            exportButton.Dock = DockStyle.Top;
            exportButton.Height = 32;
            exportButton.Click += delegate { ChooseExport(); };
            videoButton = new VideoButton { AccessibleName = "Add video" };
            ApplyButtonTheme(videoButton);
            videoButton.Dock = DockStyle.Top;
            videoButton.Height = 32;
            tips.SetToolTip(videoButton, "Add video from a file · you can also paste with Ctrl+V");
            videoButton.Click += delegate { ChooseVideos(); };
            heading.Dock = DockStyle.Fill;
            heading.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            heading.AutoEllipsis = true;
            heading.TextAlign = ContentAlignment.TopLeft;
            heading.BackColor = Surface;
            Label hint = new SingleLineLabel { Tag = "Ctrl+V pastes images or videos  ·  Tab adds a subpoint", Dock = DockStyle.Bottom, Height = 25, ForeColor = Color.FromArgb(192, 194, 201), Font = new Font("Segoe UI", 9), AutoEllipsis = true, BackColor = Surface };
            Panel titleRow = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
            Panel renameArea = new Panel { Dock = DockStyle.Right, Width = 40, Padding = new Padding(8, 0, 0, 0), BackColor = Surface };
            renameArea.Controls.Add(rename);
            Panel deleteArea = new Panel { Dock = DockStyle.Right, Width = 40, Padding = new Padding(8, 0, 0, 0), BackColor = Surface };
            deleteArea.Controls.Add(deleteButton);
            Panel exportArea = new Panel { Dock = DockStyle.Right, Width = 40, Padding = new Padding(8, 0, 0, 0), BackColor = Surface };
            exportArea.Controls.Add(exportButton);
            Panel videoArea = new Panel { Dock = DockStyle.Right, Width = 40, Padding = new Padding(8, 0, 0, 0), BackColor = Surface };
            videoArea.Controls.Add(videoButton);
            titleRow.Controls.Add(heading);
            header.Controls.Add(titleRow);
            header.Controls.Add(hint);
            Editor.Multiline = true;
            Editor.AcceptsReturn = true;
            Editor.AcceptsTab = true;
            Editor.ScrollBars = ScrollBars.None;
            Editor.WordWrap = true;
            Editor.MaxLength = int.MaxValue;
            Editor.BorderStyle = BorderStyle.FixedSingle;
            Editor.Font = new Font("Segoe UI", 11);
            Editor.ForeColor = Ink;
            Editor.BackColor = Color.FromArgb(18, 21, 25);
            Editor.Dock = DockStyle.Fill;
            Editor.AccessibleName = "Notes for the selected project";
            status.Dock = DockStyle.Fill;
            status.Height = 28;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Font = new Font("Segoe UI", 9);
            status.ForeColor = Color.FromArgb(187, 191, 199);
            status.BackColor = Surface;
            status.AutoEllipsis = true;
            Pictures.Dock = DockStyle.Bottom;
            Pictures.Height = 116;
            Pictures.AutoScroll = true;
            Pictures.WrapContents = false;
            Pictures.Padding = new Padding(0, 8, 0, 0);
            Pictures.AccessibleName = "Images and videos for the current project";
            Pictures.BackColor = Surface;
            Pictures.Visible = false;
            Panel noteArea = new Panel { Dock = DockStyle.Fill, BackColor = Editor.BackColor };
            noteArea.Controls.Add(Editor);
            noteArea.Controls.Add(NoteScroll);
            NoteScroll.ValueChanged += delegate(int value) { Editor.ScrollToLine(value); };
            Editor.ViewChanged += delegate { NoteScroll.Configure(Editor.VisualLineCount, Editor.VisibleLines, Editor.FirstVisibleLine); };
            body.Controls.Add(noteArea);
            body.Controls.Add(Pictures);
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 6, 0, 0), BackColor = Surface };
            Panel languageArea = new Panel { Dock = DockStyle.Right, Width = 46, Padding = new Padding(8, 0, 0, 0), BackColor = Surface };
            LanguageButton.Tag = null;
            LanguageButton.Dock = DockStyle.Top;
            LanguageButton.Height = 32;
            LanguageButton.AccessibleName = "Language";
            tips.SetToolTip(LanguageButton, "Change language");
            LanguageButton.Click += delegate { LanguageMenu.Show(LanguageButton, Point.Empty, ToolStripDropDownDirection.AboveRight); };
            languageArea.Controls.Add(LanguageButton);
            footer.Controls.Add(status);
            footer.Controls.Add(languageArea);
            footer.Controls.Add(videoArea);
            footer.Controls.Add(exportArea);
            footer.Controls.Add(renameArea);
            footer.Controls.Add(deleteArea);
            body.Controls.Add(footer);
            body.Controls.Add(header);
            layout.Controls.Add(sidebar, 0, 0);
            layout.Controls.Add(body, 1, 0);
            Controls.Add(layout);
        }

        /// <summary>Create a button and retain its static caption key in Tag for language switching.</summary>
        /// <remarks>Clear Tag for user filenames; otherwise the caption is treated as a translation key.</remarks>
        internal static Button MakeButton(string text)
        {
            Button button = new Button { Text = Ui.T(text), Tag = text };
            ApplyButtonTheme(button);
            return button;
        }

        /// <summary>Set readable normal, selected, and pressed button colors.</summary>
        internal static void ApplyButtonTheme(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Surface;
            button.ForeColor = Ink;
            button.FlatAppearance.BorderColor = Color.FromArgb(139, 47, 40);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(74, 35, 35);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(108, 38, 33);
        }

        /// <summary>Load an embedded resource as an independent bitmap detached from its stream.</summary>
        private static Image LoadDesignImage(string name)
        {
            using (Stream resource = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (Image original = Image.FromStream(resource)) return new Bitmap(original);
        }

        /// <summary>Apply dark menu colors without changing its commands.</summary>
        private static void StyleMenu(ContextMenuStrip menu)
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Surface;
            menu.ForeColor = Ink;
        }

        /// <summary>Save the language preference before translating open windows; preserve the active language on failure.</summary>
        /// <returns>False when the preference cannot be saved.</returns>
        internal bool ChangeLanguage(string language)
        {
            if (language != "cs" && language != "en") throw new ArgumentException("Unsupported language.", "language");
            try { if (commitLanguage != null) commitLanguage(language); }
            catch (Exception error)
            {
                ShowError(Ui.T("Could not save the language preference. The previous language remains active.\n\n") + error.Message);
                return false;
            }
            Ui.SetLanguage(language);
            return true;
        }

        /// <summary>Translate controls and refresh the gallery while preserving user project names and note text.</summary>
        private void ApplyLanguage()
        {
            Ui.Apply(this, tips);
            Ui.ApplyMenu(menu.Items);
            Ui.ApplyMenu(LanguageMenu.Items);
            LanguageButton.Text = Ui.English ? "EN" : "CZ";
            for (int i = 0; i < 2; i++)
            {
                ((ToolStripMenuItem)LanguageMenu.Items[i]).Checked = (i == 1) == Ui.English;
                ((ToolStripMenuItem)languageTray.DropDownItems[i]).Checked = (i == 1) == Ui.English;
            }
            ProjectScroll.RefreshLanguage();
            NoteScroll.RefreshLanguage();
            heading.Text = selected == null ? Ui.T(State.Projects.Count == 0 ? "Create a new project" : "No matching project") : selected.Name;
            RefreshSaveStatus();
            RefreshPictures();
        }

        /// <summary>On first display, show the overlay, register the shortcut, and save the loaded state.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (State.OverlayVisible) floating.Show();
            hotKeyRegistered = Native.RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000, (uint)Keys.N);
            if (!hotKeyRegistered)
                tray.ShowBalloonTip(6000, Program.Title, Ui.T("Ctrl+Alt+N is used by another application. Open notes using the floating button or tray icon."), ToolTipIcon.Info);
            if (store.Recovered)
                MessageBox.Show(this, Ui.T("Storage was safely recovered. Any damaged files were preserved in the notes folder."), Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            MarkDirty();
            Flush();
            RefreshPictures();
            Editor.Focus();
        }

        /// <summary>Apply title bar colors after the native window is created.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle);
        }

        /// <summary>Load the selected note without triggering text-change saving, then refresh attachments.</summary>
        private void SelectProject()
        {
            if (rebuildingProjects) return;
            Project next = Projects.SelectedItem as Project;
            selected = next;
            if (next != null) State.SelectedId = next.Id;
            loading = true;
            Editor.Text = next == null ? "" : next.Text;
            loading = false;
            heading.Text = next == null ? Ui.T(State.Projects.Count == 0 ? "Create a new project" : "No matching project") : next.Name;
            Editor.Enabled = renameButton.Enabled = deleteButton.Enabled = exportButton.Enabled = videoButton.Enabled = next != null;
            if (next != null) MarkDirty();
            RefreshPictures();
        }

        /// <summary>Filter names without case/diacritics, sort using cs-CZ, and restore the preferred Id.</summary>
        internal void RebuildProjects(string preferredId)
        {
            CompareInfo comparison = CultureInfo.InvariantCulture.CompareInfo;
            string query = ProjectSearch.Text.Trim();
            Project[] visible = State.Projects.Where(p => comparison.IndexOf(p.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
                .OrderBy(p => p.Name, StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), true)).ThenBy(p => p.Id).ToArray();
            rebuildingProjects = true;
            Projects.BeginUpdate();
            try
            {
                Projects.Items.Clear();
                Projects.Items.AddRange(visible);
                int index = Array.FindIndex(visible, p => p.Id == preferredId);
                Projects.SelectedIndex = index >= 0 ? index : (visible.Length == 0 ? -1 : 0);
            }
            finally { Projects.EndUpdate(); rebuildingProjects = false; }
            SelectProject();
        }

        /// <summary>Add and select a project, then try saving; retain it in memory on failure.</summary>
        internal Project AddProject(string name)
        {
            Project project = new Project { Name = name };
            State.Projects.Add(project);
            ProjectSearch.Clear();
            RebuildProjects(project.Id);
            Flush();
            RefreshPictures();
            return project;
        }

        /// <summary>Request a nonempty name and create the project only after confirmation.</summary>
        private void NewProject()
        {
            string name = NameDialog.Ask(this, "New project", "");
            if (name == null) return;
            AddProject(name);
            Editor.Focus();
        }

        /// <summary>Open the selected project&#x27;s name dialog and apply changes to the model.</summary>
        private void RenameProject()
        {
            if (selected == null) return;
            string name = NameDialog.Ask(this, "Rename project", selected.Name);
            if (name == null) return;
            RenameSelectedProject(name);
            Editor.Focus();
        }

        /// <summary>Update the display name and ordering while preserving its existing folder.</summary>
        internal void RenameSelectedProject(string name)
        {
            if (selected == null || string.IsNullOrWhiteSpace(name)) return;
            Project project = selected;
            project.Name = name.Trim();
            ProjectSearch.Clear();
            RebuildProjects(project.Id);
            MarkDirty();
            Flush();
        }

        /// <summary>Request explicit deletion confirmation with a negative default answer.</summary>
        private void AskDeleteProject()
        {
            if (selected == null) return;
            Project project = selected;
            if (MessageBox.Show(this, Ui.T("Delete project \"{0}\"?\n\nIts folder, note, all images and videos will be removed.", project.Name), Ui.T("Delete project"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { DeleteProject(project); }
            catch (Exception error) { ShowError(error.Message); }
        }

        /// <summary>Save pending edits, delete the project, and rebuild the list even after a cleanup error.</summary>
        internal bool DeleteProject(Project project)
        {
            if (!Flush()) return false;
            try { store.DeleteProject(State, project); }
            finally { RebuildProjects(State.SelectedId); }
            return true;
        }

        /// <summary>Select a folder and confirm before opening unrelated existing storage.</summary>
        private void ChooseStorage()
        {
            string directory = Program.ChooseStorageRoot(this, store.DirectoryPath);
            if (directory == null) return;
            try
            {
                NotesStore destination = new NotesStore(directory);
                if (!string.Equals(destination.DirectoryPath, store.DirectoryPath, StringComparison.OrdinalIgnoreCase) && destination.HasData &&
                    MessageBox.Show(this, Ui.T("This folder already contains projects. Open them?\n\nYour current notes, images and videos will remain in:\n") + store.DirectoryPath,
                        Ui.T("Change storage"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                ChangeStorage(directory);
            }
            catch (Exception error) { ShowError(Ui.T("Could not change storage. The original storage remains active.\n\n") + error.Message); }
        }

        /// <summary>Prepare/save destination storage, commit preferences, then switch the active model.</summary>
        /// <remarks>The active store changes only after the preference commit. A prepared copy can remain if that commit fails.</remarks>
        internal bool ChangeStorage(string directory)
        {
            if (!Flush()) return false;
            NotesStore destination = new NotesStore(directory);
            if (string.Equals(destination.DirectoryPath, store.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return true;
            NotesState next = destination.HasData ? destination.Load() : destination.CopyFrom(store, State);
            next.OverlayX = State.OverlayX;
            next.OverlayY = State.OverlayY;
            next.OverlayVisible = State.OverlayVisible;
            destination.Save(next);
            if (commitLocation != null) commitLocation(destination.DirectoryPath);
            store = destination;
            State = next;
            floating.SetState(State);
            ProjectSearch.Clear();
            RebuildProjects(State.SelectedId);
            Flush();
            Editor.Focus();
            return true;
        }

        /// <summary>Prefer supported video files, then images; false leaves normal native text pasting available.</summary>
        private bool PasteAttachmentFromClipboard()
        {
            try
            {
                string[] videos = ReadClipboardFiles().Where(VideoFormats.Supports).ToArray();
                if (videos.Length != 0)
                {
                    foreach (string file in videos) AttachVideo(file);
                    return true;
                }
                using (Image image = ReadClipboardImage())
                {
                    if (image == null) return false;
                    AttachImage(image);
                }
            }
            catch (Exception error) { ShowError(Ui.T("Could not paste attachment.\n\n") + error.Message); }
            return true;
        }

        /// <summary>Save a PNG and insert a localized reference at the cursor.</summary>
        internal string AttachImage(Image image)
        {
            if (selected == null) throw new InvalidOperationException(Ui.T("Select or create a project first."));
            if (!Flush()) throw new IOException(Ui.T("Save the project first."));
            string file = store.SaveImage(selected, image);
            Editor.SelectedText = "[" + Ui.T("Image") + ": " + Path.GetFileName(file) + "]";
            Flush();
            RefreshPictures();
            return file;
        }

        /// <summary>Select one or more local video files for the current project.</summary>
        private void ChooseVideos()
        {
            if (selected == null) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Title = Ui.T("Add video"), Filter = Ui.T("Video files") + "|" + VideoFormats.Pattern, Multiselect = true, CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { foreach (string file in dialog.FileNames) AttachVideo(file); }
                    catch (Exception error) { ShowError(Ui.T("Could not add video.\n\n") + error.Message); }
        }

        /// <summary>Copy a video into the project, insert its exact reference, and refresh the gallery.</summary>
        internal string AttachVideo(string source)
        {
            if (selected == null) throw new InvalidOperationException(Ui.T("Select or create a project first."));
            if (!Flush()) throw new IOException(Ui.T("Save the project first."));
            UseWaitCursor = true;
            try
            {
                string file = store.SaveVideo(selected, source);
                Editor.SelectedText = "[Video: " + Path.GetFileName(file) + "]";
                Flush();
                RefreshPictures();
                return file;
            }
            finally { UseWaitCursor = false; }
        }

        /// <summary>Dispose old thumbnails and reload image/video tiles on the UI thread.</summary>
        private void RefreshPictures()
        {
            ClearPictures();
            if (selected == null) return;
            try
            {
                float scale;
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) scale = graphics.DpiX / 96f;
                foreach (string file in store.ImageFiles(selected))
                {
                    Button button = MakeButton("");
                    button.Tag = null;
                    button.Text = Path.GetFileNameWithoutExtension(file);
                    button.Size = new Size((int)(116 * scale), (int)(90 * scale));
                    button.TextAlign = ContentAlignment.BottomCenter;
                    button.ImageAlign = ContentAlignment.TopCenter;
                    button.AccessibleName = Ui.T("View {0}", Path.GetFileName(file));
                    button.Click += delegate { ShowImage(file); };
                    ContextMenuStrip imageMenu = new ContextMenuStrip();
                    StyleMenu(imageMenu);
                    imageMenu.Items.Add("View image", null, delegate { ShowImage(file); });
                    imageMenu.Items.Add("Delete image…", null, delegate { AskDeleteImage(file); });
                    imageMenu.Items.Add("Rename image…", null, delegate { AskRenameAttachment(file, false); });
                    Ui.ApplyMenu(imageMenu.Items);
                    button.ContextMenuStrip = imageMenu;
                    tips.SetToolTip(button, Path.GetFileName(file) + "\n" + Ui.T("Right-click to rename or delete this attachment."));
                    try
                    {
                        using (Image image = Image.FromFile(file))
                        {
                            Bitmap thumbnail = new Bitmap((int)(100 * scale), (int)(62 * scale));
                            using (Graphics graphics = Graphics.FromImage(thumbnail))
                            {
                                graphics.Clear(Surface);
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                float factor = Math.Min((float)thumbnail.Width / image.Width, (float)thumbnail.Height / image.Height);
                                float width = image.Width * factor, height = image.Height * factor;
                                graphics.DrawImage(image, (thumbnail.Width - width) / 2, (thumbnail.Height - height) / 2, width, height);
                            }
                            button.Image = thumbnail;
                        }
                    }
                    catch { button.Text = Ui.T("Cannot load") + "\n" + button.Text; }
                    Pictures.Controls.Add(button);
                }
                foreach (string file in store.VideoFiles(selected))
                {
                    Button button = MakeButton("");
                    button.Tag = null;
                    button.Size = new Size((int)(116 * scale), (int)(90 * scale));
                    button.Text = "▶\n" + Path.GetFileNameWithoutExtension(file);
                    button.AccessibleName = Ui.T("Play {0}", Path.GetFileName(file));
                    button.Click += delegate { ShowVideo(file); };
                    ContextMenuStrip videoMenu = new ContextMenuStrip();
                    StyleMenu(videoMenu);
                    videoMenu.Items.Add("Play video", null, delegate { ShowVideo(file); });
                    videoMenu.Items.Add("Delete video…", null, delegate { AskDeleteVideo(file); });
                    videoMenu.Items.Add("Rename video…", null, delegate { AskRenameAttachment(file, true); });
                    Ui.ApplyMenu(videoMenu.Items);
                    button.ContextMenuStrip = videoMenu;
                    tips.SetToolTip(button, Path.GetFileName(file) + "\n" + Ui.T("Right-click to rename or delete this attachment."));
                    Pictures.Controls.Add(button);
                }
                Pictures.Visible = Pictures.Controls.Count != 0;
            }
            catch (Exception error) { status.Text = Ui.T("Cannot load attachments: ") + error.Message; }
        }

        /// <summary>Remove/dispose attachment buttons, their bitmaps, and context menus.</summary>
        private void ClearPictures()
        {
            while (Pictures.Controls.Count != 0)
            {
                Button button = (Button)Pictures.Controls[0];
                Pictures.Controls.Remove(button);
                if (button.Image != null) button.Image.Dispose();
                if (button.ContextMenuStrip != null) button.ContextMenuStrip.Dispose();
                button.Dispose();
            }
        }

        /// <summary>Open a modal image viewer with renaming and confirmed deletion callbacks.</summary>
        private void ShowImage(string file)
        {
            try
            {
                ImageViewer viewer = null;
                using (viewer = new ImageViewer(store.ImageFiles(selected), file, delegate(string current) { return AskDeleteImage(current, viewer); }, delegate(string current) { return AskRenameAttachment(current, false, viewer); }))
                { viewer.Icon = appIcon; viewer.ShowDialog(this); }
            }
            catch (Exception error) { ShowError(Ui.T("Cannot open the image.\n\n") + error.Message); }
        }

        /// <summary>Open a modal local-video viewer with project modification callbacks.</summary>
        private void ShowVideo(string file)
        {
            try
            {
                VideoViewer viewer = null;
                using (viewer = new VideoViewer(store.VideoFiles(selected), file, delegate(string current) { return AskDeleteVideo(current, viewer); }, delegate(string current) { return AskRenameAttachment(current, true, viewer); }))
                { viewer.Icon = appIcon; viewer.ShowDialog(this); }
            }
            catch (Exception error) { ShowError(Ui.T("Cannot open video.\n\n") + error.Message); }
        }

        /// <summary>Confirm video deletion and display operational errors using the correct owner.</summary>
        private bool AskDeleteVideo(string file, Form owner = null)
        {
            if (MessageBox.Show(owner ?? this, Ui.T("Delete video \"{0}\"?\n\nThe file and its references in the note will be removed.", Path.GetFileName(file)), Ui.T("Delete video"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            try { return DeleteVideo(file); }
            catch (Exception error) { ShowError(error.Message, owner); return false; }
        }

        /// <summary>Request a name without extension; cancellation/failure returns null and preserves the original.</summary>
        private string AskRenameAttachment(string file, bool video, Form owner = null)
        {
            string name = NameDialog.Ask(owner ?? this, video ? "Rename video" : "Rename image", Path.GetFileNameWithoutExtension(file), "Attachment name (without extension)");
            if (name == null) return null;
            try { return RenameAttachment(file, name, video); }
            catch (Exception error) { ShowError(Ui.T("Could not rename attachment.\n\n") + error.Message, owner); return null; }
        }

        /// <summary>Rename an owned attachment, update editor references, and refresh the gallery.</summary>
        /// <returns>New full path, or null when no project is selected or the editor cannot be saved.</returns>
        internal string RenameAttachment(string file, string name, bool video)
        {
            if (selected == null || !Flush()) return null;
            string target = video ? store.RenameVideo(State, selected, file, name) : store.RenameImage(State, selected, file, name);
            int start = Editor.SelectionStart, length = Editor.SelectionLength;
            loading = true;
            try { Editor.Text = selected.Text; Editor.Select(Math.Min(start, Editor.TextLength), Math.Min(length, Math.Max(0, Editor.TextLength - start))); }
            finally { loading = false; }
            RefreshPictures();
            RefreshSaveStatus();
            return target;
        }

        /// <summary>Save edits, delete the video and references, then reload the note into the editor.</summary>
        internal bool DeleteVideo(string file)
        {
            if (selected == null || !Flush()) return false;
            store.DeleteVideo(State, selected, file);
            loading = true;
            Editor.Text = selected.Text;
            loading = false;
            RefreshPictures();
            RefreshSaveStatus();
            return true;
        }

        /// <summary>Confirm image deletion with a negative default and report operational errors.</summary>
        private bool AskDeleteImage(string file, Form owner = null)
        {
            if (MessageBox.Show(owner ?? this, Ui.T("Delete image \"{0}\"?\n\nThe file and its references in the note will be removed.", Path.GetFileName(file)), Ui.T("Delete image"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            try { return DeleteImage(file); }
            catch (Exception error) { ShowError(error.Message, owner); return false; }
        }

        /// <summary>Delete the image and its Czech/English references while preserving other project contents.</summary>
        internal bool DeleteImage(string file)
        {
            if (selected == null || !Flush()) return false;
            store.DeleteImage(State, selected, file);
            loading = true;
            Editor.Text = selected.Text;
            loading = false;
            RefreshPictures();
            RefreshSaveStatus();
            return true;
        }

        /// <summary>Choose the destination ZIP with overwrite confirmation and export the current project.</summary>
        private void ChooseExport()
        {
            if (selected == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Title = Ui.T("Export project including images and videos"), Filter = Ui.T("Mark´s Marks project (*.zip)|*.zip"), DefaultExt = "zip", AddExtension = true,
                FileName = new string(selected.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".zip", OverwritePrompt = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { ExportProject(dialog.FileName); }
                    catch (Exception error) { ShowError(Ui.T("Could not export the project.\n\n") + error.Message); }
        }

        /// <summary>Save pending editor changes before creating the current project&#x27;s archive.</summary>
        internal bool ExportProject(string file)
        {
            if (selected == null || !Flush()) return false;
            ProjectArchive.Export(store, selected, file);
            return true;
        }

        /// <summary>Choose a ZIP and report validation or import errors.</summary>
        private void ChooseImport()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = Ui.T("Import project including images and videos"), Filter = Ui.T("Mark´s Marks project (*.zip)|*.zip"), CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { ImportProject(dialog.FileName); }
                    catch (Exception error) { ShowError(Ui.T("Could not import the project.\n\n") + error.Message); }
        }

        /// <summary>Save pending edits, import an independent project, and select it after saving.</summary>
        internal Project ImportProject(string file)
        {
            if (!Flush()) return null;
            Project project = ProjectArchive.Import(store, State, file);
            ProjectSearch.Clear();
            RebuildProjects(project.Id);
            Flush();
            return project;
        }

        /// <summary>Mark the model dirty and restart the 650 ms autosave delay.</summary>
        private void MarkDirty()
        {
            dirty = true;
            status.Text = Ui.T("Saving…");
            status.ForeColor = Color.FromArgb(187, 191, 199);
            saveTimer.Stop();
            saveTimer.Start();
        }

        /// <summary>Display save status and deferred committed cleanup without an error dialog.</summary>
        private void RefreshSaveStatus()
        {
            status.Text = saveFailure != null ? Ui.T("Not saved — press Ctrl+S to retry") : dirty ? Ui.T("Saving…") : Ui.T(store.CleanupPending ? "Saved; file cleanup pending · " : "Saved · ") + store.DirectoryPath;
            status.ForeColor = saveFailure != null ? Color.FromArgb(255, 145, 133) : Color.FromArgb(187, 191, 199);
        }

        /// <summary>Save changed data or retry deferred cleanup synchronously; retain text in memory on failure.</summary>
        /// <returns>True on success or when unchanged, false on a write failure.</returns>
        /// <remarks>False indicates a save failure, not loss of in-memory text. Avoid repeating the same error; Ctrl+S retries saving.</remarks>
        internal bool Flush()
        {
            saveTimer.Stop();
            if (!dirty && !store.CleanupPending) return true;
            try
            {
                store.Save(State);
                dirty = false;
                saveFailure = null;
                RefreshSaveStatus();
                return true;
            }
            catch (Exception error)
            {
                status.Text = Ui.T("Not saved — press Ctrl+S to retry");
                status.ForeColor = Color.FromArgb(255, 145, 133);
                if (saveFailure != error.Message)
                {
                    saveFailure = error.Message;
                    ShowError(Ui.T("Could not save notes. Your text remains in the open application.\n\n") + error.Message);
                }
                return false;
            }
        }

        /// <summary>Show the main window, restore it from minimization, and focus the editor.</summary>
        internal void OpenEditor()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            Editor.Focus();
        }

        /// <summary>Toggle editor visibility from the overlay, tray, or global shortcut.</summary>
        private void ToggleEditor()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideEditor();
            else OpenEditor();
        }

        /// <summary>Hide the window only after pending changes have been saved.</summary>
        internal void HideEditor()
        {
            if (Flush()) Hide();
        }

        /// <summary>Allow actual application exit after saving; restore the editor on failure.</summary>
        internal void ExitApplication()
        {
            if (!Flush()) { OpenEditor(); return; }
            exiting = true;
            Close();
        }

        /// <summary>Distinguish hiding via the close button from exiting, and reject closing after a save failure.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exiting)
            {
                e.Cancel = true;
                HideEditor();
            }
            else if (!Flush()) e.Cancel = true;
            base.OnFormClosing(e);
        }

        /// <summary>Handle editor shortcuts before the focused control consumes them.</summary>
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { HideEditor(); return true; }
            if (keys == (Keys.Control | Keys.S)) { Flush(); return true; }
            if (keys == (Keys.Control | Keys.N)) { NewProject(); return true; }
            if (keys == Keys.F2) { RenameProject(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }

        /// <summary>Handle WM_HOTKEY and the private second-instance activation message.</summary>
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == 1) { ToggleEditor(); return; }
            if (message.Msg == Native.ShowMessage) { OpenEditor(); return; }
            base.WndProc(ref message);
        }

        /// <summary>Check whether HKCU Run points exactly to this EXE; registry failures return false.</summary>
        private static bool StartsWithWindows()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && string.Equals(key.GetValue("ProjektovePoznamky") as string, "\"" + Application.ExecutablePath + "\"", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Toggle startup only for the current user and update the menu afterward.</summary>
        private void ToggleStartup(ToolStripMenuItem item)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (item.Checked) key.DeleteValue("ProjektovePoznamky", false);
                    else key.SetValue("ProjektovePoznamky", "\"" + Application.ExecutablePath + "\"");
                }
                item.Checked = !item.Checked;
            }
            catch (Exception error) { ShowError(Ui.T("Cannot change startup settings.\n\n") + error.Message); }
        }

        /// <summary>Show an error or pass it to the test callback without opening a modal dialog.</summary>
        private void ShowError(string message, Form owner = null)
        {
            if (ErrorReported != null) ErrorReported(message);
            else MessageBox.Show(owner ?? this, message, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>Unsubscribe events and release the hotkey, tray, overlay, menus, and owned graphics.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                Ui.LanguageChanged -= ApplyLanguage;
                if (hotKeyRegistered && IsHandleCreated) Native.UnregisterHotKey(Handle, 1);
                hotKeyRegistered = false;
                ClearPictures();
                saveTimer.Dispose();
                tips.Dispose();
                tray.Visible = false;
                tray.Dispose();
                floating.Dispose();
                brandLogo.Dispose();
                brandIcon.Dispose();
                metal.Dispose();
                banner.Dispose();
                menu.Dispose();
                LanguageMenu.Dispose();
                appIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Layered logo window available without taking focus from other applications.</summary>
    internal sealed class FloatingButton : Form
    {
        private NotesState state;
        private readonly Action open;
        private readonly Action moved;
        private readonly Image logo;
        private Point mouseOrigin;
        private Point windowOrigin;
        private bool dragging;
        private readonly ToolTip tip = new ToolTip();

        /// <summary>Create a nonactivating floating logo and constrain its saved position to an available screen.</summary>
        internal FloatingButton(NotesState state, Image logo, Action open, Action moved)
        {
            this.state = state;
            this.open = open;
            this.moved = moved;
            this.logo = logo;
            Tag = "Mark´s Marks · quick notes";
            AccessibleName = "Open project notes";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = MainForm.Surface;
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                int diameter = (int)Math.Round(54 * graphics.DpiX / 96);
                ClientSize = new Size(diameter, diameter);
            }
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = Clamp(state.OverlayX == -1 && state.OverlayY == -1
                ? new Point(area.Right - Width - 22, area.Top + area.Height / 2)
                : new Point(state.OverlayX, state.OverlayY), Size);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            tip.SetToolTip(this, "Notes · Ctrl+Alt+N\nDrag to move. Right-click to open the menu.");
            Ui.LanguageChanged += ApplyLanguage;
            ApplyLanguage();
        }

        /// <summary>Update the floating logo&#x27;s accessible name and tooltip.</summary>
        private void ApplyLanguage() { Ui.Apply(this, tip); }

        /// <summary>Show the logo without moving focus from the active application.</summary>
        protected override bool ShowWithoutActivation { get { return true; } }
        /// <summary>Redirect position saving to the new model after changing storage.</summary>
        internal void SetState(NotesState state) { this.state = state; }
        /// <summary>Enable nonactivating tool-window and per-pixel alpha styles.</summary>
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return parameters; }
        }

        /// <summary>Constrain the window to the work area nearest the supplied position.</summary>
        internal static Point Clamp(Point point, Size size)
        {
            Rectangle area = Screen.FromPoint(point).WorkingArea;
            return new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - size.Width)), Math.Max(area.Top, Math.Min(point.Y, area.Bottom - size.Height)));
        }

        /// <summary>Upload a per-pixel transparent bitmap into the Windows layered window.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            using (Bitmap bitmap = LayeredOverlay.Render(logo, ClientSize)) LayeredOverlay.Apply(Handle, Location, bitmap);
        }

        /// <summary>Record the start of a click or drag without activating the main editor.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseOrigin = MousePosition;
            windowOrigin = Location;
            dragging = false;
            Capture = true;
        }

        /// <summary>Move the logo within an available screen after exceeding the system drag threshold.</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture || e.Button != MouseButtons.Left) return;
            Point delta = new Point(MousePosition.X - mouseOrigin.X, MousePosition.Y - mouseOrigin.Y);
            if (Math.Abs(delta.X) > SystemInformation.DragSize.Width / 2 || Math.Abs(delta.Y) > SystemInformation.DragSize.Height / 2) dragging = true;
            if (dragging) Location = Clamp(new Point(windowOrigin.X + delta.X, windowOrigin.Y + delta.Y), Size);
        }

        /// <summary>Distinguish an editor-opening click from a completed position-saving drag.</summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
            if (dragging)
            {
                state.OverlayX = Left;
                state.OverlayY = Top;
                moved();
            }
            else open();
        }

        /// <summary>Unsubscribe language changes and dispose the owned tooltip; the shared logo is not owned here.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Ui.LanguageChanged -= ApplyLanguage; tip.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Readable single-line caption using ellipsis for long paths or names.</summary>
    internal sealed class SingleLineLabel : Label
    {
        /// <summary>Draw one ellipsized line without treating ampersands as shortcut markers.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (TextAlign == ContentAlignment.MiddleLeft) flags |= TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
        }
    }

    /// <summary>Two-line project heading using ellipsis on overflow.</summary>
    internal sealed class TwoLineLabel : Label
    {
        /// <summary>Wrap into at most two measured lines and ellipsize any overflow.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            bounds.Height = Math.Min(bounds.Height, TextRenderer.MeasureText(e.Graphics, "Ag", Font, Size.Empty, TextFormatFlags.SingleLine).Height * 2);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }
    }

    /// <summary>Standard menu colors matching the application&#x27;s dark theme.</summary>
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color ToolStripDropDownBackground { get { return MainForm.Surface; } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color ImageMarginGradientBegin { get { return MainForm.Surface; } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color ImageMarginGradientMiddle { get { return MainForm.Surface; } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color ImageMarginGradientEnd { get { return MainForm.Surface; } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color MenuItemSelected { get { return Color.FromArgb(74, 35, 35); } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color MenuItemBorder { get { return MainForm.Accent; } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color SeparatorDark { get { return Color.FromArgb(70, 73, 80); } }
        /// <summary>Menu color attribute from the application theme.</summary>
        public override Color SeparatorLight { get { return MainForm.Surface; } }
    }

    /// <summary>Shared modal name dialog for projects and attachments.</summary>
    internal sealed class NameDialog : Form
    {
        /// <summary>Request a nonempty name up to 100 characters with a localized field label; cancellation returns null.</summary>
        /// <param name="owner">Owner of the modal dialog.</param>
        /// <param name="title">Dialog title translation key.</param>
        /// <param name="initial">Initial user-defined name.</param>
        /// <param name="fieldLabel">Field caption key; attachments expect a name without its extension.</param>
        /// <returns>Trimmed nonempty name, or null on cancellation.</returns>
        internal static string Ask(Form owner, string title, string initial, string fieldLabel = "Project name")
        {
            using (NameDialog dialog = new NameDialog())
            {
                dialog.SuspendLayout();
                dialog.Tag = title;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ShowInTaskbar = false;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.TopMost = true;
                dialog.Icon = owner.Icon;
                dialog.BackColor = MainForm.Surface;
                dialog.ForeColor = Color.FromArgb(235, 237, 241);
                dialog.ClientSize = new Size(360, 140);
                dialog.Font = owner.Font;
                dialog.AutoScaleDimensions = new SizeF(96, 96);
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                Label label = new Label { Tag = fieldLabel, Location = new Point(20, 16), AutoSize = true };
                TextBox name = new TextBox { Text = initial, MaxLength = 100, Location = new Point(20, 42), Width = 320, AccessibleName = fieldLabel, BackColor = MainForm.Surface, ForeColor = dialog.ForeColor };
                Button ok = MainForm.MakeButton("Save");
                ok.Location = new Point(160, 90); ok.Width = 85; ok.DialogResult = DialogResult.OK; ok.Enabled = !string.IsNullOrWhiteSpace(initial);
                Button cancel = MainForm.MakeButton("Cancel");
                cancel.Location = new Point(255, 90); cancel.Width = 85; cancel.DialogResult = DialogResult.Cancel;
                name.TextChanged += delegate { ok.Enabled = !string.IsNullOrWhiteSpace(name.Text); };
                dialog.Controls.AddRange(new Control[] { label, name, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += delegate { Native.DarkTitleBar(dialog.Handle); name.Focus(); name.SelectAll(); };
                Action translate = delegate { Ui.Apply(dialog); };
                translate();
                dialog.ResumeLayout(true);
                Ui.LanguageChanged += translate;
                try { return dialog.ShowDialog(owner) == DialogResult.OK ? name.Text.Trim() : null; }
                finally { Ui.LanguageChanged -= translate; }
            }
        }
    }
}
