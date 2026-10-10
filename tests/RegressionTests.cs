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
        TestEnglishStorage(Path.Combine(root, "english-storage"));
        TestUnifiedActions(Path.Combine(root, "buttons"));
    }

    /// <summary>Create notes, independent backups, an image, and a valid video for layout migration checks.</summary>
    private static NotesStore MigrationFixture(string root, out NotesState state)
    {
        NotesStore store = new NotesStore(root); state = store.Load();
        Project project = state.Projects[0]; project.Text = "Previous note"; store.Save(state);
        string image; using (Bitmap bitmap = new Bitmap(20, 20)) image = store.SaveImage(project, bitmap);
        string source = Path.Combine(root, "source.avi"); MakeVideo(source); string video = store.SaveVideo(project, source);
        project.Name = "Everyday notes";
        project.Text = "Ideas 📝\r\n[Image: " + Path.GetFileName(image) + "]\r\n[Video: " + Path.GetFileName(video) + "]";
        store.Save(state); return store;
    }

    /// <summary>Recreate the exact pre-1.0.1 layout without changing file contents or attachment references.</summary>
    private static void MakeLegacyLayout(NotesStore store, NotesState state)
    {
        foreach (Project project in state.Projects)
        {
            string folder = store.ProjectDirectory(project);
            foreach (string suffix in new[] { "", ".bak" })
                if (File.Exists(Path.Combine(folder, "notes.txt" + suffix))) File.Move(Path.Combine(folder, "notes.txt" + suffix), Path.Combine(folder, "poznamky.txt" + suffix));
            if (Directory.Exists(Path.Combine(folder, "images"))) Directory.Move(Path.Combine(folder, "images"), Path.Combine(folder, "snimky"));
            if (Directory.Exists(Path.Combine(folder, "videos"))) Directory.Move(Path.Combine(folder, "videos"), Path.Combine(folder, "videa"));
        }
        foreach (string suffix in new[] { "", ".bak" })
            if (File.Exists(Path.Combine(store.DirectoryPath, "notes.xml" + suffix))) File.Move(Path.Combine(store.DirectoryPath, "notes.xml" + suffix), Path.Combine(store.DirectoryPath, "poznamky.xml" + suffix));
    }

    /// <summary>Verify new paths, legacy recovery, interrupted migration, collision preservation, and both old ZIP versions.</summary>
    private static void TestEnglishStorage(string root)
    {
        NotesState state; NotesStore store = MigrationFixture(Path.Combine(root, "normal"), out state);
        Project project = state.Projects[0]; string folder = store.ProjectDirectory(project);
        byte[] image = File.ReadAllBytes(store.ImageFiles(project)[0]), video = File.ReadAllBytes(store.VideoFiles(project)[0]);
        byte[] noteBackup = File.ReadAllBytes(Path.Combine(folder, "notes.txt.bak")), indexBackup = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "notes.xml.bak"));
        Check(Path.GetFileName(store.ImageFiles(project)[0]) == "image-001.png" && Directory.Exists(Path.Combine(folder, "images")) && Directory.Exists(Path.Combine(folder, "videos")), "new storage and numbered images use English filenames");
        MakeLegacyLayout(store, state);
        NotesStore migrated = new NotesStore(store.DirectoryPath); Check(migrated.HasData, "legacy storage is recognized before loading");
        NotesState loaded = migrated.Load(); Project restored = loaded.Projects[0];
        Check(migrated.Recovered && restored.Text == project.Text && restored.Id == project.Id && restored.Folder == project.Folder
            && File.ReadAllBytes(migrated.ImageFiles(restored)[0]).SequenceEqual(image) && File.ReadAllBytes(migrated.VideoFiles(restored)[0]).SequenceEqual(video), "migration preserves text, identity, folder names, attachment bytes, and references");
        Check(File.ReadAllBytes(Path.Combine(folder, "notes.txt.bak")).SequenceEqual(noteBackup) && File.ReadAllBytes(Path.Combine(store.DirectoryPath, "notes.xml.bak")).SequenceEqual(indexBackup)
            && !File.Exists(Path.Combine(store.DirectoryPath, "poznamky.xml")) && !File.Exists(Path.Combine(folder, "poznamky.txt")) && !Directory.Exists(Path.Combine(folder, "snimky")) && !Directory.Exists(Path.Combine(folder, "videa")), "migration retains independent backups and removes the old active names");
        NotesStore reopened = new NotesStore(store.DirectoryPath);
        Check(reopened.Load().Projects[0].Text == project.Text && !reopened.Recovered, "English storage migration is idempotent");

        NotesState copyState; NotesStore copySource = MigrationFixture(Path.Combine(root, "copy-source"), out copyState); MakeLegacyLayout(copySource, copyState);
        NotesStore copyTarget = new NotesStore(Path.Combine(root, "copy-target")); NotesState copied = copyTarget.CopyFrom(copySource, copySource.Load(false));
        Check(copied.Projects[0].Text == copyState.Projects[0].Text && copyTarget.ImageFiles(copied.Projects[0]).Length == 1 && copyTarget.VideoFiles(copied.Projects[0]).Length == 1
            && File.Exists(Path.Combine(copySource.DirectoryPath, "poznamky.xml")) && !File.Exists(Path.Combine(copySource.DirectoryPath, "notes.xml")), "copying legacy storage creates English paths while retaining the original layout");

        NotesState conflictState; NotesStore conflict = MigrationFixture(Path.Combine(root, "conflict"), out conflictState); MakeLegacyLayout(conflict, conflictState);
        string conflictFolder = conflict.ProjectDirectory(conflictState.Projects[0]); File.WriteAllText(Path.Combine(conflictFolder, "notes.txt"), "Independent English note");
        bool refused = false; try { new NotesStore(conflict.DirectoryPath).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.ReadAllText(Path.Combine(conflictFolder, "notes.txt")) == "Independent English note" && File.ReadAllText(Path.Combine(conflictFolder, "poznamky.txt")) == conflictState.Projects[0].Text
            && File.Exists(Path.Combine(conflict.DirectoryPath, "poznamky.xml")) && Directory.Exists(Path.Combine(conflictFolder, "snimky")), "a migration conflict preserves both notes and performs no earlier moves");
        File.Delete(Path.Combine(conflictFolder, "notes.txt")); Directory.CreateDirectory(Path.Combine(conflictFolder, "images")); refused = false;
        try { new NotesStore(conflict.DirectoryPath).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(Path.Combine(conflictFolder, "poznamky.txt")) && Directory.Exists(Path.Combine(conflictFolder, "snimky")), "attachment folder conflicts are detected before renaming notes");
        Directory.Delete(Path.Combine(conflictFolder, "images")); File.Copy(Path.Combine(conflict.DirectoryPath, "poznamky.xml"), Path.Combine(conflict.DirectoryPath, "notes.xml")); refused = false;
        try { new NotesStore(conflict.DirectoryPath).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(Path.Combine(conflict.DirectoryPath, "poznamky.xml")) && File.Exists(Path.Combine(conflict.DirectoryPath, "notes.xml")) && File.Exists(Path.Combine(conflictFolder, "poznamky.txt")), "competing indices are preserved before recovery or migration");

        NotesState interruptedState; NotesStore interrupted = MigrationFixture(Path.Combine(root, "interrupted"), out interruptedState); MakeLegacyLayout(interrupted, interruptedState);
        string interruptedFolder = interrupted.ProjectDirectory(interruptedState.Projects[0]); refused = false;
        using (FileStream locked = new FileStream(Path.Combine(interruptedFolder, "poznamky.txt.bak"), FileMode.Open, FileAccess.Read, FileShare.Read))
            try { new NotesStore(interrupted.DirectoryPath).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(Path.Combine(interruptedFolder, "notes.txt")) && File.Exists(Path.Combine(interrupted.DirectoryPath, "poznamky.xml")), "an interrupted migration retains its readable index and completed file moves");
        Check(new NotesStore(interrupted.DirectoryPath).Load().Projects[0].Text == interruptedState.Projects[0].Text && File.Exists(Path.Combine(interruptedFolder, "notes.txt.bak")), "retry completes a partially renamed layout without losing its backup");

        NotesState backupState; NotesStore backupOnly = MigrationFixture(Path.Combine(root, "backup-only"), out backupState); MakeLegacyLayout(backupOnly, backupState);
        File.Delete(Path.Combine(backupOnly.DirectoryPath, "poznamky.xml")); string backupFolder = backupOnly.ProjectDirectory(backupState.Projects[0]); File.Delete(Path.Combine(backupFolder, "poznamky.txt"));
        NotesStore backupReader = new NotesStore(backupOnly.DirectoryPath); NotesState fromBackups = backupReader.Load(); backupReader.Save(fromBackups);
        Check(fromBackups.Projects[0].Text == "Previous note" && File.Exists(Path.Combine(backupOnly.DirectoryPath, "notes.xml")) && File.Exists(Path.Combine(backupFolder, "notes.txt")), "backup-only legacy storage recovers into English filenames");

        foreach (string kind in new[] { "attachment-rename", "project-delete" })
        {
            NotesState journalState; NotesStore journalStore = MigrationFixture(Path.Combine(root, kind), out journalState); Project original = journalState.Projects[0];
            string operation = StartIntent(journalStore, new StoreOperation { Kind = kind, Folder = original.Folder, Media = "snimky", Source = "image-001.png", Target = "renamed.png" });
            MakeLegacyLayout(journalStore, journalState);
            if (kind == "project-delete") Directory.Move(journalStore.ProjectDirectory(original), Path.Combine(operation, "projekt"));
            else File.Move(Path.Combine(journalStore.ProjectDirectory(original), "snimky", "image-001.png"), Path.Combine(operation, "priloha"));
            NotesStore journalReader = new NotesStore(journalStore.DirectoryPath); NotesState recovered = journalReader.Load();
            Check(recovered.Projects[0].Text == original.Text && journalReader.ImageFiles(recovered.Projects[0]).Length == 1 && !Directory.Exists(operation)
                && File.Exists(Path.Combine(journalStore.DirectoryPath, "notes.xml")), "legacy " + kind + " journal rolls back before path migration");
        }
        NotesState conflictingJournalState; NotesStore conflictingJournal = MigrationFixture(Path.Combine(root, "journal-conflict"), out conflictingJournalState);
        Project conflictingProject = conflictingJournalState.Projects[0];
        string retainedJournal = StartIntent(conflictingJournal, new StoreOperation { Kind = "attachment-delete", Folder = conflictingProject.Folder, Media = "snimky", Source = "image-001.png" });
        MakeLegacyLayout(conflictingJournal, conflictingJournalState); string retainedFolder = conflictingJournal.ProjectDirectory(conflictingProject);
        File.Move(Path.Combine(retainedFolder, "snimky", "image-001.png"), Path.Combine(retainedJournal, "priloha"));
        File.WriteAllText(Path.Combine(retainedFolder, "notes.txt"), "Independent note after interruption"); refused = false;
        try { new NotesStore(conflictingJournal.DirectoryPath).Load(); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(Path.Combine(retainedJournal, "priloha")) && File.ReadAllText(Path.Combine(retainedFolder, "notes.txt")) == "Independent note after interruption"
            && File.ReadAllText(Path.Combine(retainedFolder, "poznamky.txt")) == conflictingProject.Text, "legacy journal recovery preserves competing notes and the staged attachment");

        string package = Path.Combine(root, "english.zip"); ProjectArchive.Export(migrated, restored, package);
        using (ZipArchive zip = ZipFile.OpenRead(package))
        using (StreamReader reader = new StreamReader(zip.GetEntry("project.xml").Open()))
            Check(reader.ReadToEnd().Contains("<Version>3</Version>") && zip.GetEntry("notes.txt") != null && zip.GetEntry("images/image-001.png") != null && zip.GetEntry("videos/video-001.avi") != null, "new export archives use English paths and package version 3");
        foreach (int version in new[] { 1, 2 })
        {
            string legacyZip = Path.Combine(root, "legacy-" + version + ".zip");
            using (ZipArchive input = ZipFile.OpenRead(package))
            using (ZipArchive output = ZipFile.Open(legacyZip, ZipArchiveMode.Create))
                foreach (ZipArchiveEntry entry in input.Entries)
                {
                    if (entry.FullName == "project.xml")
                    {
                        using (StreamWriter writer = new StreamWriter(output.CreateEntry("projekt.xml").Open())) writer.Write("<ProjectPackage><Version>" + version + "</Version><Name>Legacy notes</Name></ProjectPackage>");
                        continue;
                    }
                    if (version == 1 && entry.FullName.StartsWith("videos/")) continue;
                    string name = entry.FullName.Replace("notes.txt", "poznamky.txt").Replace("images/", "snimky/").Replace("videos/", "videa/");
                    using (Stream source = entry.Open()) using (Stream target = output.CreateEntry(name).Open()) source.CopyTo(target);
                }
            NotesStore receiver = new NotesStore(Path.Combine(root, "import-" + version)); NotesState received = receiver.Load();
            Project imported = ProjectArchive.Import(receiver, received, legacyZip);
            Check(imported.Text == restored.Text && File.ReadAllBytes(receiver.ImageFiles(imported)[0]).SequenceEqual(image) && receiver.VideoFiles(imported).Length == version - 1
                && File.Exists(Path.Combine(receiver.ProjectDirectory(imported), "notes.txt")), "legacy ZIP version " + version + " imports into English storage with intact attachments");
            using (ZipArchive malicious = ZipFile.Open(legacyZip, ZipArchiveMode.Update)) using (Stream target = malicious.CreateEntry("snimky/../../escape.png").Open()) target.WriteByte(1);
            Check(RefusesImport(receiver, received, legacyZip) && receiver.Load().Projects.Count == 2, "legacy ZIP version " + version + " retains traversal protection");
        }
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
            if (phase >= 2) { state.Projects.Remove(project); state.SelectedId = state.Projects[0].Id; store.Save(state); File.Copy(Path.Combine(directory, "notes.xml"), Path.Combine(directory, "notes.xml.bak"), true); }
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
                string operation = StartIntent(store, new StoreOperation { Kind = kind, Folder = project.Folder, Media = "images", Source = oldLeaf, Target = newLeaf });
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
        string journal = StartIntent(lockedStore, new StoreOperation { Kind = "attachment-delete", Folder = lockedState.Projects[0].Folder, Media = "images", Source = Path.GetFileName(picture) });
        string staged = Path.Combine(journal, "priloha"); File.Move(picture, staged); NotesStore.WriteAtomic(Path.Combine(journal, "dokonceno"), new byte[0]);
        bool refused = false;
        NotesStore waitingStore = new NotesStore(lockedRoot);
        NotesState waiting = null;
        using (FileStream locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            waiting = waitingStore.Load();
            Check(waitingStore.CleanupPending && File.Exists(Path.Combine(journal, "dokonceno")), "deferred committed cleanup retains its marker and allows startup");
            waiting.Projects[0].Text = "New work during deferred cleanup"; waitingStore.Save(waiting);
            Check(waitingStore.CleanupPending && File.ReadAllText(Path.Combine(waitingStore.ProjectDirectory(waiting.Projects[0]), "notes.txt")) == waiting.Projects[0].Text,
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
        Check(!waitingStore.CleanupPending && File.ReadAllText(Path.Combine(waitingStore.ProjectDirectory(waiting.Projects[0]), "notes.txt.bak")) == "",
            "completing deferred cleanup does not rewrite unchanged notes or replace their meaningful backup");
        NotesState cleaned = new NotesStore(lockedRoot).Load();
        Check(cleaned.Projects.Count == 1 && cleaned.Projects[0].Text == "New work during deferred cleanup" && !Directory.Exists(journal) && !File.Exists(picture), "retry completes cleanup without rolling back a committed deletion or later work");
        string damagedRoot = Path.Combine(root, "damaged-snapshot");
        NotesStore damagedStore = new NotesStore(damagedRoot); NotesState damagedState = damagedStore.Load(); damagedStore.Save(damagedState);
        string source; using (Bitmap bitmap = new Bitmap(20, 20)) source = damagedStore.SaveImage(damagedState.Projects[0], bitmap);
        byte[] imageBytes = File.ReadAllBytes(source);
        string damagedJournal = StartIntent(damagedStore, new StoreOperation { Kind = "attachment-rename", Folder = damagedState.Projects[0].Folder, Media = "images", Source = Path.GetFileName(source), Target = "target.png" });
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
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("project.xml").Open())) writer.Write("<ProjectPackage><Version>3</Version><Name>" + name + "</Name></ProjectPackage>");
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("notes.txt").Open())) writer.Write("long name note");
        }
        string deep = Path.Combine(root, "deep"); while (deep.Length < 175) deep = Path.Combine(deep, "nested");
        NotesStore store = new NotesStore(deep); NotesState state = store.Load(); store.Save(state);
        Project first = ProjectArchive.Import(store, state, archive), second = ProjectArchive.Import(store, state, archive);
        Check(first.Name == name && second.Name.StartsWith(name) && first.Folder != second.Folder && first.Folder.Length <= 64 && store.Load().Projects.Count == 3,
            "a 300-character display name imports twice with short unique folders in a deep root");
        string image; using (Bitmap bitmap = new Bitmap(20, 20)) image = store.SaveImage(first, bitmap);
        string renamed = store.RenameImage(state, first, image, "short");
        Check(File.Exists(renamed) && Path.Combine(store.ProjectDirectory(first), "notes.txt.bak").Length <= 259, "deep project paths reserve space for images, journals and note backups");
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
        Directory.CreateDirectory(Path.Combine(target.DirectoryPath, "notes.xml")); refused = false;
        try { target.CopyFrom(source, state); } catch (IOException) { refused = true; }
        Check(refused && !target.HasData && Directory.GetDirectories(target.DirectoryPath).Length == 1 && File.ReadAllText(sentinel) == "unrelated",
            "a late index failure rolls back every owned project folder and retains a foreign directory");
        Directory.Delete(Path.Combine(target.DirectoryPath, "notes.xml")); NotesState copied = target.CopyFrom(source, state);
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
        string other = Path.Combine(store.ProjectDirectory(state.Projects[1]), "notes.txt"); state.Projects[0].Text = "changed";
        using (FileStream locked = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None)) store.Save(state);
        Check(new NotesStore(root).Load().Projects[0].Text == "changed", "a locked unchanged note cannot block saving the edited project");
        using (FileStream lockedNote = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None))
        using (FileStream lockedIndex = new FileStream(Path.Combine(root, "notes.xml"), FileMode.Open, FileAccess.Read, FileShare.None)) store.Save(state);
        Check(!File.Exists(other + ".bak"), "unchanged Save performs no note/index reads or writes and keeps existing backups");
        string first = Path.Combine(store.ProjectDirectory(state.Projects[0]), "notes.txt"); state.Projects[0].Text = "retry"; bool refused = false;
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
