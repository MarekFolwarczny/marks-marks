using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ProjektovePoznamky;

/// <summary>Regression checks over isolated files and native Windows UI controls.</summary>
internal static partial class Tests
{
    private static int checks;
    /// <summary>Evaluate an assertion and stop with the specific failure description.</summary>
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }

    /// <summary>Run regression suites in an isolated folder on an STA thread; return nonzero on failure.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        string root = args[0];
        Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Ui.SetLanguage("cs");
            string suite = args.Length > 1 ? args[1] : "All";
            if (suite == "--attachments-only") suite = "Attachments";
            switch (suite)
            {
                case "Storage": TestStorage(Path.Combine(root, "storage")); break;
                case "Legacy": TestLegacyAndLocation(Path.Combine(root, "legacy")); break;
                case "Window": TestWindow(Path.Combine(root, "window"), Path.Combine(root, "preview.png")); break;
                case "Design": TestDesign(Path.Combine(root, "design"), Path.Combine(root, "overflow.png")); break;
                case "Actions": TestProjectActions(Path.Combine(root, "actions")); break;
                case "Viewer": TestViewer(Path.Combine(root, "viewer"), Path.Combine(root, "viewer.png")); break;
                case "Archive": TestArchive(Path.Combine(root, "archive")); break;
                case "Languages": TestLanguages(Path.Combine(root, "languages"), Path.Combine(root, "english.png")); break;
                case "Videos": TestVideos(Path.Combine(root, "videos")); break;
                case "Attachments": TestAttachmentNames(Path.Combine(root, "attachment-names")); break;
                case "Regression": TestRegressions(Path.Combine(root, "regression")); break;
                default: throw new ArgumentException("Select a test suite through test.ps1.");
            }
            Console.WriteLine("All " + checks + " checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    /// <summary>Write a RIFF chunk with its length and odd-byte padding for a test AVI.</summary>
    private static void AviChunk(BinaryWriter writer, string tag, Action<BinaryWriter> write)
    {
        using (MemoryStream data = new MemoryStream())
        {
            using (BinaryWriter body = new BinaryWriter(data, System.Text.Encoding.ASCII, true)) write(body);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(tag)); writer.Write((uint)data.Length);
            writer.Write(data.ToArray()); if (data.Length % 2 != 0) writer.Write((byte)0);
        }
    }

    /// <summary>Generate a short uncompressed AVI without downloads or an external encoder.</summary>
    private static void MakeVideo(string file)
    {
        // Uncompressed AVI uses the Windows decoder without any downloaded test dependency.
        const int width = 64, height = 48, frames = 50, bytes = width * height * 3;
        using (BinaryWriter output = new BinaryWriter(File.Create(file)))
        AviChunk(output, "RIFF", delegate(BinaryWriter riff)
        {
            riff.Write(System.Text.Encoding.ASCII.GetBytes("AVI "));
            AviChunk(riff, "LIST", delegate(BinaryWriter header)
            {
                header.Write(System.Text.Encoding.ASCII.GetBytes("hdrl"));
                AviChunk(header, "avih", delegate(BinaryWriter avih)
                {
                    foreach (int value in new[] { 100000, bytes * 10, 0, 16, frames, 0, 1, bytes, width, height, 0, 0, 0, 0 }) avih.Write(value);
                });
                AviChunk(header, "LIST", delegate(BinaryWriter stream)
                {
                    stream.Write(System.Text.Encoding.ASCII.GetBytes("strl"));
                    AviChunk(stream, "strh", delegate(BinaryWriter strh)
                    {
                        strh.Write(System.Text.Encoding.ASCII.GetBytes("vidsDIB "));
                        strh.Write(0); strh.Write((short)0); strh.Write((short)0);
                        foreach (int value in new[] { 0, 1, 10, 0, frames, bytes, -1, 0 }) strh.Write(value);
                        foreach (short value in new short[] { 0, 0, width, height }) strh.Write(value);
                    });
                    AviChunk(stream, "strf", delegate(BinaryWriter strf)
                    {
                        strf.Write(40); strf.Write(width); strf.Write(height); strf.Write((short)1); strf.Write((short)24);
                        foreach (int value in new[] { 0, bytes, 0, 0, 0, 0 }) strf.Write(value);
                    });
                });
            });
            AviChunk(riff, "LIST", delegate(BinaryWriter movie)
            {
                movie.Write(System.Text.Encoding.ASCII.GetBytes("movi"));
                for (int frame = 0; frame < frames; frame++)
                {
                    int number = frame;
                    AviChunk(movie, "00db", delegate(BinaryWriter image)
                    {
                        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                        { image.Write((byte)(x * 4)); image.Write((byte)(y * 5)); image.Write((byte)(number * 5)); }
                    });
                }
            });
            AviChunk(riff, "idx1", delegate(BinaryWriter index)
            {
                for (int frame = 0; frame < frames; frame++)
                { index.Write(System.Text.Encoding.ASCII.GetBytes("00db")); index.Write(16); index.Write(4 + frame * (bytes + 8)); index.Write(bytes); }
            });
        });
    }

    /// <summary>Pump UI events until an asynchronous condition succeeds or its timeout expires.</summary>
    private static bool PumpUntil(Func<bool> done, int timeout)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(20); }
        Application.DoEvents(); return done();
    }

    /// <summary>Verify attachment insertion, persistence, deletion, ZIP format 2, native playback, and recovery.</summary>
    private static void TestVideos(string directory)
    {
        Directory.CreateDirectory(directory); Ui.SetLanguage("en");
        string original = Path.Combine(directory, "original.AVI"); MakeVideo(original);
        Check(VideoFormats.Supports(original) && !VideoFormats.Supports("film.exe"), "Video formats accept case-insensitive supported extensions only");
        VideoFormats.Validate(original);
        NotesStore store = new NotesStore(Path.Combine(directory, "source")); NotesState state = store.Load(); state.OverlayVisible = false;
        Project project = state.Projects[0]; string first, second;
        using (MainForm form = new MainForm(store, state, delegate { }))
        {
            form.ErrorReported = delegate(string error) { throw new Exception(error); };
            form.Show(); Application.DoEvents();
            form.Editor.Text = "- Video here: \r\n- Another point"; form.Editor.SelectionStart = 14;
            first = form.AttachVideo(original);
            Check(File.Exists(original) && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(original)) && Path.GetDirectoryName(first).EndsWith("videa"),
                "Adding a video copies its exact bytes into the project and retains the source file");
            Check(form.Editor.Text == "- Video here: [Video: video-001.avi]\r\n- Another point", "Video insertion associates the file with the point at the caret");
            form.ReadClipboardFiles = delegate { return new[] { original, original }; };
            form.ReadClipboardImage = delegate { throw new Exception("A video clipboard must not request an image."); };
            form.Editor.SelectionStart = form.Editor.TextLength; form.Editor.Paste();
            second = store.VideoFiles(project)[1];
            Check(store.VideoFiles(project).Length == 3 && form.Editor.Text.Contains("[Video: video-002.avi]") && form.Editor.Text.Contains("[Video: video-003.avi]"), "Ctrl+V inserts every copied video file with distinct filenames and note references");
            Button tile = (Button)form.Pictures.Controls[0];
            Check(form.Pictures.Controls.Count == 3 && tile.ContextMenuStrip.Items[0].Text == "Play video" && tile.ContextMenuStrip.Items[1].Text == "Delete video…",
                "Video tiles offer playback and deletion in English");
            Ui.SetLanguage("cs"); Check(((Button)form.Pictures.Controls[0]).ContextMenuStrip.Items[0].Text == "Přehrát video", "Video tile actions update when the language changes"); Ui.SetLanguage("en");
            string bad = Path.Combine(directory, "broken.mp4"); File.WriteAllBytes(bad, new byte[40]); bool refused = false;
            string note = form.Editor.Text;
            try { form.AttachVideo(bad); } catch (InvalidDataException) { refused = true; }
            Check(refused && store.VideoFiles(project).Length == 3 && form.Editor.Text == note && Directory.GetFiles(Path.GetDirectoryName(first), "*.tmp").Length == 0,
                "A damaged video leaves the project and note unchanged with no temporary file");
            refused = false;
            using (FileStream locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
                try { form.DeleteVideo(first); } catch (IOException) { refused = true; }
            Check(refused && File.Exists(first) && form.Editor.Text == note && store.Load().Projects[0].Text == note, "A locked video cannot be deleted and retains all note references");
            refused = false; try { form.DeleteVideo(original); } catch (InvalidOperationException) { refused = true; }
            Check(refused && File.Exists(original) && form.Editor.Text == note, "Video deletion refuses a file outside the selected project");
            using (Bitmap image = new Bitmap(20, 20)) form.AttachImage(image);
            Check(form.Pictures.Controls.Count == 4, "Images and videos coexist in the same project gallery");
            Check(form.ExportProject(Path.Combine(directory, "team.zip")), "The app exports a project containing both images and videos");
            form.ExitApplication();
        }
        string package = Path.Combine(directory, "team.zip");
        using (ZipArchive zip = ZipFile.OpenRead(package))
        using (StreamReader metadata = new StreamReader(zip.GetEntry("projekt.xml").Open()))
            Check(metadata.ReadToEnd().Contains("<Version>2</Version>") && zip.GetEntry("videa/video-003.avi") != null, "Video archives use package version 2 and include all video files");
        NotesStore target = new NotesStore(Path.Combine(directory, "target")); NotesState received = target.Load();
        Project imported = ProjectArchive.Import(target, received, package);
        Check(imported.Text == project.Text && target.VideoFiles(imported).Length == 3 && target.ImageFiles(imported).Length == 1
            && File.ReadAllBytes(target.VideoFiles(imported)[0]).SequenceEqual(File.ReadAllBytes(first)), "ZIP transfer preserves notes, image attachments and original video bytes");
        string invalid = Path.Combine(directory, "bad.zip");
        foreach (string path in new[] { "videa/../../escape.avi", "videa/NUL.avi", "videa/clip.exe", "videa/clip.avi", "videa/VIDEO-001.AVI" })
        {
            File.Copy(package, invalid, true);
            using (ZipArchive zip = ZipFile.Open(invalid, ZipArchiveMode.Update))
            using (Stream stream = zip.CreateEntry(path).Open()) stream.Write(new byte[40], 0, 40);
            Check(RefusesImport(target, received, invalid) && target.Load().Projects.Count == 2 && Directory.GetDirectories(target.DirectoryPath, ".import-*").Length == 0,
                "Video import rejects unsafe, duplicate or damaged entries without changing projects: " + path);
        }
        File.Copy(package, invalid, true);
        using (ZipArchive zip = ZipFile.Open(invalid, ZipArchiveMode.Update))
        {
            string text; using (StreamReader reader = new StreamReader(zip.GetEntry("projekt.xml").Open())) text = reader.ReadToEnd();
            zip.GetEntry("projekt.xml").Delete(); using (StreamWriter writer = new StreamWriter(zip.CreateEntry("projekt.xml").Open())) writer.Write(text.Replace("<Version>2</Version>", "<Version>1</Version>"));
        }
        Check(RefusesImport(target, received, invalid), "An archive containing video cannot pretend to be the older image-only format");
        bool failed = false; int count = received.Projects.Count;
        using (FileStream locked = new FileStream(Path.Combine(target.DirectoryPath, "poznamky.xml"), FileMode.Open, FileAccess.Read, FileShare.Read))
            try { ProjectArchive.Import(target, received, package); } catch (IOException) { failed = true; }
        Check(failed && received.Projects.Count == count && Directory.GetDirectories(target.DirectoryPath).Length == count, "A failed video import commit removes all imported files and retains existing projects");
        NotesStore moved = new NotesStore(Path.Combine(directory, "moved")); NotesState movedState = moved.CopyFrom(store, state);
        Check(moved.VideoFiles(movedState.Projects[0]).Length == 3 && store.VideoFiles(project).Length == 3, "Changing storage copies all videos and retains the original storage");
        using (VideoViewer viewer = new VideoViewer(new[] { first }, first, delegate { return false; }))
        {
            viewer.ErrorReported = delegate(string error) { throw new Exception(error); }; viewer.Show();
            Check(PumpUntil(delegate { return viewer.Ready; }, 12000), "Video is ready before a canceled deletion");
            viewer.SetPlaying(false); viewer.SeekTo(1.5);
            Check(!viewer.DeleteCurrent() && File.Exists(first) && PumpUntil(delegate { return viewer.Ready && Math.Abs(viewer.Position - 1.5) < .3; }, 12000) && !viewer.Playing,
                "Canceling deletion restores the same video, playback position and paused state");
        }
        string undecodable = Path.Combine(directory, "undecodable.avi");
        File.WriteAllBytes(undecodable, System.Text.Encoding.ASCII.GetBytes("RIFF0000AVI 00000"));
        using (VideoViewer viewer = new VideoViewer(new[] { undecodable, first }, undecodable, null))
        {
            string error = null; viewer.ErrorReported = delegate(string text) { error = text; }; viewer.Show();
            Check(PumpUntil(delegate { return error != null; }, 12000) && error.StartsWith("Cannot play video.") && !viewer.Ready && File.Exists(undecodable),
                "A decoder failure shows a translated error and retains the source video");
            viewer.Next.PerformClick();
            Check(PumpUntil(delegate { return viewer.Ready; }, 12000) && viewer.CurrentIndex == 1, "The player can navigate to a working video after a decoder failure");
        }
        using (VideoViewer viewer = new VideoViewer(store.VideoFiles(project), first, delegate(string file) { store.DeleteVideo(state, project, file); return true; }))
        {
            string error = null; viewer.ErrorReported = delegate(string text) { error = text; };
            viewer.Show();
            Check(PumpUntil(delegate { return viewer.Ready || error != null; }, 12000) && viewer.Ready && viewer.Duration >= 4.9, "Windows opens the native AVI fixture and reports its playback duration: " + error);
            Size normalSize = viewer.Size; viewer.Size = viewer.MinimumSize; Application.DoEvents();
            Check(Descendants(viewer).OfType<Button>().All(b => b.Height >= b.Font.Height + 8 && b.Parent.ClientRectangle.Contains(b.Bounds)),
                "Video playback controls remain fully visible at minimum size and Windows DPI scaling");
            viewer.Size = normalSize; Application.DoEvents();
            viewer.SetPlaying(false); viewer.SeekTo(2);
            Check(PumpUntil(delegate { return Math.Abs(viewer.Position - 2) < .3; }, 1500) && !viewer.Playing, "The built-in player pauses and seeks to a chosen position");
            viewer.SetPlaying(true); double before = viewer.Position;
            Check(PumpUntil(delegate { return viewer.Position > before + .2; }, 2500), "Video playback advances inside the app");
            viewer.SetPlaying(false); viewer.SetZoom(2);
            Check(viewer.Zoom == 2 && viewer.Playing == false, "Video zoom preserves paused playback");
            Ui.SetLanguage("cs"); Check(viewer.Delete.AccessibleName == "Smazat video", "The open video player switches to Czech live"); Ui.SetLanguage("en");
            viewer.Next.PerformClick(); Check(PumpUntil(delegate { return viewer.Ready; }, 12000) && viewer.CurrentIndex == 1 && viewer.Visible, "Next video opens without closing the player");
            viewer.Previous.PerformClick(); Check(PumpUntil(delegate { return viewer.Ready; }, 12000) && viewer.CurrentIndex == 0, "Previous video opens in the same player");
            viewer.SetPlaying(false); viewer.SeekTo(1); PumpUntil(delegate { return Math.Abs(viewer.Position - 1) < .3; }, 1000);
            using (Bitmap preview = new Bitmap(viewer.Width, viewer.Height))
            { using (Graphics graphics = Graphics.FromImage(preview)) graphics.CopyFromScreen(viewer.Location, Point.Empty, viewer.Size); preview.Save(Path.Combine(directory, "player.png")); }
            Check(viewer.DeleteCurrent() && !File.Exists(first) && viewer.Visible && PumpUntil(delegate { return viewer.Ready; }, 12000), "Deleting a playing video releases its file and opens the next clip");
            Check(!project.Text.Contains("[Video: video-001.avi]") && project.Text.Contains("[Video: video-002.avi]"), "Video deletion removes only that video's exact note markers");
            Check(viewer.DeleteCurrent() && !File.Exists(second) && PumpUntil(delegate { return viewer.Ready; }, 12000), "The player can delete a second video after navigation");
            Check(viewer.DeleteCurrent() && viewer.IsDisposed && store.VideoFiles(project).Length == 0, "Deleting the final video closes the empty player");
        }
        target.DeleteProject(received, imported);
        Check(!Directory.Exists(target.ProjectDirectory(imported)) && received.Projects.Count == 1, "Deleting a project removes its videos along with its other data");
    }

    /// <summary>Verify attachment naming, exact reference updates, collisions, rollback, playback, and ZIP transfer.</summary>
    private static void TestAttachmentNames(string directory)
    {
        Ui.SetLanguage("en"); NotesStore store = new NotesStore(directory); NotesState state = store.Load(); state.OverlayVisible = false;
        using (MainForm form = new MainForm(store, state))
        {
            form.ErrorReported = delegate(string error) { throw new Exception(error); }; form.Show(); Application.DoEvents();
            Project project = state.Projects[0]; string image, other;
            using (Bitmap sample = new Bitmap(20, 20)) { image = form.AttachImage(sample); other = form.AttachImage(sample); }
            string oldLeaf = Path.GetFileName(image);
            form.Editor.AppendText("\r\n[Snímek: " + oldLeaf + "]\r\nText: " + oldLeaf); form.Flush();
            byte[] pixels = File.ReadAllBytes(image);
            string outside = Path.Combine(directory, "outside.png"); File.Copy(image, outside);
            bool unowned = false;
            try { store.RenameImage(state, project, outside, "Outside rename"); } catch (InvalidOperationException) { unowned = true; }
            Check(unowned && File.ReadAllBytes(outside).SequenceEqual(pixels), "Image renaming refuses a file outside the project and preserves its data");
            string renamed = form.RenameAttachment(image, "Návrh – verze 1", false);
            Check(Path.GetFileName(renamed) == "Návrh – verze 1.png" && !File.Exists(image) && File.ReadAllBytes(renamed).SequenceEqual(pixels), "Image renaming preserves the PNG extension and original bytes");
            Check(project.Text.Contains("[Image: Návrh – verze 1.png]") && project.Text.Contains("[Snímek: Návrh – verze 1.png]") && project.Text.Contains("Text: " + oldLeaf)
                && project.Text.Contains("[Image: " + Path.GetFileName(other) + "]"), "Renaming updates exact Czech and English markers while retaining free text and other attachments");
            Check(store.Load().Projects[0].Text == project.Text && ((Button)form.Pictures.Controls[0]).ContextMenuStrip.Items[2].Text == "Rename image…", "A renamed image persists and its gallery menu offers English renaming");
            foreach (string name in new[] { " ", "../outside", "NUL", "COM1", "bad:name" })
            {
                bool refused = false; string text = project.Text;
                try { form.RenameAttachment(renamed, name, false); } catch (InvalidOperationException) { refused = true; }
                Check(refused && File.Exists(renamed) && project.Text == text, "Image renaming rejects an unsafe filename without altering data: " + name);
            }
            bool collision = false;
            try { form.RenameAttachment(renamed, Path.GetFileNameWithoutExtension(other), false); } catch (IOException) { collision = true; }
            Check(collision && File.Exists(renamed) && File.Exists(other), "Renaming refuses an existing attachment name without overwriting either file");
            using (System.Windows.Forms.Timer close = new System.Windows.Forms.Timer { Interval = 100 })
            {
                bool translated = false;
                close.Tick += delegate
                {
                    NameDialog dialog = Application.OpenForms.OfType<NameDialog>().FirstOrDefault();
                    if (dialog == null) return;
                    translated = dialog.Text == "Rename image" && Descendants(dialog).OfType<Label>().Any(label => label.Text == "Attachment name (without extension)")
                        && Descendants(dialog).OfType<TextBox>().Any(box => box.Text == "Návrh – verze 1");
                    dialog.Close(); close.Stop();
                };
                close.Start();
                Check(NameDialog.Ask(form, "Přejmenovat snímek", "Návrh – verze 1", "Název přílohy (bez přípony)") == null && translated,
                    "The attachment dialog translates its title and field, preserves the custom name, and supports cancel");
            }
            string note = project.Text; bool failed = false;
            using (FileStream locked = new FileStream(Path.Combine(store.ProjectDirectory(project), "poznamky.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                try { form.RenameAttachment(renamed, "Blocked note", false); } catch (IOException) { failed = true; }
            Check(failed && File.Exists(renamed) && project.Text == note && store.Load().Projects[0].Text == note && !File.Exists(Path.Combine(Path.GetDirectoryName(renamed), "Blocked note.png")),
                "A failed note commit rolls back the attachment filename and every note marker");
            renamed = form.RenameAttachment(renamed, "návrh – verze 1", false);
            Check(Directory.GetFiles(Path.GetDirectoryName(renamed)).Any(file => Path.GetFileName(file) == "návrh – verze 1.png") && project.Text.Contains("[Image: návrh – verze 1.png]"), "Renaming supports a change of letter case on Windows");
            using (ImageViewer viewer = new ImageViewer(store.ImageFiles(project), renamed, null, delegate(string file) { return form.RenameAttachment(file, "Preview image", false); }))
            {
                viewer.Show(); Application.DoEvents(); viewer.Canvas.OriginalSize(); float zoom = viewer.Canvas.Zoom;
                viewer.Rename.PerformClick();
                Check(viewer.Text == "Preview image.png" && viewer.Canvas.Zoom == zoom && viewer.Canvas.Image.Width == 20, "The viewer's pencil renames an image while preserving its displayed pixels and zoom");
                Ui.SetLanguage("cs"); Check(viewer.Rename.AccessibleName == "Přejmenovat snímek", "The image rename icon updates its accessible name with the language"); Ui.SetLanguage("en");
                renamed = Path.Combine(Path.GetDirectoryName(renamed), "Preview image.png");
            }
            string originalVideo = Path.Combine(directory, "original.avi"); MakeVideo(originalVideo); string video = form.AttachVideo(originalVideo);
            string unchangedNote = project.Text;
            Check(form.RenameAttachment(video, Path.GetFileNameWithoutExtension(video), true) == video && File.ReadAllBytes(video).SequenceEqual(File.ReadAllBytes(originalVideo)) && project.Text == unchangedNote,
                "An unchanged video name preserves its path, bytes and note references");
            using (VideoViewer viewer = new VideoViewer(store.VideoFiles(project), video, null, delegate(string file) { return form.RenameAttachment(file, "Meeting clip", true); }))
            {
                string error = null; viewer.ErrorReported = delegate(string text) { error = text; }; viewer.Show();
                Check(PumpUntil(delegate { return viewer.Ready || error != null; }, 12000) && viewer.Ready, "The video is open before renaming it from its player");
                viewer.SetPlaying(false); viewer.SeekTo(2); viewer.SetZoom(2);
                viewer.Rename.PerformClick();
                Check(PumpUntil(delegate { return viewer.Ready && Math.Abs(viewer.Position - 2) < .3; }, 12000) && !viewer.Playing && viewer.Zoom == 2
                    && viewer.Text == "Meeting clip.avi" && !File.Exists(video) && project.Text.Contains("[Video: Meeting clip.avi]"), "Video renaming releases the open file, updates its marker and restores playback position, paused state and zoom");
                video = Path.Combine(Path.GetDirectoryName(video), "Meeting clip.avi");
                Ui.SetLanguage("cs"); Check(viewer.Rename.AccessibleName == "Přejmenovat video", "The video rename icon updates with the language"); Ui.SetLanguage("en");
            }
            using (VideoViewer viewer = new VideoViewer(store.VideoFiles(project), video, null, delegate { return null; }))
            {
                viewer.ErrorReported = delegate(string error) { throw new Exception(error); }; viewer.Show();
                Check(PumpUntil(delegate { return viewer.Ready; }, 12000) && !viewer.RenameCurrent() && PumpUntil(delegate { return viewer.Ready; }, 12000) && File.Exists(video),
                    "Canceling a video rename retains the clip and reopens playback");
            }
            string package = Path.Combine(directory, "named.zip"); form.ExportProject(package);
            NotesStore target = new NotesStore(Path.Combine(directory, "receiver")); NotesState received = target.Load(); Project imported = ProjectArchive.Import(target, received, package);
            Check(target.ImageFiles(imported).Any(file => Path.GetFileName(file) == "Preview image.png") && Path.GetFileName(target.VideoFiles(imported).Single()) == "Meeting clip.avi" && imported.Text == project.Text,
                "ZIP export and import preserve custom image/video names and all renamed note references");
            Check(form.DeleteVideo(video) && !project.Text.Contains("[Video: Meeting clip.avi]") && form.DeleteImage(renamed) && !project.Text.Contains("[Image: Preview image.png]"),
                "Renamed attachments can still be deleted with their note references");
            Check(Directory.GetFiles(store.ProjectDirectory(project), "*.prejmenovani-*", SearchOption.AllDirectories).Length == 0, "Completed and rolled-back renames leave no staging files");
            form.ExitApplication();
        }
    }

    /// <summary>Verify English defaults, persistence, UI switching, user text, and two-line headings.</summary>
    private static void TestLanguages(string directory, string preview)
    {
        string settings = Path.Combine(directory, "settings");
        Check(Ui.ReadLanguage(settings) == "en", "Missing language settings default to English");
        Ui.SaveLanguage(settings, "en");
        Check(Ui.ReadLanguage(settings) == "en", "The user's language preference persists independently of project storage");
        File.WriteAllText(Path.Combine(settings, "jazyk.txt"), "unknown");
        Check(Ui.ReadLanguage(settings) == "en", "An invalid language preference falls back to English");
        bool invalid = false;
        try { Ui.SetLanguage("de"); } catch (ArgumentException) { invalid = true; }
        Check(invalid && Ui.Language == "cs", "Unsupported languages leave the active language unchanged");
        Ui.SetLanguage("cs");
        NotesStore store = new NotesStore(Path.Combine(directory, "notes"));
        NotesState state = store.Load();
        state.OverlayVisible = false;
        bool refusePreference = false;
        using (MainForm form = new MainForm(store, state, null, delegate(string language)
        {
            if (refusePreference) throw new IOException("Language settings are locked.");
            Ui.SaveLanguage(settings, language);
        }))
        {
            try
            {
                form.ErrorReported = delegate { };
                form.Show(); Application.DoEvents();
                Project project = form.AddProject("Nový projekt");
                form.Editor.Text = "Název projektu\r\nMoje rozepsaná česká poznámka, Save a New project.";
                form.ProjectSearch.Text = "nov";
                form.Editor.Select(4, 8);
                string note = form.Editor.Text, folder = project.Folder;
                int start = form.Editor.SelectionStart, length = form.Editor.SelectionLength;
                form.LanguageButton.PerformClick(); Application.DoEvents();
                Check(form.LanguageMenu.Visible && form.LanguageMenu.Items[0].Text == "Čeština" && form.LanguageMenu.Items[1].Text == "English",
                    "The footer language button opens the language choices");
                form.LanguageMenu.Close();
                form.LanguageMenu.Items[1].PerformClick();
                Check(Ui.Language == "en" && form.LanguageButton.Text == "EN" && Ui.ReadLanguage(settings) == "en", "The English menu choice changes and remembers the language without restarting");
                File.Delete(Path.Combine(settings, "jazyk.txt"));
                form.LanguageMenu.Items[1].PerformClick();
                Check(File.Exists(Path.Combine(settings, "jazyk.txt")) && Ui.ReadLanguage(settings) == "en", "Choosing the already active English language still saves an explicit preference");
                Check(Descendants(form).OfType<Button>().Any(b => b.Text == "New project") && Descendants(form).OfType<Button>().Any(b => b.Text == "Storage…")
                    && Descendants(form).OfType<Button>().Any(b => b.Text == "Import project…"), "Main actions are displayed in English");
                Check(form.Editor.Text == note && form.Editor.SelectionStart == start && form.Editor.SelectionLength == length && form.ProjectSearch.Text == "nov"
                    && form.Projects.SelectedItem == project && project.Name == "Nový projekt" && project.Folder == folder,
                    "Changing language preserves user text, selection, search, project identity and names that match translation keys");
                Button rename = Descendants(form).OfType<RenameButton>().Single();
                Check(form.Projects.ContextMenuStrip.Items[0].Text == "Rename project    F2" && form.tips.GetToolTip(rename) == "Rename project · F2"
                    && form.Editor.AccessibleName == "Notes for the selected project" && form.ProjectScroll.AccessibleDescription.StartsWith("Position"),
                    "Context menus, tooltips and accessibility descriptions switch to English");
                form.ProjectSearch.Text = "not found";
                Check(Descendants(form).OfType<Label>().Any(l => l.Text == "No matching project") && !form.Editor.Enabled, "The empty search result is translated");
                form.ProjectSearch.Clear();
                form.Projects.SelectedItem = project;
                string image;
                using (Bitmap sample = new Bitmap(120, 80))
                {
                    using (Graphics graphics = Graphics.FromImage(sample)) graphics.Clear(Color.DarkCyan);
                    image = form.AttachImage(sample);
                }
                string leaf = Path.GetFileName(image);
                Check(form.Editor.Text.Contains("[Image: " + leaf + "]") && ((Button)form.Pictures.Controls[0]).ContextMenuStrip.Items[1].Text == "Delete image…",
                    "Images added in English have English note markers and thumbnail actions");
                form.Editor.AppendText("\r\n[Snímek: " + leaf + "]\r\n[Image: keep.png]");
                using (ImageViewer viewer = new ImageViewer(store.ImageFiles(project), image, null))
                {
                    viewer.Show(); Application.DoEvents();
                    Check(viewer.Delete.Text == "Delete image" && Descendants(viewer).OfType<Button>().Any(b => b.Text == "Fit") && viewer.Next.AccessibleName == "Next image",
                        "The image viewer uses English controls and accessibility text");
                    float zoom = viewer.Canvas.Zoom;
                    form.LanguageMenu.Items[0].PerformClick();
                    Check(viewer.Delete.Text == "Smazat snímek" && form.LanguageButton.Text == "CZ" && Ui.ReadLanguage(settings) == "cs" && viewer.Canvas.Zoom == zoom,
                        "Switching back to Czech updates an open viewer without losing its zoom");
                }
                Check(form.DeleteImage(image) && !form.Editor.Text.Contains(leaf) && form.Editor.Text.Contains("[Image: keep.png]"),
                    "Image deletion removes Czech and English markers for that image and preserves other markers");
                form.ChangeLanguage("en");
                using (System.Windows.Forms.Timer close = new System.Windows.Forms.Timer { Interval = 100 })
                {
                    bool translatedDialog = false;
                    close.Tick += delegate
                    {
                        NameDialog dialog = Application.OpenForms.OfType<NameDialog>().FirstOrDefault();
                        if (dialog == null) return;
                        translatedDialog = dialog.Text == "Rename project" && Descendants(dialog).OfType<Label>().Any(l => l.Text == "Project name")
                            && Descendants(dialog).OfType<Button>().Any(b => b.Text == "Save") && Descendants(dialog).OfType<TextBox>().Single().Text == project.Name;
                        close.Stop(); dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                    };
                    close.Start();
                    NameDialog.Ask(form, "Přejmenovat projekt", project.Name);
                    Check(translatedDialog, "The name dialog translates its captions while retaining the user's project name");
                }
                refusePreference = true;
                Check(!form.ChangeLanguage("cs") && Ui.Language == "en" && Ui.ReadLanguage(settings) == "en" && form.LanguageButton.Text == "EN",
                    "A failed preference write preserves the active and persisted language");
                refusePreference = false;
                string failure = null;
                try { ProjectArchive.Export(store, project, Path.Combine(directory, "bad.txt")); } catch (IOException error) { failure = error.Message; }
                Check(failure == "The archive must have a .zip extension.", "Archive validation errors are translated into English");
                using (Bitmap sample = new Bitmap(120, 80)) image = form.AttachImage(sample);
                string zip = Path.Combine(directory, "english.zip");
                form.ExportProject(zip);
                form.ChangeLanguage("cs");
                NotesStore receiver = new NotesStore(Path.Combine(directory, "receiver"));
                NotesState received = receiver.Load();
                Project imported = ProjectArchive.Import(receiver, received, zip);
                string englishNote = imported.Text;
                receiver.DeleteImage(received, imported, receiver.ImageFiles(imported).Single());
                Check(englishNote.Contains("[Image: " + Path.GetFileName(image) + "]") && !imported.Text.Contains(Path.GetFileName(image)) && imported.Text.Contains("[Image: keep.png]"),
                    "A Czech teammate can import an English project and delete its image without leaving an English reference behind");
                string namedImage = Path.Combine(Path.GetDirectoryName(image), "Uložit.png");
                File.Move(image, namedImage);
                form.ChangeLanguage("en");
                Check(((Button)form.Pictures.Controls[0]).Text == "Uložit", "Image filenames matching a translation key remain unchanged");
                form.RenameSelectedProject("A long project name that should wrap across two lines in the branded header while the note remains fully usable");
                form.Size = form.MinimumSize;
                Application.DoEvents();
                Label heading = Descendants(form).OfType<TwoLineLabel>().Single();
                using (Bitmap title = new Bitmap(heading.Width, heading.Height))
                {
                    heading.DrawToBitmap(title, heading.ClientRectangle);
                    int lineHeight = TextRenderer.MeasureText("Ag", heading.Font).Height;
                    bool secondLine = false, thirdLine = false;
                    for (int y = lineHeight; y < title.Height; y++)
                        for (int x = 0; x < title.Width; x++)
                        {
                            Color pixel = title.GetPixel(x, y);
                            if (pixel.R < 150 || pixel.G < 150 || pixel.B < 150) continue;
                            if (y < lineHeight * 2) secondLine = true; else thirdLine = true;
                        }
                    title.Save(Path.Combine(directory, "title.png"));
                    Check(secondLine && !thirdLine, "A long project title visibly wraps onto a second line and never draws a third line");
                }
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(preview, System.Drawing.Imaging.ImageFormat.Png); }
                Check(Descendants(form).OfType<Button>().Any(b => b.Text == "New project" && b.Width >= TextRenderer.MeasureText(b.Text, b.Font).Width),
                    "English action text fits at the minimum window size with Windows DPI scaling");
            }
            finally { Ui.SetLanguage("cs"); }
        }
        Ui.SetLanguage("en");
        Check(new NotesStore(Path.Combine(directory, "fresh-english")).Load().Projects.Single().Name == "General", "Only a new empty English storage gets the translated default project name");
        Ui.SetLanguage("cs");
    }

    /// <summary>Verify UTF-8, separate folders, backups, invalid input rejection, and safe filenames.</summary>
    private static void TestStorage(string directory)
    {
        NotesStore store = new NotesStore(directory);
        NotesState state = store.Load();
        Check(state.Projects.Count == 1 && state.Projects[0].Text == "", "First run starts with an empty project");
        string originalText = "Příliš žluťoučký kůň 📝\r\n<poznámka> & další\ttext";
        state.Projects[0].Text = originalText;
        Project second = new Project { Name = "Druhý projekt", Text = new string('x', 100000) };
        state.Projects.Add(second);
        state.SelectedId = second.Id;
        state.OverlayX = -1500;
        state.OverlayY = 240;
        state.OverlayVisible = false;
        store.Save(state);
        NotesState loaded = store.Load();
        string firstFile = Path.Combine(store.ProjectDirectory(state.Projects[0]), "poznamky.txt");
        Check(File.ReadAllText(firstFile) == originalText && loaded.Projects[0].Text == originalText, "UTF-8 text files preserve Unicode, newlines and tabs exactly");
        Check(loaded.Projects[1].Text.Length == 100000, "Long notes are not truncated");
        Check(state.Projects[0].Folder != second.Folder && File.Exists(Path.Combine(store.ProjectDirectory(second), "poznamky.txt")), "Each project gets its own folder and note file");
        Check(!File.ReadAllText(Path.Combine(directory, "poznamky.xml")).Contains("<Text>"), "The project index contains no duplicate note body");
        Check(loaded.SelectedId == second.Id && loaded.OverlayX == -1500 && !loaded.OverlayVisible, "Selection and overlay settings round-trip");
        state.Projects[0].Text = "Novější text";
        state.OverlayY++;
        store.Save(state);
        Check(store.Load().Projects[0].Text == "Novější text", "Changed notes are atomically saved");
        Check(File.ReadAllText(firstFile + ".bak") == originalText && File.Exists(Path.Combine(directory, "poznamky.xml.bak")), "Text and project metadata have independent backups");
        store.Save(state);
        Check(File.ReadAllText(firstFile + ".bak") == originalText, "An unchanged save keeps the meaningful previous text backup");

        string index = Path.Combine(directory, "poznamky.xml");
        File.WriteAllText(index, "broken xml");
        NotesStore recovery = new NotesStore(directory);
        NotesState restored = recovery.Load();
        Check(recovery.Recovered && restored.Projects[0].Text == "Novější text", "A damaged index recovers while retaining the latest project text");
        Check(Directory.GetFiles(directory, "poznamky.xml.poskozene-*").Length == 1, "The damaged index is preserved");
        recovery.Save(restored);
        File.WriteAllBytes(firstFile, new byte[] { 0xff, 0xff });
        bool refused = false;
        try { recovery.Load(); } catch (InvalidDataException) { refused = true; }
        Check(refused && File.ReadAllBytes(firstFile).SequenceEqual(new byte[] { 0xff, 0xff }) && File.ReadAllText(firstFile + ".bak") == originalText,
            "Invalid UTF-8 is reported while the original and its backup stay untouched");
        File.WriteAllText(firstFile, originalText);
        File.Delete(firstFile);
        restored = recovery.Load();
        Check(recovery.Recovered && restored.Projects[0].Text == originalText, "A missing note is restored from its own backup");
        recovery.Save(restored);
        File.Delete(firstFile);
        File.Delete(firstFile + ".bak");
        refused = false;
        try { recovery.Load(); } catch (IOException) { refused = true; }
        Check(refused && !File.Exists(firstFile), "Missing note files are reported instead of silently replaced by empty notes");
        recovery.Save(restored);
        File.WriteAllText(index, "broken primary");
        File.WriteAllText(index + ".bak", "broken backup");
        refused = false;
        try { new NotesStore(directory).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.ReadAllText(index) == "broken primary" && File.ReadAllText(index + ".bak") == "broken backup", "Two damaged index files stay intact");

        NotesStore invalid = new NotesStore(Path.Combine(directory, "invalid"));
        refused = false;
        try { invalid.Save(new NotesState { Projects = null }); } catch (InvalidOperationException) { refused = true; }
        Check(refused && !Directory.Exists(invalid.DirectoryPath), "An invalid state is rejected before writing");
        NotesState bad = invalid.Load();
        bad.Projects[0].Folder = "..\\outside";
        refused = false;
        try { invalid.Save(bad); } catch (InvalidOperationException) { refused = true; }
        Check(refused && !Directory.Exists(invalid.DirectoryPath), "A project folder cannot escape the selected storage root");

        NotesStore names = new NotesStore(Path.Combine(directory, "names"));
        NotesState named = names.Load();
        named.Projects[0].Name = "CON";
        named.Projects.Add(new Project { Name = "CON" });
        named.Projects.Add(new Project { Name = "Web / API: návrh?" });
        Directory.CreateDirectory(Path.Combine(names.DirectoryPath, "Projekt CON"));
        File.WriteAllText(Path.Combine(names.DirectoryPath, "Projekt CON", "moje.txt"), "unrelated file");
        names.Save(named);
        Check(named.Projects.Select(p => p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 && names.Load().Projects.Count == 3, "Reserved names, invalid characters and duplicate names get safe distinct folders");
        Check(File.ReadAllText(Path.Combine(names.DirectoryPath, "Projekt CON", "moje.txt")) == "unrelated file", "Existing unrelated folders are not reused or overwritten");
        string blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "existing file");
        refused = false;
        try { new NotesStore(blocked).Save(restored); } catch (IOException) { refused = true; }
        Check(refused && File.ReadAllText(blocked) == "existing file", "An unwritable root preserves existing data");
    }

    /// <summary>Verify legacy XML migration, initial storage selection, and path preference persistence.</summary>
    private static void TestLegacyAndLocation(string root)
    {
        string legacy = Path.Combine(root, "old");
        Directory.CreateDirectory(legacy);
        string xml = "<NotesState><Projects><Project><Id>old-id</Id><Name>Starý projekt</Name><Text>Žluťoučký kůň &amp; text&#xD;\nDruhý řádek</Text></Project></Projects><SelectedId>old-id</SelectedId></NotesState>";
        string oldFile = Path.Combine(legacy, "poznamky.xml");
        File.WriteAllText(oldFile, xml);
        NotesState state;
        NotesStore migrated = Program.PrepareStorage(legacy, Path.Combine(root, "chosen"), out state);
        Check(state.Projects[0].Text == "Žluťoučký kůň & text\r\nDruhý řádek" && migrated.Load().Projects[0].Text == state.Projects[0].Text, "The first folder selection imports legacy XML notes into project folders");
        Check(File.ReadAllText(oldFile) == xml && Directory.GetDirectories(legacy).Length == 0, "Legacy migration leaves the original storage untouched");
        StorageLocation settings = new StorageLocation(Path.Combine(root, "settings"));
        Check(settings.Read() == null, "Without a saved location the app requests the first folder selection");
        settings.Save(migrated.DirectoryPath);
        Check(settings.Read() == migrated.DirectoryPath, "The selected storage path survives a restart");
        NotesState existing;
        NotesStore reopened = Program.PrepareStorage(legacy, migrated.DirectoryPath, out existing);
        Check(existing.Projects.Count == 1 && existing.Projects[0].Id == "old-id", "An existing storage opens without importing duplicate projects");
        NotesState fresh;
        Program.PrepareStorage(null, Path.Combine(root, "empty"), out fresh);
        Check(fresh.Projects.Count == 1 && fresh.Projects[0].Text == "", "A fresh folder is initialized only after the user selects it");
    }

    /// <summary>Verify end-to-end editing, Tab, image insertion, autosave, and storage changes.</summary>
    private static void TestWindow(string directory, string preview)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        NotesStore store = new NotesStore(directory);
        string committed = directory;
        bool refuseConfig = false;
        using (MainForm form = new MainForm(store, store.Load(), delegate(string path)
        {
            if (refuseConfig) throw new IOException("Simulated settings failure");
            committed = path;
        }))
        {
            form.ErrorReported = delegate(string message) { throw new Exception(message); };
            form.Show();
            Application.DoEvents();
            Check(form.Text == "Mark´s Marks v1.0.0" && form.Icon != null, "The title bar shows the application name and version with the branded executable icon");
            form.Editor.Text = "První projekt: vlastní poznámka.";
            Project second = form.AddProject("Webové stránky");
            Check(form.Editor.Text == "" && form.State.Projects[0].Text.Contains("První projekt"), "A new project retains the previous project's note");
            form.Editor.Text = "Připravit hlavní stránku.\r\nDoplnit kontakty.";
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && store.Load().Projects[1].Text != form.Editor.Text)
            {
                Application.DoEvents(); Thread.Sleep(20);
            }
            Check(store.Load().Projects[1].Text == form.Editor.Text, "The timer saves text without manual saving");

            form.Editor.Text = "Nákup";
            form.Editor.Select(form.Editor.TextLength, 0);
            form.Editor.IndentSelection();
            Check(form.Editor.Text == "\t- Nákup" && form.Editor.SelectionStart == form.Editor.TextLength, "Tab indents a plain line, adds a dash and keeps the caret with its text");
            form.Editor.IndentSelection();
            Check(form.Editor.Text == "\t\t- Nákup", "Repeated Tab increases indentation without duplicate dashes");
            form.Editor.Text = "- první\r\ndruhý";
            form.Editor.SelectAll();
            form.Editor.IndentSelection();
            Check(form.Editor.Text == "\t- první\r\n\t- druhý", "Tab indents all selected lines while retaining Windows newlines");

            form.Editor.Text = "- Návrh stránky\r\n\t- Vzor: ";
            form.Editor.Select(form.Editor.TextLength, 0);
            using (Bitmap sample = new Bitmap(120, 80))
            {
                using (Graphics graphics = Graphics.FromImage(sample)) graphics.Clear(Color.DarkCyan);
                form.ReadClipboardFiles = delegate { return new string[0]; };
                form.ReadClipboardImage = delegate { return new Bitmap(sample); };
                form.Editor.Paste();
                form.Editor.AppendText("\r\n\t- Druhý snímek: ");
                form.Editor.Paste();
            }
            string[] images = store.ImageFiles(second);
            Check(images.Length == 2 && images[0] != images[1], "The native paste command saves multiple images without replacing earlier images");
            Check(form.Editor.Text.Contains("[Snímek: snimek-001.png]") && form.Editor.Text.Contains("[Snímek: snimek-002.png]"), "Pasted images are linked at the caret to the matching note points");
            using (Bitmap image = new Bitmap(images[0]))
                Check(image.Width == 120 && image.Height == 80 && image.GetPixel(5, 5).ToArgb() == Color.DarkCyan.ToArgb(), "Saved PNG images retain their dimensions and pixels");
            Check(form.Pictures.Controls.Count == 2 && ((Button)form.Pictures.Controls[0]).Image != null, "Saved images are displayed as thumbnails inside the app");
            bool viewed = false;
            using (System.Windows.Forms.Timer closeViewer = new System.Windows.Forms.Timer { Interval = 80 })
            {
                closeViewer.Tick += delegate
                {
                    Form viewer = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "snimek-001.png");
                    if (viewer == null) return;
                    ZoomCanvas picture = ((ImageViewer)viewer).Canvas;
                    viewed = picture.Image.Width == 120 && picture.Image.Height == 80 && picture.Zoom > 0;
                    closeViewer.Stop();
                    viewer.Close();
                };
                closeViewer.Start();
                ((Button)form.Pictures.Controls[0]).PerformClick();
            }
            Check(viewed, "Clicking a thumbnail opens the saved image in the in-app viewer");
            form.Projects.SelectedIndex = 0;
            Check(form.Editor.Text.Contains("První projekt") && form.Pictures.Controls.Count == 0, "Text and image attachments remain separate between projects");
            form.Projects.SelectedIndex = 1;
            Check(form.Pictures.Controls.Count == 2, "Switching back reloads the project's images");

            string nextRoot = directory + "-new";
            string note = form.Editor.Text;
            Check(form.ChangeStorage(nextRoot) && committed == nextRoot, "A storage change is committed after copying succeeds");
            NotesStore moved = new NotesStore(nextRoot);
            NotesState movedState = moved.Load();
            Check(movedState.Projects[1].Text == note && moved.ImageFiles(movedState.Projects[1]).Length == 2, "A storage change copies both notes and image attachments");
            Check(store.Load().Projects[1].Text == note && store.ImageFiles(second).Length == 2, "The original storage stays intact after changing location");
            Check(form.ChangeStorage(directory) && form.Editor.Text == note && form.Pictures.Controls.Count == 2, "An existing storage can be reopened without duplicate projects or images");
            string blocked = directory + "-blocked";
            File.WriteAllText(blocked, "existing file");
            bool failed = false;
            try { form.ChangeStorage(blocked); } catch (IOException) { failed = true; }
            Check(failed && committed == directory && form.Editor.Text == note && form.Flush(), "An unsuccessful storage change leaves the active storage and editor unchanged");
            refuseConfig = true;
            failed = false;
            try { form.ChangeStorage(directory + "-config-failure"); } catch (IOException) { failed = true; }
            Check(failed && committed == directory && form.Editor.Text == note && form.Flush(), "A failed preference write does not switch the active storage");
            refuseConfig = false;
            form.Close(); Application.DoEvents();
            Check(!form.Visible && !form.IsDisposed, "Closing hides the editor while the app stays alive");
            form.OpenEditor(); Application.DoEvents();
            Check(form.Visible && form.Pictures.Controls.Count == 2, "The hidden editor reopens with its image thumbnails");
            Native.PostMessage(form.Handle, 0x0312, new IntPtr(1), IntPtr.Zero); Application.DoEvents();
            Check(!form.Visible, "The hotkey message hides the editor");
            Native.PostMessage(form.Handle, Native.ShowMessage, IntPtr.Zero, IntPtr.Zero); Application.DoEvents();
            Check(form.Visible, "A second-instance message reopens the editor");
            Check(form.Editor.ClientSize.Width > 250 && form.Editor.ClientSize.Height > 100, "The editor remains usable with thumbnails and Windows DPI scaling");
            form.Editor.Select(form.Editor.TextLength, 0);
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Editor.Text += "\r\nPoslední změna před ukončením.";
            form.ExitApplication();
            Check(form.IsDisposed && store.Load().Projects.Single(p => p.Id == second.Id).Text.Contains("Poslední změna"), "Exit saves the latest edit and disposes the app");
            NotesState after = store.Load();
            Check(store.ImageFiles(after.Projects[1]).Length == 2, "Images persist across saving and restarting");
        }
        Point clamped = FloatingButton.Clamp(new Point(int.MaxValue / 2, int.MaxValue / 2), new Size(54, 54));
        Check(Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(new Rectangle(clamped, new Size(54, 54)))), "An off-screen overlay position is brought back onto a display");
    }

    /// <summary>Enumerate descendant controls for size, caption, and accessibility assertions.</summary>
    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>Calculate relative sRGB luminance for control contrast measurements.</summary>
    private static double Luminance(Color color)
    {
        double[] rgb = { color.R / 255d, color.G / 255d, color.B / 255d };
        for (int i = 0; i < rgb.Length; i++) rgb[i] = rgb[i] <= 0.04045 ? rgb[i] / 12.92 : Math.Pow((rgb[i] + 0.055) / 1.055, 2.4);
        return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
    }

    /// <summary>Return the contrast ratio between two colors from their relative luminance.</summary>
    private static double Contrast(Color foreground, Color background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Send a native mouse message only to the specified isolated test control.</summary>
    private static void Mouse(Control control, int message, int x, int y, int buttons)
    {
        ScrollNative.SendMessage(control.Handle, message, new IntPtr(buttons), new IntPtr((y << 16) | (x & 0xffff)));
    }

    /// <summary>Verify contrast, minimum size, custom scrollbars, and their input behavior.</summary>
    private static void TestDesign(string directory, string preview)
    {
        NotesStore store = new NotesStore(directory);
        NotesState state = store.Load();
        state.OverlayVisible = false;
        using (MainForm form = new MainForm(store, state, delegate { }))
        {
            form.ErrorReported = delegate { };
            form.Show(); Application.DoEvents();
            Button rename = Descendants(form).OfType<RenameButton>().Single();
            Check(rename.Text == "" && rename.Width == rename.Height && form.tips.GetToolTip(rename).Contains("F2"), "Rename is a small square pencil icon with an accessible name and tooltip");
            Control[] texts = Descendants(form).Where(c => c is Label || c is TextBoxBase || c is Button || c is ListBox).ToArray();
            double minimum = texts.Min(c => Contrast(c.ForeColor, c.BackColor));
            Check(texts.All(c => c.BackColor.A == 255 && c.BackgroundImage == null) && minimum >= 4.5,
                "Editor and controls have solid backgrounds and at least 4.5:1 contrast (minimum " + minimum.ToString("F2") + ":1)");
            Check(Descendants(form).OfType<Button>().All(b => Contrast(b.ForeColor, b.FlatAppearance.MouseOverBackColor) >= 4.5 && Contrast(b.ForeColor, b.FlatAppearance.MouseDownBackColor) >= 4.5)
                && Contrast(Color.White, Color.FromArgb(82, 32, 31)) >= 4.5, "Hover, pressed and selected-project text remain readable");
            Check(!form.ProjectScroll.Visible && !form.NoteScroll.Visible, "Short project lists and notes have no unnecessary scrollbar");

            form.AddProject("Velmi dlouhý název projektu – poznámky k návrhu, obrázkům a jednotlivým úkolům");
            for (int i = 3; i <= 40; i++)
            {
                Project project = new Project { Name = "Projekt " + i.ToString("D2") };
                state.Projects.Add(project);
                form.Projects.Items.Add(project);
            }
            form.RebuildProjects(state.Projects[1].Id);
            form.Size = form.MinimumSize;
            form.Editor.Text = string.Join("\r\n", Enumerable.Range(1, 120).Select(i => "- Bod " + i + ": Čitelná poznámka s českými znaky."));
            Application.DoEvents();
            Check(form.ProjectScroll.Visible && form.NoteScroll.Visible, "Both branded scrollbars appear when content overflows at the minimum window size");
            Check(form.Editor.ClientSize.Height > 100 && form.Editor.ClientSize.Width > 250 && rename.Width == rename.Height,
                "The icon and text layout stay usable in the minimum window size with DPI scaling");
            form.ProjectScroll.ScrollTo(form.ProjectScroll.Maximum);
            Check(form.Projects.TopIndex == form.ProjectScroll.Maximum && form.Projects.TopIndex + form.Projects.VisibleItems >= form.Projects.Items.Count,
                "The project scrollbar reaches the last project");
            form.ProjectScroll.ScrollTo(0);
            ScrollNative.SendMessage(form.Projects.Handle, 0x020a, new IntPtr(-120 << 16), IntPtr.Zero);
            Application.DoEvents();
            Check(form.Projects.TopIndex > 0 && form.ProjectScroll.Value == form.Projects.TopIndex, "The project scrollbar follows mouse wheel scrolling");
            form.Projects.SelectedIndex = 0;
            form.Projects.SelectedIndex = form.Projects.Items.Count - 1;
            Application.DoEvents();
            Check(form.ProjectScroll.Value == form.Projects.TopIndex && form.Projects.TopIndex + form.Projects.VisibleItems >= form.Projects.Items.Count,
                "Selecting the last project scrolls it into view and updates the thumb");
            form.Projects.SelectedItem = state.Projects[1];
            form.NoteScroll.ScrollTo(form.NoteScroll.Maximum);
            Point last = form.Editor.GetPositionFromCharIndex(form.Editor.TextLength - 1);
            Check(form.Editor.FirstVisibleLine == form.NoteScroll.Maximum && last.Y >= 0 && last.Y + form.Editor.Font.Height <= form.Editor.ClientSize.Height,
                "The note scrollbar reaches the complete final line");
            form.NoteScroll.ScrollTo(0);
            ScrollNative.SendMessage(form.Editor.Handle, 0x020a, new IntPtr(-120 << 16), IntPtr.Zero);
            Application.DoEvents();
            Check(form.Editor.FirstVisibleLine > 0 && form.NoteScroll.Value == form.Editor.FirstVisibleLine, "The note scrollbar follows mouse wheel scrolling");
            form.NoteScroll.ScrollTo(0);
            Rectangle thumb = form.NoteScroll.Thumb;
            Mouse(form.NoteScroll, 0x0201, thumb.Left + 2, thumb.Top + 3, 1);
            Mouse(form.NoteScroll, 0x0200, thumb.Left + 2, form.NoteScroll.Height + 10, 1);
            Mouse(form.NoteScroll, 0x0202, thumb.Left + 2, form.NoteScroll.Height + 10, 0);
            Check(form.Editor.FirstVisibleLine == form.NoteScroll.Maximum, "Dragging the branded thumb reaches the bottom of the note");
            form.NoteScroll.ScrollTo(0);
            Mouse(form.NoteScroll, 0x0201, 5, form.NoteScroll.Height - 5, 1);
            Mouse(form.NoteScroll, 0x0202, 5, form.NoteScroll.Height - 5, 0);
            Check(form.Editor.FirstVisibleLine == form.NoteScroll.Page, "Clicking the scrollbar track moves by a page");
            ScrollNative.SendMessage(form.NoteScroll.Handle, 0x0100, new IntPtr((int)Keys.End), IntPtr.Zero);
            Check(form.Editor.FirstVisibleLine == form.NoteScroll.Maximum, "The branded scrollbar supports keyboard navigation");
            form.Editor.Text = new string('w', 5000);
            Application.DoEvents();
            Check(form.Editor.Lines.Length == 1 && form.Editor.VisualLineCount > form.Editor.VisibleLines && form.NoteScroll.Visible,
                "A long wrapped paragraph gets a scrollbar based on visual lines");
            form.Editor.Text = string.Join("\r\n", Enumerable.Range(1, 120).Select(i => "- Bod " + i + ": Čitelná poznámka s českými znaky."));
            form.NoteScroll.ScrollTo(8);
            form.ProjectScroll.ScrollTo(2);
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Editor.Text = "Krátká poznámka";
            state.Projects.RemoveRange(2, state.Projects.Count - 2);
            form.RebuildProjects(state.Projects[1].Id);
            Application.DoEvents();
            Check(!form.NoteScroll.Visible && !form.ProjectScroll.Visible, "Scrollbars disappear again when the content fits");

            form.Flush();
            using (FileStream locked = new FileStream(Path.Combine(store.ProjectDirectory(state.Projects[1]), "poznamky.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                form.Editor.AppendText(" – neuložená změna");
                bool saved = form.Flush();
                Label errorStatus = Descendants(form).OfType<Label>().Single(l => l.Text.StartsWith("Neuloženo"));
                Check(!saved && Contrast(errorStatus.ForeColor, errorStatus.BackColor) >= 4.5, "A save error uses a readable light red status while retaining the edit");
            }
            form.ExitApplication();
        }
    }

    /// <summary>Verify sorting, search, renaming, and safe project/image deletion.</summary>
    private static void TestProjectActions(string directory)
    {
        NotesStore store = new NotesStore(directory);
        NotesState state = store.Load();
        state.OverlayVisible = false;
        using (MainForm form = new MainForm(store, state, delegate { }))
        {
            form.ErrorReported = delegate(string message) { throw new Exception(message); };
            form.Show(); Application.DoEvents();
            Project yellow = form.AddProject("Žlutý projekt");
            form.Editor.Text = "Moje rozepsaná poznámka.";
            form.AddProject("Alfa"); form.AddProject("ábel"); form.AddProject("Chata"); form.AddProject("Hotel");
            Check(form.Projects.Items.Cast<Project>().Select(p => p.Name).SequenceEqual(new string[] { "ábel", "Alfa", "Hotel", "Chata", "Obecné", "Žlutý projekt" }), "Project names use Czech alphabetical order including Ch after H");
            form.ProjectSearch.Text = "ZLUTY";
            Check(form.Projects.Items.Count == 1 && form.Projects.SelectedItem == yellow && form.Editor.Text.Contains("rozepsaná"), "Search ignores case and diacritics and retains the matching project's note");
            form.ProjectSearch.Text = "nenalezeno";
            Check(form.Projects.Items.Count == 0 && !form.Editor.Enabled && yellow.Text.Contains("rozepsaná"), "A search with no results disables editing without losing the previous note");
            form.ProjectSearch.Clear();
            Check(form.Projects.Items.Count == 6 && form.Projects.SelectedItem == yellow, "Clearing search restores the selected project");
            form.RenameSelectedProject("Aardvark");
            Check(form.Projects.Items[0] == yellow && form.Projects.SelectedItem == yellow && store.Load().Projects.Single(p => p.Id == yellow.Id).Name == "Aardvark", "Renaming sorts immediately and preserves selection and storage");
            Button add = Descendants(form).OfType<Button>().Single(b => b.Text == "Nový projekt");
            Check(form.Text == Program.WindowTitle && !Descendants(form).OfType<Label>().Any(l => l.Text == "Marks Marks" || l.Text == Program.Title || l.Text == "Hledat projekt"),
                "The application name appears only in the title bar and search has no separate label");
            Check(add.PointToScreen(Point.Empty).Y - form.Projects.PointToScreen(new Point(0, form.Projects.Height)).Y >= 12, "New project has no plus sign and has a gap above it");
            Button[] icons = Descendants(form).OfType<Button>().Where(b => b is ExportButton || b is RenameButton || b is DeleteButton).ToArray();
            Check(icons.Length == 3 && icons.All(b => b.PointToScreen(Point.Empty).Y > form.Editor.PointToScreen(new Point(0, form.Editor.Height)).Y && b.Text == "" && b.Width == b.Height),
                "Export, rename and delete are small icons in the bottom bar, leaving the full header width for the project name");
            string first, second;
            using (Bitmap sample = new Bitmap(120, 80))
            {
                using (Graphics graphics = Graphics.FromImage(sample)) graphics.Clear(Color.DarkCyan);
                first = form.AttachImage(sample);
                second = form.AttachImage(sample);
            }
            form.Editor.AppendText("\r\nOpakovaný odkaz: [Snímek: " + Path.GetFileName(first) + "]");
            Check(form.DeleteImage(first) && !File.Exists(first) && store.ImageFiles(yellow).Length == 1 && !form.Editor.Text.Contains(Path.GetFileName(first)) && form.Editor.Text.Contains(Path.GetFileName(second)),
                "Deleting one image removes its file, thumbnail and all note markers while retaining other images");
            string note = form.Editor.Text;
            bool refused = false;
            using (FileStream locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None))
                try { form.DeleteImage(second); } catch (IOException) { refused = true; }
            Check(refused && File.Exists(second) && form.Editor.Text == note && yellow.Text == note, "A locked image cannot be deleted and leaves its note and file intact");
            string outside = Path.Combine(directory, "outside.png");
            File.Copy(second, outside);
            refused = false;
            try { form.DeleteImage(outside); } catch (InvalidOperationException) { refused = true; }
            Check(refused && File.Exists(outside) && form.Editor.Text == note, "Image deletion refuses a file outside the selected project");
            string projectFolder = store.ProjectDirectory(yellow);
            form.Flush();
            refused = false;
            using (FileStream locked = new FileStream(Path.Combine(directory, "poznamky.xml"), FileMode.Open, FileAccess.Read, FileShare.Read))
                try { form.DeleteProject(yellow); } catch (IOException) { refused = true; }
            Check(refused && state.Projects.Contains(yellow) && Directory.Exists(projectFolder) && File.Exists(second) && Directory.GetDirectories(directory, ".mazani-*").Length == 0,
                "A failed deletion commit restores the project folder, images and selection");
            Check(form.DeleteProject(yellow) && !Directory.Exists(projectFolder) && !store.Load().Projects.Any(p => p.Id == yellow.Id), "Deleting a project removes its folder, notes, images and index entry");
            File.WriteAllText(Path.Combine(directory, "poznamky.xml"), "damaged");
            Check(new NotesStore(directory).Load().Projects.Count == 5, "The recovery index never references a deleted project's folder");
            while (state.Projects.Count != 0) form.DeleteProject(state.Projects[0]);
            Check(store.Load().Projects.Count == 0 && form.Projects.Items.Count == 0 && !form.Editor.Enabled && form.Pictures.Controls.Count == 0, "Deleting the final project leaves a persistent empty project list");
            Project fresh = form.AddProject("Nový začátek");
            Check(form.Editor.Enabled && form.Projects.SelectedItem == fresh && store.Load().Projects.Count == 1, "A new project can be created after deleting every project");
            form.ExitApplication();
        }
    }

    /// <summary>Verify image pixels, zoom, dragging, navigation, file release, and overlay alpha transparency.</summary>
    private static void TestViewer(string directory, string preview)
    {
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "snimek-001.png"), second = Path.Combine(directory, "snimek-002.png");
        using (Bitmap bitmap = new Bitmap(1600, 1200))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(34, 44, 54));
                using (Pen pen = new Pen(Color.FromArgb(71, 87, 104), 2))
                {
                    for (int x = 0; x < bitmap.Width; x += 80) graphics.DrawLine(pen, x, 0, x, bitmap.Height);
                    for (int y = 0; y < bitmap.Height; y += 80) graphics.DrawLine(pen, 0, y, bitmap.Width, y);
                }
                using (Font font = new Font("Segoe UI", 90, FontStyle.Bold)) graphics.DrawString("Mark´s Marks", font, Brushes.White, 200, 460);
            }
            bitmap.Save(first, System.Drawing.Imaging.ImageFormat.Png);
        }
        using (Bitmap bitmap = new Bitmap(1000, 800))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.DarkOrange);
            bitmap.Save(second, System.Drawing.Imaging.ImageFormat.Png);
        }
        using (ImageViewer viewer = new ImageViewer(new string[] { first, second }, first, delegate(string file) { File.Delete(file); return true; }))
        {
            viewer.ErrorReported = delegate { };
            viewer.Show(); Application.DoEvents();
            Check(viewer.Canvas.Image.Width == 1600 && viewer.CurrentIndex == 0 && !viewer.Previous.Enabled && viewer.Next.Enabled, "The viewer opens the selected image and enables valid navigation directions");
            float initial = viewer.Canvas.Zoom;
            viewer.Canvas.ZoomAt(new Point(viewer.Canvas.Width / 2, viewer.Canvas.Height / 2), 120);
            Check(viewer.Canvas.Zoom > initial, "Mouse-wheel zoom enlarges the image");
            viewer.Canvas.OriginalSize();
            Point anchor = new Point(viewer.Canvas.Width / 2 + 20, viewer.Canvas.Height / 2 + 20);
            RectangleF before = viewer.Canvas.ImageBounds;
            float imageX = (anchor.X - before.X) / viewer.Canvas.Zoom, imageY = (anchor.Y - before.Y) / viewer.Canvas.Zoom;
            viewer.Canvas.ZoomAt(anchor, 120);
            RectangleF after = viewer.Canvas.ImageBounds;
            Check(Math.Abs(after.X + imageX * viewer.Canvas.Zoom - anchor.X) < 1 && Math.Abs(after.Y + imageY * viewer.Canvas.Zoom - anchor.Y) < 1, "Zoom keeps the point under the mouse in place");
            float zoom = viewer.Canvas.Zoom;
            ScrollNative.SendMessage(viewer.Canvas.Handle, 0x020a, new IntPtr(-120 << 16), IntPtr.Zero);
            Check(viewer.Canvas.Zoom < zoom, "A native wheel message also zooms out");
            viewer.Canvas.OriginalSize();
            Mouse(viewer.Canvas, 0x0201, 300, 300, 1);
            Mouse(viewer.Canvas, 0x0200, 250, 250, 1);
            Mouse(viewer.Canvas, 0x0202, 250, 250, 0);
            Check(viewer.Canvas.Pan.X < 0 && viewer.Canvas.Pan.Y < 0, "Dragging pans an enlarged image");
            viewer.Next.PerformClick();
            Check(viewer.Visible && !viewer.IsDisposed && viewer.CurrentIndex == 1 && viewer.Canvas.Image.Width == 1000 && viewer.Previous.Enabled && !viewer.Next.Enabled,
                "Navigation changes images without closing the viewer");
            using (FileStream unlocked = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(unlocked.Length > 0, "The viewer releases disk files after loading an image");
            viewer.Previous.PerformClick();
            File.Copy(second, second + ".backup");
            File.WriteAllText(second, "broken image");
            Check(!viewer.ShowAt(1) && viewer.CurrentIndex == 0 && viewer.Canvas.Image.Width == 1600, "An unreadable next image preserves the current image and position");
            File.Copy(second + ".backup", second, true);
            viewer.Canvas.Fit();
            Check(Math.Abs(viewer.Canvas.Zoom - initial) < .01 && viewer.Canvas.Pan == PointF.Empty, "Fit restores the complete image and clears panning");
            using (Bitmap bitmap = new Bitmap(viewer.Width, viewer.Height))
            {
                viewer.DrawToBitmap(bitmap, new Rectangle(Point.Empty, viewer.Size));
                bitmap.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
            }
            Check(viewer.DeleteCurrent() && !File.Exists(first) && viewer.Visible && viewer.Canvas.Image.Width == 1000 && !viewer.Previous.Enabled && !viewer.Next.Enabled,
                "Deleting the displayed image moves to the remaining image without closing the viewer");
            Check(viewer.DeleteCurrent() && !File.Exists(second) && viewer.IsDisposed, "Deleting the final image closes the empty viewer");
        }
        using (System.IO.Stream resource = typeof(MainForm).Assembly.GetManifestResourceStream("MarksMarks.BrandIcon"))
        using (Image logo = Image.FromStream(resource))
        using (Bitmap bitmap = LayeredOverlay.Render(logo, new Size(81, 81)))
        using (FloatingButton overlay = new FloatingButton(new NotesState(), logo, delegate { }, delegate { }))
        {
            Check(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(40, 40).A > 0, "The overlay keeps transparent corners and the original logo alpha");
            overlay.Show(); Application.DoEvents();
            Check((LayeredOverlay.WindowStyle(overlay.Handle, -20) & 0x00080000) != 0, "The overlay uses Windows per-pixel transparency without a red backing");
        }
    }

    /// <summary>Return true for expected invalid-archive rejection; let unexpected exceptions fail the test.</summary>
    private static bool RefusesImport(NotesStore store, NotesState state, string file)
    {
        try { ProjectArchive.Import(store, state, file); return false; }
        catch (InvalidDataException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.Text.DecoderFallbackException) { return true; }
    }

    /// <summary>Verify ZIP format 1, exact data transfer, naming conflicts, traversal, XML entities, limits, and rollback.</summary>
    private static void TestArchive(string directory)
    {
        Directory.CreateDirectory(directory);
        NotesStore source = new NotesStore(Path.Combine(directory, "source"));
        NotesState state = source.Load();
        Project project = state.Projects[0];
        project.Name = "Tým – návrh 📝";
        source.Save(state);
        using (Bitmap bitmap = new Bitmap(120, 80))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.DarkCyan);
            source.SaveImage(project, bitmap); source.SaveImage(project, bitmap);
        }
        project.Text = "Žluťoučký kůň 📝\r\n\t- Návrh: [Snímek: snimek-001.png]\r\n- Druhý: [Snímek: snimek-002.png]";
        source.Save(state);
        string package = Path.Combine(directory, "tym.zip");
        ProjectArchive.Export(source, project, package);
        using (ZipArchive zip = ZipFile.OpenRead(package))
            Check(zip.Entries.Count == 4 && zip.GetEntry("projekt.xml") != null && zip.GetEntry("poznamky.txt") != null && zip.GetEntry("snimky/snimek-002.png") != null,
                "Export contains one project with its name, plain text and every PNG image");
        NotesStore target = new NotesStore(Path.Combine(directory, "target"));
        NotesState existing = target.Load();
        existing.Projects[0].Text = "Moje původní data";
        target.Save(existing);
        byte[] originalIndex = File.ReadAllBytes(Path.Combine(target.DirectoryPath, "poznamky.xml"));
        Project imported = ProjectArchive.Import(target, existing, package);
        Check(imported.Name == project.Name && imported.Id != project.Id && imported.Text == project.Text && target.Load().Projects.Count == 2,
            "Import preserves Unicode, exact newlines and image markers while creating an independent project");
        Check(target.ImageFiles(imported).Length == 2 && File.ReadAllBytes(target.ImageFiles(imported)[0]).SequenceEqual(File.ReadAllBytes(source.ImageFiles(project)[0])),
            "Transferred images retain their original PNG bytes and filenames");
        Check(target.Load().Projects[0].Text == "Moje původní data", "Import keeps the receiving user's existing project unchanged");
        Project duplicate = ProjectArchive.Import(target, existing, package);
        Check(duplicate.Name == project.Name + " (2)" && duplicate.Folder != imported.Folder && target.Load().Projects.Count == 3,
            "Repeated imports get a distinct name and folder without overwriting existing data");
        bool refused = false;
        using (FileStream locked = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { ProjectArchive.Export(source, project, package); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(package) && Directory.GetFiles(directory, "tym.zip.tmp-*").Length == 0, "A locked export target keeps its original archive and removes temporary files");
        refused = false;
        try { ProjectArchive.Export(source, project, Path.Combine(source.ProjectDirectory(project), "export.zip")); } catch (IOException) { refused = true; }
        Check(refused && source.Load().Projects[0].Text == project.Text, "Export cannot overwrite a file inside the source project's folder");

        int count = existing.Projects.Count;
        byte[] beforeIndex = File.ReadAllBytes(Path.Combine(target.DirectoryPath, "poznamky.xml"));
        string bad = Path.Combine(directory, "bad.zip");
        File.Copy(package, bad);
        using (ZipArchive zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        using (Stream stream = zip.CreateEntry("snimky/../../outside.png").Open()) stream.WriteByte(1);
        Check(RefusesImport(target, existing, bad) && existing.Projects.Count == count && !File.Exists(Path.Combine(directory, "outside.png")), "Import rejects traversal paths before creating any project");
        File.Copy(package, bad, true);
        using (ZipArchive zip = ZipFile.Open(bad, ZipArchiveMode.Update))
            zip.GetEntry("poznamky.txt").Delete();
        Check(RefusesImport(target, existing, bad), "Import rejects an archive missing its note");
        File.Copy(package, bad, true);
        using (ZipArchive zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        {
            zip.GetEntry("snimky/snimek-001.png").Delete();
            using (Stream stream = zip.CreateEntry("snimky/snimek-001.png").Open()) stream.WriteByte(1);
        }
        Check(RefusesImport(target, existing, bad), "Import rejects a damaged image and cleans up its partially extracted files");
        File.Copy(package, bad, true);
        using (ZipArchive zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        using (Stream stream = zip.CreateEntry("snimky/SNIMEK-001.PNG").Open()) stream.WriteByte(1);
        Check(RefusesImport(target, existing, bad), "Import rejects duplicate paths regardless of filename case");
        File.Copy(package, bad, true);
        using (ZipArchive zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        {
            zip.GetEntry("projekt.xml").Delete();
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("projekt.xml").Open()))
                writer.Write("<!DOCTYPE ProjectPackage [<!ENTITY file SYSTEM 'file:///C:/Windows/win.ini'>]><ProjectPackage><Version>1</Version><Name>&file;</Name></ProjectPackage>");
        }
        Check(RefusesImport(target, existing, bad), "Import refuses external XML entities");
        File.Copy(package, bad, true);
        byte[] bytes = File.ReadAllBytes(bad);
        int central = -1;
        for (int i = 0; i < bytes.Length - 28; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2) { central = i; break; }
        Array.Copy(BitConverter.GetBytes(1073741825U), 0, bytes, central + 24, 4);
        File.WriteAllBytes(bad, bytes);
        Check(RefusesImport(target, existing, bad), "Import rejects oversized unpacked data before extraction");
        refused = false;
        using (FileStream locked = new FileStream(Path.Combine(target.DirectoryPath, "poznamky.xml"), FileMode.Open, FileAccess.Read, FileShare.Read))
            try { ProjectArchive.Import(target, existing, package); } catch (IOException) { refused = true; }
        Check(refused && existing.Projects.Count == count && File.ReadAllBytes(Path.Combine(target.DirectoryPath, "poznamky.xml")).SequenceEqual(beforeIndex)
            && Directory.GetDirectories(target.DirectoryPath, ".import-*").Length == 0 && Directory.GetDirectories(target.DirectoryPath).Length == count,
            "A failed import commit leaves the index and existing projects intact with no imported or temporary folder");
        using (MainForm form = new MainForm(target, target.Load(), delegate { }))
        {
            form.ErrorReported = delegate(string message) { throw new Exception(message); };
            form.Show(); Application.DoEvents();
            form.ProjectSearch.Text = "nenalezeno";
            Project added = form.ImportProject(package);
            Check(form.ProjectSearch.Text == "" && form.Projects.SelectedItem == added && form.Pictures.Controls.Count == 2, "Import selects the received project, clears search and shows its image thumbnails");
            Check(form.ExportProject(Path.Combine(directory, "gui.zip")) && File.Exists(Path.Combine(directory, "gui.zip")), "The editor can export its selected project after saving pending edits");
            form.ExitApplication();
        }
    }
}
