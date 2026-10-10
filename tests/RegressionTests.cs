using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ProjektovePoznamky;

/// <summary>Storage, viewer, accessibility, and layout regression checks over isolated data.</summary>
internal static partial class Tests
{
    /// <summary>Run regression cases without accessing user notes or the system clipboard.</summary>
    private static void TestRegressions(string root)
    {
        Ui.SetLanguage("en");
        TestJournalRecovery(Path.Combine(root, "journal"));
        TestDamagedViewers(Path.Combine(root, "viewers"));
        TestLongPaths(Path.Combine(root, "paths"));
        TestCopyRollback(Path.Combine(root, "copy"));
        TestPreferenceRecovery(Path.Combine(root, "preference"));
        TestDirtySave(Path.Combine(root, "save"));
        TestUnifiedActions(Path.Combine(root, "buttons"));
    }

    /// <summary>Create a durable intent; a fresh store instance simulates restarting after interruption.</summary>
    private static string StartIntent(NotesStore store, StoreOperation intent)
    {
        return (string)typeof(NotesStore).GetMethod("BeginOperation", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(store, new object[] { intent });
    }

    /// <summary>Verify interruption points before/after moving data, writing notes/index, and committing.</summary>
    private static void TestJournalRecovery(string root)
    {
        for (int phase = 0; phase <= 3; phase++)
        {
            string directory = Path.Combine(root, "project-" + phase);
            NotesStore store = new NotesStore(directory); NotesState state = store.Load();
            Project project = state.Projects[0]; project.Text = "Original project";
            state.Projects.Add(new Project { Name = "Other", Text = "Other note" }); store.Save(state);
            string folder = store.ProjectDirectory(project);
            string operation = StartIntent(store, new StoreOperation { Kind = "project-delete", Folder = project.Folder });
            if (phase >= 1) Directory.Move(folder, Path.Combine(operation, "projekt"));
            if (phase >= 2) { state.Projects.Remove(project); state.SelectedId = state.Projects[0].Id; store.Save(state); File.Copy(Path.Combine(directory, "poznamky.xml"), Path.Combine(directory, "poznamky.xml.bak"), true); }
            if (phase == 3) NotesStore.WriteAtomic(Path.Combine(operation, "dokonceno"), new byte[0]);
            NotesStore restarted = new NotesStore(directory); NotesState loaded = restarted.Load();
            Check(restarted.Recovered && loaded.Projects.Count == (phase == 3 ? 1 : 2) && loaded.Projects.Any(p => p.Text == "Other note")
                && (phase == 3 ? !Directory.Exists(folder) : loaded.Projects.Any(p => p.Id == project.Id && p.Text == "Original project")) && !Directory.Exists(operation),
                "project deletion recovers phase " + phase + " without losing other projects");
            Check(new NotesStore(directory).Load().Projects.Count == loaded.Projects.Count, "project recovery is idempotent, phase " + phase);
        }
        foreach (string kind in new[] { "attachment-rename", "attachment-delete" })
            for (int phase = 0; phase <= 4; phase++)
            {
                string directory = Path.Combine(root, kind + "-" + phase);
                NotesStore store = new NotesStore(directory); NotesState state = store.Load(); store.Save(state);
                Project project = state.Projects[0]; string image;
                using (Bitmap bitmap = new Bitmap(20, 20)) image = store.SaveImage(project, bitmap);
                byte[] bytes = File.ReadAllBytes(image); string oldLeaf = Path.GetFileName(image), newLeaf = "Named.png";
                project.Text = "Keep [Image: " + oldLeaf + "] text"; store.Save(state); string before = project.Text;
                string operation = StartIntent(store, new StoreOperation { Kind = kind, Folder = project.Folder, Media = "snimky", Source = oldLeaf, Target = newLeaf });
                string pending = Path.Combine(operation, "priloha"), target = Path.Combine(Path.GetDirectoryName(image), newLeaf);
                if (phase >= 1) File.Move(image, pending);
                if (phase >= 2 && kind == "attachment-rename") File.Move(pending, target);
                if (phase >= 3) { project.Text = kind == "attachment-rename" ? before.Replace(oldLeaf, newLeaf) : before.Replace("[Image: " + oldLeaf + "]", ""); store.Save(state); }
                if (phase == 4) NotesStore.WriteAtomic(Path.Combine(operation, "dokonceno"), new byte[0]);
                NotesStore restarted = new NotesStore(directory); NotesState loaded = restarted.Load();
                Check(restarted.Recovered && loaded.Projects[0].Text == (phase == 4 ? project.Text : before) && !Directory.Exists(operation)
                    && (phase < 4 ? File.Exists(image) && File.ReadAllBytes(image).SequenceEqual(bytes) : kind == "attachment-rename" ? File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes) : !File.Exists(image)),
                    "" + kind + " recovers phase " + phase + " with matching file and note");
            }
        string lockedRoot = Path.Combine(root, "committed-cleanup");
        NotesStore lockedStore = new NotesStore(lockedRoot); NotesState lockedState = lockedStore.Load(); lockedStore.Save(lockedState);
        string picture; using (Bitmap bitmap = new Bitmap(20, 20)) picture = lockedStore.SaveImage(lockedState.Projects[0], bitmap);
        string journal = StartIntent(lockedStore, new StoreOperation { Kind = "attachment-delete", Folder = lockedState.Projects[0].Folder, Media = "snimky", Source = Path.GetFileName(picture) });
        string staged = Path.Combine(journal, "priloha"); File.Move(picture, staged); NotesStore.WriteAtomic(Path.Combine(journal, "dokonceno"), new byte[0]);
        bool refused = false;
        NotesStore waitingStore = new NotesStore(lockedRoot);
        NotesState waiting = null;
        using (FileStream locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            waiting = waitingStore.Load();
            Check(waitingStore.CleanupPending && File.Exists(Path.Combine(journal, "dokonceno")), "deferred committed cleanup retains its marker and allows startup");
            waiting.Projects[0].Text = "New work during deferred cleanup"; waitingStore.Save(waiting);
            Check(waitingStore.CleanupPending && File.ReadAllText(Path.Combine(waitingStore.ProjectDirectory(waiting.Projects[0]), "poznamky.txt")) == waiting.Projects[0].Text,
                "new work saves normally while a committed cleanup remains locked");
            using (MainForm form = new MainForm(waitingStore, waiting))
            {
                int errors = 0; form.ErrorReported = delegate { errors++; };
                Label status = (Label)typeof(MainForm).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Check(form.Flush() && errors == 0 && status.Text.StartsWith("Saved; file cleanup pending"), "UI: committed cleanup is a saved status without a save-error dialog");
                Ui.SetLanguage("cs");
                Check(status.Text.StartsWith("Uloženo; úklid souborů čeká") && form.Editor.Text == waiting.Projects[0].Text, "UI: deferred cleanup status follows language and preserves editor text");
                Ui.SetLanguage("en");
            }
        }
        waitingStore.Save(waiting);
        Check(!waitingStore.CleanupPending && File.ReadAllText(Path.Combine(waitingStore.ProjectDirectory(waiting.Projects[0]), "poznamky.txt.bak")) == "",
            "completing deferred cleanup does not rewrite unchanged notes or replace their meaningful backup");
        NotesState cleaned = new NotesStore(lockedRoot).Load();
        Check(cleaned.Projects.Count == 1 && cleaned.Projects[0].Text == "New work during deferred cleanup" && !Directory.Exists(journal) && !File.Exists(picture), "retry completes cleanup without rolling back a committed deletion or later work");
        string damagedRoot = Path.Combine(root, "damaged-snapshot");
        NotesStore damagedStore = new NotesStore(damagedRoot); NotesState damagedState = damagedStore.Load(); damagedStore.Save(damagedState);
        string source; using (Bitmap bitmap = new Bitmap(20, 20)) source = damagedStore.SaveImage(damagedState.Projects[0], bitmap);
        byte[] imageBytes = File.ReadAllBytes(source);
        string damagedJournal = StartIntent(damagedStore, new StoreOperation { Kind = "attachment-rename", Folder = damagedState.Projects[0].Folder, Media = "snimky", Source = Path.GetFileName(source), Target = "target.png" });
        byte[] validSnapshot = File.ReadAllBytes(Path.Combine(damagedJournal, "pred"));
        string moved = Path.Combine(Path.GetDirectoryName(source), "target.png"); File.Move(source, moved);
        File.WriteAllBytes(Path.Combine(damagedJournal, "pred"), new byte[] { 255, 255 }); refused = false;
        try { new NotesStore(damagedRoot).Load(); } catch (InvalidDataException) { refused = true; }
        Check(refused && Directory.Exists(damagedJournal) && File.ReadAllBytes(moved).SequenceEqual(imageBytes) && !File.Exists(source),
            "a corrupt recovery snapshot refuses rollback before modifying the remaining media");
        File.WriteAllBytes(Path.Combine(damagedJournal, "pred"), validSnapshot);
        Check(new NotesStore(damagedRoot).Load().Projects.Count == 1 && File.Exists(source) && !Directory.Exists(damagedJournal), "repairing only the snapshot permits an idempotent safe recovery");
    }

    /// <summary>Keep a valid remaining path and clear old pixels even when the next file is damaged.</summary>
    private static void TestDamagedViewers(string root)
    {
        Directory.CreateDirectory(root); string bad = Path.Combine(root, "bad.png"), good = Path.Combine(root, "good.png");
        File.WriteAllText(bad, "broken"); using (Bitmap image = new Bitmap(20, 20)) image.Save(good);
        using (ImageViewer viewer = new ImageViewer(new[] { bad, good }, good, delegate(string file) { File.Delete(file); return true; }))
        {
            viewer.ErrorReported = delegate { };
            Check(viewer.DeleteCurrent() && viewer.CurrentIndex == 0 && viewer.Canvas.Image == null && viewer.Text == "bad.png" && !viewer.Previous.Enabled && !viewer.Next.Enabled,
                "image deletion clears old pixels and selects the damaged remaining file safely");
            Check(viewer.DeleteCurrent() && !File.Exists(bad) && viewer.IsDisposed, "the damaged remaining image can be deleted without an index exception");
        }
        bad = Path.Combine(root, "bad.avi"); good = Path.Combine(root, "good.avi"); File.WriteAllText(bad, "broken"); MakeVideo(good);
        using (VideoViewer viewer = new VideoViewer(new[] { bad, good }, good, delegate(string file) { File.Delete(file); return true; }))
        {
            viewer.ErrorReported = delegate { };
            Check(viewer.DeleteCurrent() && viewer.CurrentIndex == 0 && !viewer.Ready && viewer.Text == "bad.avi" && !viewer.Previous.Enabled && !viewer.Next.Enabled,
                "video deletion leaves a valid failed-media state with released playback");
            Check(viewer.DeleteCurrent() && !File.Exists(bad) && viewer.IsDisposed, "the damaged remaining video can be deleted without an index exception");
        }
    }

    /// <summary>Import long display names into short unique folders under a deep root.</summary>
    private static void TestLongPaths(string root)
    {
        Directory.CreateDirectory(root); string archive = Path.Combine(root, "long.zip"), name = new string('a', 300);
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("projekt.xml").Open())) writer.Write("<ProjectPackage><Version>1</Version><Name>" + name + "</Name></ProjectPackage>");
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("poznamky.txt").Open())) writer.Write("long name note");
        }
        string deep = Path.Combine(root, "deep"); while (deep.Length < 175) deep = Path.Combine(deep, "nested");
        NotesStore store = new NotesStore(deep); NotesState state = store.Load(); store.Save(state);
        Project first = ProjectArchive.Import(store, state, archive), second = ProjectArchive.Import(store, state, archive);
        Check(first.Name == name && second.Name.StartsWith(name) && first.Folder != second.Folder && first.Folder.Length <= 64 && store.Load().Projects.Count == 3,
            "a 300-character display name imports twice with short unique folders in a deep root");
        string image; using (Bitmap bitmap = new Bitmap(20, 20)) image = store.SaveImage(first, bitmap);
        string renamed = store.RenameImage(state, first, image, "short");
        Check(File.Exists(renamed) && Path.Combine(store.ProjectDirectory(first), "poznamky.txt.bak").Length <= 259, "deep project paths reserve space for images, journals and note backups");
        bool refused = false; try { store.RenameImage(state, first, renamed, new string('z', 250)); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(renamed) && Directory.GetDirectories(deep, ".operace-*").Length == 0, "an attachment that cannot fit the root is refused before creating a journal or moving data");
    }

    /// <summary>Clean up only owned destination files after preparation or late index failures.</summary>
    private static void TestCopyRollback(string root)
    {
        NotesStore source = new NotesStore(Path.Combine(root, "source")); NotesState state = source.Load(); state.Projects[0].Text = "copy text"; source.Save(state);
        using (Bitmap bitmap = new Bitmap(20, 20)) source.SaveImage(state.Projects[0], bitmap);
        string original = Path.Combine(root, "original.avi"); MakeVideo(original); string video = source.SaveVideo(state.Projects[0], original);
        NotesStore target = new NotesStore(Path.Combine(root, "target")); Directory.CreateDirectory(target.DirectoryPath);
        string sentinel = Path.Combine(target.DirectoryPath, "keep.txt"); File.WriteAllText(sentinel, "unrelated"); bool refused = false;
        using (FileStream locked = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None))
            try { target.CopyFrom(source, state); } catch (IOException) { refused = true; }
        Check(refused && !target.HasData && Directory.GetDirectories(target.DirectoryPath).Length == 0 && File.ReadAllText(sentinel) == "unrelated" && File.Exists(video),
            "a failed media copy leaves no partial destination and preserves source plus unrelated files");
        Directory.CreateDirectory(Path.Combine(target.DirectoryPath, "poznamky.xml")); refused = false;
        try { target.CopyFrom(source, state); } catch (IOException) { refused = true; }
        Check(refused && !target.HasData && Directory.GetDirectories(target.DirectoryPath).Length == 1 && File.ReadAllText(sentinel) == "unrelated",
            "a late index failure rolls back every owned project folder and retains a foreign directory");
        Directory.Delete(Path.Combine(target.DirectoryPath, "poznamky.xml")); NotesState copied = target.CopyFrom(source, state);
        Check(copied.Projects[0].Folder == state.Projects[0].Folder && target.Load().Projects[0].Text == "copy text" && target.VideoFiles(copied.Projects[0]).Length == 1,
            "retry uses the original folder name and commits the complete project");
    }

    /// <summary>Use the backup after an invalid primary preference; request selection when both are invalid.</summary>
    private static void TestPreferenceRecovery(string root)
    {
        StorageLocation location = new StorageLocation(root); string first = Path.Combine(root, "first"); location.Save(first); location.Save(Path.Combine(root, "second"));
        File.WriteAllText(Path.Combine(root, "uloziste.txt"), "bad\0path");
        Check(location.Read() == first && File.ReadAllText(Path.Combine(root, "uloziste.txt")) == "bad\0path", "an invalid primary preference falls back without overwriting evidence");
        File.WriteAllText(Path.Combine(root, "uloziste.txt.bak"), "relative-path");
        Check(location.Read() == null, "two invalid preferences request a new storage location");
        File.WriteAllText(Path.Combine(root, "uloziste.txt"), "C:relative-path");
        Check(location.Read() == null, "a drive-relative preference is not mistaken for an absolute storage path");
    }

    /// <summary>Avoid I/O for unchanged locked notes/index and retry failed changed writes.</summary>
    private static void TestDirtySave(string root)
    {
        NotesStore store = new NotesStore(root); NotesState state = store.Load(); state.Projects.Add(new Project { Name = "Other", Text = new string('x', 1024 * 1024) }); store.Save(state);
        string other = Path.Combine(store.ProjectDirectory(state.Projects[1]), "poznamky.txt"); state.Projects[0].Text = "changed";
        using (FileStream locked = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None)) store.Save(state);
        Check(new NotesStore(root).Load().Projects[0].Text == "changed", "a locked unchanged note cannot block saving the edited project");
        using (FileStream lockedNote = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None))
        using (FileStream lockedIndex = new FileStream(Path.Combine(root, "poznamky.xml"), FileMode.Open, FileAccess.Read, FileShare.None)) store.Save(state);
        Check(!File.Exists(other + ".bak"), "unchanged Save performs no note/index reads or writes and keeps existing backups");
        string first = Path.Combine(store.ProjectDirectory(state.Projects[0]), "poznamky.txt"); state.Projects[0].Text = "retry"; bool refused = false;
        using (FileStream locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { store.Save(state); } catch (IOException) { refused = true; }
        store.Save(state);
        Check(refused && new NotesStore(root).Load().Projects[0].Text == "retry", "a failed changed-note commit remains dirty and succeeds on retry");
        Stopwatch watch = Stopwatch.StartNew(); for (int i = 0; i < 3; i++) store.Save(state); watch.Stop();
        Console.WriteLine("MEASURE three unchanged Saves=" + watch.ElapsedMilliseconds + " ms");
    }

    /// <summary>Keep accessible position current and action sizes/spacing consistent.</summary>
    private static void TestUnifiedActions(string root)
    {
        using (BrandScrollBar bar = new BrandScrollBar("Posouvání projektů"))
        {
            bar.Configure(100, 10, 0); bar.ScrollTo(30); bar.Configure(100, 10, 30);
            Check(bar.AccessibleDescription == "Position 31 of 91", "the accessible scrollbar position updates after ScrollTo");
            Ui.SetLanguage("cs"); bar.RefreshLanguage(); Check(bar.AccessibleDescription == "Pozice 31 z 91", "the current accessible position survives language switching"); Ui.SetLanguage("en");
        }
        NotesStore store = new NotesStore(root); NotesState state = store.Load(); state.OverlayVisible = false;
        using (MainForm form = new MainForm(store, state))
        {
            form.ErrorReported = delegate(string error) { throw new Exception(error); }; form.Show(); Application.DoEvents();
            string[] names = { "New project", "Import project…", "Storage…" }; Button[] buttons = names.Select(name => Descendants(form).OfType<Button>().Single(b => b.Text == name)).ToArray();
            int firstGap = buttons[1].Top - buttons[0].Bottom, secondGap = buttons[2].Top - buttons[1].Bottom;
            Check(buttons.Select(b => b.Size).Distinct().Count() == 1 && buttons.Select(b => b.Margin).Distinct().Count() == 1 && firstGap == secondGap,
                "all three sidebar actions have identical sizes, margins and gaps at native Windows DPI");
            Console.WriteLine("MEASURE " + buttons[0].Size + "; gaps=" + firstGap + "/" + secondGap); form.ExitApplication();
        }
    }
}
