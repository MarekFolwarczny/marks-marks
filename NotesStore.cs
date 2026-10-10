using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace ProjektovePoznamky
{
    /// <summary>Project record in the XML index; note text lives in memory and a separate UTF-8 file.</summary>
    public class Project
    {
        /// <summary>Stable identifier; new projects receive a unique GUID.</summary>
        public string Id = Guid.NewGuid().ToString("N");
        /// <summary>User-facing name, never treated as a UI translation key.</summary>
        public string Name = "";
        /// <summary>Single folder component below the root, unchanged when the project is renamed.</summary>
        public string Folder = "";
        /// <summary>Current note contents, excluded from new XML indices by ShouldSerializeText.</summary>
        public string Text = "";
        // Read old XML notes, but write new notes exclusively to the project's text file.
        /// <summary>Read legacy XML Text contents without writing them into new project indices.</summary>
        public bool ShouldSerializeText() { return false; }
        /// <summary>Return the user-facing name for the project list.</summary>
        public override string ToString() { return Name; }
    }

    /// <summary>Serialized project list, selection, and floating button state.</summary>
    public class NotesState
    {
        /// <summary>Projects owned by this model; the UI derives display order separately.</summary>
        public List<Project> Projects = new List<Project>();
        /// <summary>Last selected project Id, or null for an empty storage.</summary>
        public string SelectedId;
        /// <summary>Saved desktop X coordinate; -1 with OverlayY=-1 means first placement.</summary>
        public int OverlayX = -1;
        /// <summary>Saved Y coordinate, including negative coordinates on other monitors.</summary>
        public int OverlayY = -1;
        /// <summary>Remembered visibility of the floating button.</summary>
        public bool OverlayVisible = true;
    }

    /// <summary>Durable intent for one file operation; paths are validated components below the root.</summary>
    public sealed class StoreOperation
    {
        /// <summary>project-delete, attachment-delete, or attachment-rename.</summary>
        public string Kind;
        /// <summary>Stable folder of the owning project.</summary>
        public string Folder;
        /// <summary>images or videos; empty for project deletion.</summary>
        public string Media;
        /// <summary>Original attachment filename without its parent path.</summary>
        public string Source;
        /// <summary>Target filename for renaming.</summary>
        public string Target;
    }

    /// <summary>User-specific storage root preference in LOCALAPPDATA.</summary>
    public sealed class StorageLocation
    {
        private readonly string path;
        /// <summary>Locate uloziste.txt outside the transferable projects.</summary>
        public StorageLocation(string settingsDirectory) { path = Path.Combine(settingsDirectory, "uloziste.txt"); }
        /// <summary>Read a valid absolute preference, then its backup; preserve damaged files.</summary>
        /// <returns>Normalized path, or null to request a new storage selection.</returns>
        public string Read()
        {
            foreach (string file in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    string value = File.ReadAllText(file).Trim();
                    if (!Path.IsPathRooted(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.Length > 259) continue;
                    string full = Path.GetFullPath(value);
                    if (!string.Equals(Path.GetPathRoot(value), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) continue;
                    return full;
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)) throw;
                }
            }
            return null;
        }
        /// <summary>Save the normalized absolute path and retain its previous value in .bak.</summary>
        public void Save(string directory)
        {
            NotesStore.WriteAtomic(path, Encoding.UTF8.GetBytes(Path.GetFullPath(directory)));
        }
    }

    /// <summary>Project and attachment storage with validation, backups, and per-file Win32 commits.</summary>
    public sealed class NotesStore
    {
        /// <summary>Rename a file through Win32 and retain the native error code for IOException.</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, int flags);
        private readonly string path;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(NotesState));
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly Dictionary<string, string> savedTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private byte[] savedIndex;
        private bool operationActive, recoveryChecked;

        /// <summary>Indicate recovery of an operation, index, or missing note during the last Load.</summary>
        public bool Recovered { get; private set; }
        /// <summary>A committed operation is awaiting cleanup of temporarily locked owned files.</summary>
        public bool CleanupPending { get; private set; }
        /// <summary>Absolute storage root derived from the index path.</summary>
        public string DirectoryPath { get { return Path.GetDirectoryName(path); } }
        /// <summary>A primary index or backup exists; leftover folders alone do not qualify.</summary>
        public bool HasData { get { string index = IndexToRead; return File.Exists(index) || File.Exists(index + ".bak"); } }

        /// <summary>Prefer the English index, retaining access to older storage until migration completes.</summary>
        private string IndexToRead
        {
            get
            {
                string legacy = Path.Combine(DirectoryPath, "poznamky.xml");
                return !File.Exists(path) && !File.Exists(path + ".bak") && (File.Exists(legacy) || File.Exists(legacy + ".bak")) ? legacy : path;
            }
        }

        /// <summary>Normalize the root and locate notes.xml without creating files.</summary>
        public NotesStore(string directory) { path = Path.Combine(Path.GetFullPath(directory), "notes.xml"); }

        /// <summary>Load and validate the index and project texts, or return a new unsaved default model.</summary>
        /// <param name="migrateLegacy">Rename older storage in place; disable only when copying an original legacy source.</param>
        /// <returns>Model containing loaded text; a new model must be saved first.</returns>
        public NotesState Load(bool migrateLegacy = true)
        {
            savedTexts.Clear(); savedIndex = null;
            foreach (string suffix in new[] { "", ".bak" }) RefuseLegacyCollision(Path.Combine(DirectoryPath, "poznamky.xml" + suffix), path + suffix);
            Recovered = RecoverOperations(); recoveryChecked = !CleanupPending;
            if (!HasData)
            {
                NotesState fresh = new NotesState();
                Project project = new Project { Name = Ui.T("General") };
                fresh.Projects.Add(project);
                fresh.SelectedId = project.Id;
                return fresh;
            }
            NotesState state;
            string index = IndexToRead;
            try { state = ReadIndex(index); savedIndex = File.ReadAllBytes(index); }
            catch (Exception first)
            {
                if (!(first is IOException || first is InvalidOperationException || first is XmlException)) throw;
                try
                {
                    state = ReadIndex(index + ".bak");
                    PreserveDamaged(index);
                    Recovered = true;
                }
                catch (Exception second)
                {
                    throw new IOException(Ui.T("Cannot load the project list. The original files were preserved in:\n") + DirectoryPath, second);
                }
            }
            if (migrateLegacy) Recovered |= MigrateLegacyFiles(state);
            foreach (Project project in state.Projects)
                if (!string.IsNullOrEmpty(project.Folder))
                {
                    string file = NoteToRead(ProjectDirectory(project));
                    project.Text = ReadNote(file);
                    savedTexts[file] = project.Text;
                }
            return state;
        }

        /// <summary>Locate a current or legacy note during recovery or copying, including backup-only files.</summary>
        private static string NoteToRead(string folder)
        {
            string current = Path.Combine(folder, "notes.txt"), legacy = Path.Combine(folder, "poznamky.txt");
            return !File.Exists(current) && !File.Exists(current + ".bak") && (File.Exists(legacy) || File.Exists(legacy + ".bak")) ? legacy : current;
        }

        /// <summary>Refuse competing legacy/current paths before migration or journal recovery can alter either copy.</summary>
        private static void RefuseLegacyCollision(string legacy, string current)
        {
            if ((File.Exists(legacy) || Directory.Exists(legacy)) && (File.Exists(current) || Directory.Exists(current)))
                throw new IOException(Ui.T("Cannot migrate storage because both names exist:\n") + legacy + "\n" + current);
        }

        /// <summary>Rename legacy notes, backups, and attachment folders without replacing any existing destination.</summary>
        /// <remarks>Recover journals first. Preflight all conflicts and path lengths; interrupted moves resume on the next Load.</remarks>
        private bool MigrateLegacyFiles(NotesState state)
        {
            List<KeyValuePair<string, string>> moves = new List<KeyValuePair<string, string>>();
            foreach (Project project in state.Projects.Where(p => !string.IsNullOrEmpty(p.Folder)))
            {
                string folder = ProjectDirectory(project);
                moves.Add(new KeyValuePair<string, string>(Path.Combine(folder, "poznamky.txt"), Path.Combine(folder, "notes.txt")));
                moves.Add(new KeyValuePair<string, string>(Path.Combine(folder, "poznamky.txt.bak"), Path.Combine(folder, "notes.txt.bak")));
                moves.Add(new KeyValuePair<string, string>(Path.Combine(folder, "snimky"), Path.Combine(folder, "images")));
                moves.Add(new KeyValuePair<string, string>(Path.Combine(folder, "videa"), Path.Combine(folder, "videos")));
            }
            moves.Add(new KeyValuePair<string, string>(Path.Combine(DirectoryPath, "poznamky.xml"), path));
            moves.Add(new KeyValuePair<string, string>(Path.Combine(DirectoryPath, "poznamky.xml.bak"), path + ".bak"));
            moves.RemoveAll(pair => !File.Exists(pair.Key) && !Directory.Exists(pair.Key));
            foreach (KeyValuePair<string, string> pair in moves)
            {
                RefuseLegacyCollision(pair.Key, pair.Value);
                CheckFilePath(pair.Value);
                if (Directory.Exists(pair.Key))
                    foreach (string child in Directory.GetFileSystemEntries(pair.Key, "*", SearchOption.AllDirectories))
                        CheckFilePath(pair.Value + child.Substring(pair.Key.Length));
            }
            foreach (KeyValuePair<string, string> pair in moves)
                if (Directory.Exists(pair.Key)) Directory.Move(pair.Key, pair.Value); else File.Move(pair.Key, pair.Value);
            return moves.Count != 0;
        }

        /// <summary>Deserialize XML without DTDs/external resources, validate the model, and repair an invalid selected Id.</summary>
        private NotesState ReadIndex(string file)
        {
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(file, settings))
            {
                NotesState state = (NotesState)serializer.Deserialize(reader);
                Validate(state);
                if (!state.Projects.Any(p => p.Id == state.SelectedId)) state.SelectedId = state.Projects.Count == 0 ? null : state.Projects[0].Id;
                return state;
            }
        }

        /// <summary>Read UTF-8 text and try its backup after an ordinary I/O failure.</summary>
        /// <remarks>Invalid UTF-8 raises InvalidDataException without overwriting the original or backup.</remarks>
        private string ReadNote(string file)
        {
            try { return ReadUtf8(file); }
            catch (Exception first)
            {
                if (!(first is IOException)) throw;
                try
                {
                    string text = ReadUtf8(file + ".bak");
                    PreserveDamaged(file);
                    WriteAtomic(file, Utf8.GetBytes(text));
                    Recovered = true;
                    return text;
                }
                catch (Exception second) { throw new IOException(Ui.T("Cannot load the note; its files were preserved:\n") + file, second); }
            }
        }

        /// <summary>Read UTF-8 with an optional BOM and reject damaged encoding using a round-trip check.</summary>
        private static string ReadUtf8(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            string text = Utf8.GetString(bytes, start, bytes.Length - start);
            if (!Utf8.GetBytes(text).SequenceEqual(bytes.Skip(start))) throw new InvalidDataException(Ui.T("The note file is not valid UTF-8 text."));
            return text;
        }

        /// <summary>Preserve an existing problematic file under a unique name for manual recovery.</summary>
        private static void PreserveDamaged(string file)
        {
            if (File.Exists(file)) File.Copy(file, file + ".poskozene-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>Reject missing data, duplicate Ids, and shared or unsafe folders before writing.</summary>
        private static void Validate(NotesState state)
        {
            if (state == null || state.Projects == null ||
                state.Projects.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Text == null) ||
                state.Projects.Select(p => p.Id).Distinct().Count() != state.Projects.Count)
                throw new InvalidOperationException(Ui.T("The notes file contains invalid data."));
            string[] folders = state.Projects.Where(p => !string.IsNullOrEmpty(p.Folder)).Select(p => p.Folder).ToArray();
            foreach (string folder in folders) ValidateFolder(folder);
            if (folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Length)
                throw new InvalidOperationException(Ui.T("Two projects cannot share the same folder."));
        }

        /// <summary>Validate one path component without separators or dot-based traversal.</summary>
        private static void ValidateFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder == "." || folder == ".." ||
                folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.EndsWith(".") || folder.EndsWith(" ") ||
                !string.Equals(Path.GetFileName(folder), folder, StringComparison.Ordinal))
                throw new InvalidOperationException(Ui.T("Invalid project folder."));
        }

        /// <summary>Allocate short unique folders with space reserved for attachments, backups, and recovery records.</summary>
        /// <param name="state">Model whose existing folder names remain stable.</param>
        /// <param name="longestAttachment">Length of the longest attachment filename during import or copying.</param>
        internal void AllocateFolders(NotesState state, int longestAttachment = 0)
        {
            Validate(state);
            int limit = Math.Min(64, 259 - DirectoryPath.Length - 1 - Math.Max(64, 8 + longestAttachment + 4));
            if (limit < 1 && state.Projects.Any(p => string.IsNullOrEmpty(p.Folder))) throw new IOException(Ui.T("The path is too long. Choose a shorter storage path or attachment name."));
            HashSet<string> used = new HashSet<string>(state.Projects.Where(p => !string.IsNullOrEmpty(p.Folder)).Select(p => p.Folder), StringComparer.OrdinalIgnoreCase);
            foreach (Project project in state.Projects.Where(p => string.IsNullOrEmpty(p.Folder)))
            {
                string name = new string(project.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
                string folder = FitFolder(name, limit);
                int number = 2;
                while (used.Contains(folder) || Directory.Exists(Path.Combine(DirectoryPath, folder)) || File.Exists(Path.Combine(DirectoryPath, folder)))
                {
                    string suffix = " (" + number++ + ")";
                    if (suffix.Length >= limit) throw new IOException(Ui.T("The path is too long. Choose a shorter storage path or attachment name."));
                    folder = FitFolder(name, limit - suffix.Length) + suffix;
                }
                project.Folder = folder;
                used.Add(folder);
            }
        }

        /// <summary>Shorten a filename without splitting surrogate pairs or using Windows device names.</summary>
        private static string FitFolder(string name, int limit)
        {
            if (name.Length > limit) name = name.Substring(0, limit);
            if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1])) name = name.Substring(0, name.Length - 1);
            name = name.TrimEnd(' ', '.');
            if (name.Length == 0) name = "P";
            if (IsReservedName(name)) name = "_" + name.Substring(0, Math.Min(name.Length, limit - 1));
            return name;
        }

        /// <summary>Reject excessively long paths before creating or moving files.</summary>
        internal static void CheckFilePath(string file)
        {
            if (Path.GetFullPath(file).Length > 259) throw new IOException(Ui.T("The path is too long. Choose a shorter storage path or attachment name."));
        }

        /// <summary>Return the validated project folder below the storage root.</summary>
        public string ProjectDirectory(Project project)
        {
            ValidateFolder(project.Folder);
            return Path.Combine(DirectoryPath, project.Folder);
        }

        /// <summary>Save changed text files followed by the index; commit each file with its previous version.</summary>
        /// <remarks>Compare only with successfully loaded/saved contents; unchanged files are not read. Atomicity is per file. External edits require another Load.</remarks>
        public void Save(NotesState state)
        {
            if (!operationActive && !recoveryChecked)
            {
                if (RecoverOperations()) Recovered = true;
                recoveryChecked = !CleanupPending;
            }
            AllocateFolders(state);
            HashSet<string> activeFiles = new HashSet<string>(state.Projects.Select(p => Path.Combine(ProjectDirectory(p), "notes.txt")), StringComparer.OrdinalIgnoreCase);
            foreach (string oldFile in savedTexts.Keys.Where(file => !activeFiles.Contains(file)).ToArray()) savedTexts.Remove(oldFile);
            foreach (Project project in state.Projects)
            {
                string file = Path.Combine(ProjectDirectory(project), "notes.txt");
                string saved;
                if (!savedTexts.TryGetValue(file, out saved) || saved != project.Text)
                {
                    byte[] contents = Utf8.GetBytes(project.Text);
                    if (Utf8.GetString(contents) != project.Text) throw new InvalidDataException(Ui.T("The note contains an invalid Unicode character."));
                    WriteAtomic(file, contents);
                    savedTexts[file] = project.Text;
                }
            }
            using (MemoryStream buffer = new MemoryStream())
            {
                using (XmlWriter writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize }))
                    serializer.Serialize(writer, state);
                byte[] contents = buffer.ToArray();
                if (savedIndex == null || !savedIndex.SequenceEqual(contents))
                {
                    WriteAtomic(path, contents);
                    savedIndex = contents;
                }
            }
        }

        /// <summary>Prepare a copy in unique staging space; publish the index only after transferring all data.</summary>
        /// <remarks>On failure, remove only new owned folders; preserve the source and unrelated destination files.</remarks>
        public NotesState CopyFrom(NotesStore source, NotesState original)
        {
            if (HasData) throw new IOException(Ui.T("The destination storage already contains projects."));
            NotesState copy = new NotesState { SelectedId = original.SelectedId, OverlayX = original.OverlayX, OverlayY = original.OverlayY, OverlayVisible = original.OverlayVisible };
            foreach (Project project in original.Projects) copy.Projects.Add(new Project { Id = project.Id, Name = project.Name, Text = project.Text });
            string[][] media = original.Projects.Select(p => source.ImageFiles(p).Concat(source.VideoFiles(p)).ToArray()).ToArray();
            AllocateFolders(copy, media.SelectMany(files => files).Select(file => Path.GetFileName(file).Length).DefaultIfEmpty(0).Max());
            string staging = Path.Combine(DirectoryPath, ".copy-" + Guid.NewGuid().ToString("N"));
            List<string> owned = new List<string>();
            List<KeyValuePair<string, string>> transfers = new List<KeyValuePair<string, string>>();
            bool committed = false;
            try
            {
                Directory.CreateDirectory(staging);
                // Flat staging names fit within the path budget even for long original attachment names.
                for (int i = 0; i < copy.Projects.Count; i++)
                    foreach (string file in media[i])
                    {
                        string temporary = Path.Combine(staging, "media-" + transfers.Count);
                        File.Copy(file, temporary, false);
                        string mediaFolder = Path.GetFileName(Path.GetDirectoryName(file));
                        string target = Path.Combine(ProjectDirectory(copy.Projects[i]), mediaFolder == "images" || mediaFolder == "snimky" ? "images" : "videos", Path.GetFileName(file));
                        transfers.Add(new KeyValuePair<string, string>(temporary, target));
                    }
                new NotesStore(staging).Save(copy);
                foreach (Project project in copy.Projects)
                {
                    string target = ProjectDirectory(project);
                    Directory.Move(Path.Combine(staging, project.Folder), target);
                    owned.Add(target);
                    savedTexts[Path.Combine(target, "notes.txt")] = project.Text;
                }
                foreach (KeyValuePair<string, string> transfer in transfers)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(transfer.Value));
                    File.Move(transfer.Key, transfer.Value);
                }
                Save(copy);
                committed = true;
                return copy;
            }
            finally
            {
                if (!committed)
                    foreach (string folder in owned)
                    {
                        savedTexts.Remove(Path.Combine(folder, "notes.txt"));
                        if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    }
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        /// <summary>Return alphabetically sorted PNG files in images, or an empty array when absent.</summary>
        public string[] ImageFiles(Project project)
        {
            if (string.IsNullOrEmpty(project.Folder)) return new string[0];
            string directory = Path.Combine(ProjectDirectory(project), "images");
            if (!Directory.Exists(directory)) directory = Path.Combine(ProjectDirectory(project), "snimky");
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
        }

        /// <summary>Return sorted supported files in videos; codec availability is not checked here.</summary>
        public string[] VideoFiles(Project project)
        {
            if (string.IsNullOrEmpty(project.Folder)) return new string[0];
            string directory = Path.Combine(ProjectDirectory(project), "videos");
            if (!Directory.Exists(directory)) directory = Path.Combine(ProjectDirectory(project), "videa");
            return Directory.Exists(directory) ? Directory.GetFiles(directory).Where(VideoFormats.Supports).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
        }

        /// <summary>Journal the intent, move the project, and save the index; Load rolls back incomplete operations.</summary>
        /// <remarks>Synchronize the index backup before the commit marker. Only retryable cleanup remains after commitment.</remarks>
        public void DeleteProject(NotesState state, Project project)
        {
            int index = state.Projects.IndexOf(project);
            if (index < 0) throw new InvalidOperationException(Ui.T("The project is not in this storage."));
            Save(state);
            string folder = ProjectDirectory(project), selectedId = state.SelectedId;
            string operation = BeginOperation(new StoreOperation { Kind = "project-delete", Folder = project.Folder });
            try
            {
                Directory.Move(folder, Path.Combine(operation, "projekt"));
                state.Projects.RemoveAt(index);
                if (state.SelectedId == project.Id) state.SelectedId = state.Projects.Count == 0 ? null : state.Projects[0].Id;
                Save(state);
                File.Copy(path, path + ".bak", true);
                CompleteOperation(operation);
                savedTexts.Remove(Path.Combine(folder, "notes.txt"));
            }
            catch
            {
                if (!File.Exists(Path.Combine(operation, "dokonceno")))
                {
                    if (!state.Projects.Contains(project)) state.Projects.Insert(index, project);
                    state.SelectedId = selectedId;
                    RollbackOperation(operation, new StoreOperation { Kind = "project-delete", Folder = project.Folder });
                }
                throw;
            }
            finally { operationActive = false; recoveryChecked = !Directory.Exists(operation) && !CleanupPending; }
        }

        /// <summary>Validate PNG ownership and remove the file and its exact Czech/English note references.</summary>
        public void DeleteImage(NotesState state, Project project, string file)
        {
            if (!state.Projects.Contains(project)) throw new InvalidOperationException(Ui.T("The project is not in this storage."));
            file = Path.GetFullPath(file);
            if (!ImageFiles(project).Contains(file, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Ui.T("The image is not in this project."));
            DeleteAttachment(state, project, file, new[] { "Snímek", "Image" });
        }

        /// <summary>Validate video ownership and remove the file and its exact Video references.</summary>
        public void DeleteVideo(NotesState state, Project project, string file)
        {
            if (!state.Projects.Contains(project)) throw new InvalidOperationException(Ui.T("The project is not in this storage."));
            file = Path.GetFullPath(file);
            if (!VideoFiles(project).Contains(file, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Ui.T("The video does not belong to this project."));
            DeleteAttachment(state, project, file, new[] { "Video" });
        }

        /// <summary>Rename an owned PNG and update matching Czech/English image references.</summary>
        /// <param name="state">Owning storage model.</param>
        /// <param name="project">Project owning the attachment.</param>
        /// <param name="file">Existing attachment path in that project.</param>
        /// <param name="name">New name without its extension; preserve the file type.</param>
        /// <returns>Full renamed path with its note references saved.</returns>
        public string RenameImage(NotesState state, Project project, string file, string name)
        {
            if (!state.Projects.Contains(project)) throw new InvalidOperationException(Ui.T("The project is not in this storage."));
            file = Path.GetFullPath(file);
            if (!ImageFiles(project).Contains(file, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Ui.T("The image is not in this project."));
            return RenameAttachment(state, project, file, name, new[] { "Snímek", "Image" });
        }

        /// <summary>Rename an owned video without changing its extension and update matching Video references.</summary>
        /// <param name="state">Owning storage model.</param>
        /// <param name="project">Project owning the attachment.</param>
        /// <param name="file">Existing attachment path in that project.</param>
        /// <param name="name">New name without its extension; preserve the file type.</param>
        /// <returns>Full renamed path with its note references saved.</returns>
        public string RenameVideo(NotesState state, Project project, string file, string name)
        {
            if (!state.Projects.Contains(project)) throw new InvalidOperationException(Ui.T("The project is not in this storage."));
            file = Path.GetFullPath(file);
            if (!VideoFiles(project).Contains(file, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Ui.T("The video does not belong to this project."));
            return RenameAttachment(state, project, file, name, new[] { "Video" });
        }

        /// <summary>Recognize reserved Windows device names regardless of extension or case.</summary>
        private static bool IsReservedName(string name)
        {
            string device = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device);
        }

        /// <summary>Rename the attachment and exact references with durable recovery intent.</summary>
        private string RenameAttachment(NotesState state, Project project, string file, string name, string[] markerNames)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException(Ui.T("The attachment name must not be empty."));
            string leaf = name.Trim() + Path.GetExtension(file);
            ValidateFolder(leaf);
            if (IsReservedName(leaf)) throw new InvalidOperationException(Ui.T("This name is reserved by Windows."));
            string target = Path.Combine(Path.GetDirectoryName(file), leaf);
            CheckFilePath(target);
            if (string.Equals(file, target, StringComparison.Ordinal)) return file;
            if (File.Exists(target) && !string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) throw new IOException(Ui.T("An attachment with this name already exists in the project."));
            Save(state);
            string original = project.Text;
            StoreOperation intent = new StoreOperation { Kind = "attachment-rename", Folder = project.Folder, Media = Path.GetFileName(Path.GetDirectoryName(file)), Source = Path.GetFileName(file), Target = leaf };
            string operation = BeginOperation(intent), pending = Path.Combine(operation, "priloha");
            try
            {
                File.Move(file, pending);
                File.Move(pending, target);
                foreach (string marker in markerNames) project.Text = project.Text.Replace("[" + marker + ": " + intent.Source + "]", "[" + marker + ": " + leaf + "]");
                Save(state);
                CompleteOperation(operation);
                return target;
            }
            catch
            {
                if (!File.Exists(Path.Combine(operation, "dokonceno"))) { project.Text = original; RollbackOperation(operation, intent); }
                throw;
            }
            finally { operationActive = false; recoveryChecked = !Directory.Exists(operation) && !CleanupPending; }
        }

        /// <summary>Remove exact references and delete the attachment after commitment; earlier states remain recoverable.</summary>
        private void DeleteAttachment(NotesState state, Project project, string file, string[] markerNames)
        {
            Save(state);
            string original = project.Text;
            StoreOperation intent = new StoreOperation { Kind = "attachment-delete", Folder = project.Folder, Media = Path.GetFileName(Path.GetDirectoryName(file)), Source = Path.GetFileName(file) };
            string operation = BeginOperation(intent);
            try
            {
                File.Move(file, Path.Combine(operation, "priloha"));
                foreach (string marker in markerNames) project.Text = project.Text.Replace("[" + marker + ": " + intent.Source + "]", "");
                Save(state);
                CompleteOperation(operation);
            }
            catch
            {
                if (!File.Exists(Path.Combine(operation, "dokonceno"))) { project.Text = original; RollbackOperation(operation, intent); }
                throw;
            }
            finally { operationActive = false; recoveryChecked = !Directory.Exists(operation) && !CleanupPending; }
        }

        /// <summary>Save the original index/text before the XML intent; only then may data be moved.</summary>
        private string BeginOperation(StoreOperation intent)
        {
            string directory = Path.Combine(DirectoryPath, ".operace-" + Guid.NewGuid().ToString("N"));
            try
            {
                string before = intent.Kind == "project-delete" ? path : Path.Combine(DirectoryPath, intent.Folder, "notes.txt");
                WriteAtomic(Path.Combine(directory, "pred"), File.ReadAllBytes(before));
                using (MemoryStream buffer = new MemoryStream())
                {
                    new XmlSerializer(typeof(StoreOperation)).Serialize(buffer, intent);
                    WriteAtomic(Path.Combine(directory, "operace.xml"), buffer.ToArray());
                }
                operationActive = true;
                return directory;
            }
            catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
        }

        /// <summary>Write the commit marker after saving data and remove only owned staging files.</summary>
        private void CompleteOperation(string directory)
        {
            WriteAtomic(Path.Combine(directory, "dokonceno"), new byte[0]);
            TryCleanupOperation(directory);
        }

        /// <summary>Defer only I/O errors during committed cleanup, keeping saved data and UI consistent.</summary>
        private bool TryCleanupOperation(string directory)
        {
            try { CleanupOperation(directory); return true; }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException)) throw;
                CleanupPending = true;
                return false;
            }
        }

        /// <summary>Remove obsolete payload first and retain the commit marker until cleanup can finish safely.</summary>
        private static void CleanupOperation(string directory)
        {
            string project = Path.Combine(directory, "projekt"), attachment = Path.Combine(directory, "priloha");
            if (Directory.Exists(project)) Directory.Delete(project, true);
            if (File.Exists(attachment)) File.Delete(attachment);
            foreach (string file in Directory.GetFiles(directory))
                if (Path.GetFileName(file) != "operace.xml" && Path.GetFileName(file) != "dokonceno") File.Delete(file);
            File.Delete(Path.Combine(directory, "operace.xml"));
            File.Delete(Path.Combine(directory, "dokonceno"));
            Directory.Delete(directory);
        }

        /// <summary>Roll back incomplete operations before loading; clean committed records and preserve unknown leftovers.</summary>
        private bool RecoverOperations()
        {
            CleanupPending = false;
            if (!Directory.Exists(DirectoryPath)) return false;
            bool recovered = false;
            foreach (string directory in Directory.GetDirectories(DirectoryPath, ".operace-*"))
            {
                Guid id;
                if (!Guid.TryParseExact(Path.GetFileName(directory).Substring(9), "N", out id) || !File.Exists(Path.Combine(directory, "operace.xml"))) continue;
                StoreOperation intent;
                using (XmlReader reader = XmlReader.Create(Path.Combine(directory, "operace.xml"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                    intent = (StoreOperation)new XmlSerializer(typeof(StoreOperation)).Deserialize(reader);
                if (intent == null || !new[] { "project-delete", "attachment-delete", "attachment-rename" }.Contains(intent.Kind)) throw new InvalidDataException(Ui.T("Invalid storage recovery record."));
                ValidateFolder(intent.Folder);
                if (intent.Kind != "project-delete")
                {
                    if (!new[] { "images", "videos", "snimky", "videa" }.Contains(intent.Media)) throw new InvalidDataException(Ui.T("Invalid storage recovery record."));
                    ValidateFolder(intent.Source);
                    if (intent.Kind == "attachment-rename") ValidateFolder(intent.Target);
                }
                if (File.Exists(Path.Combine(directory, "dokonceno")))
                {
                    if (!TryCleanupOperation(directory)) continue;
                }
                else RollbackOperation(directory, intent);
                recovered = true;
            }
            return recovered;
        }

        /// <summary>Restore the original file and text/index; preserve the journal and remaining copies on collision.</summary>
        private void RollbackOperation(string directory, StoreOperation intent)
        {
            string snapshot = Path.Combine(directory, "pred");
            if (intent.Kind == "project-delete") ReadIndex(snapshot); else ReadUtf8(snapshot);
            byte[] before = File.ReadAllBytes(snapshot);
            string folder = Path.Combine(DirectoryPath, intent.Folder), original;
            if (intent.Kind == "project-delete")
            {
                string pending = Path.Combine(directory, "projekt");
                if (Directory.Exists(pending)) Directory.Move(pending, folder);
                if (!Directory.Exists(folder)) throw new IOException(Ui.T("Storage recovery data is missing."));
                original = IndexToRead;
            }
            else
            {
                foreach (string suffix in new[] { "", ".bak" }) RefuseLegacyCollision(Path.Combine(folder, "poznamky.txt" + suffix), Path.Combine(folder, "notes.txt" + suffix));
                string source = Path.Combine(folder, intent.Media, intent.Source), pending = Path.Combine(directory, "priloha");
                if (File.Exists(pending)) File.Move(pending, source);
                else if (intent.Kind == "attachment-rename" && (!File.Exists(source) || string.Equals(source, Path.Combine(folder, intent.Media, intent.Target), StringComparison.OrdinalIgnoreCase)))
                {
                    File.Move(Path.Combine(folder, intent.Media, intent.Target), pending);
                    File.Move(pending, source);
                }
                if (!File.Exists(source)) throw new IOException(Ui.T("Storage recovery data is missing."));
                original = Path.Combine(folder, intent.Media == "snimky" || intent.Media == "videa" ? "poznamky.txt" : "notes.txt");
                savedTexts.Remove(original);
            }
            WriteAtomic(original, before);
            File.Copy(original, original + ".bak", true);
            savedIndex = null;
            CleanupOperation(directory);
        }

        /// <summary>Save an image copy as the next numbered PNG; the caller retains ownership of the original Image.</summary>
        /// <returns>Full path of the new PNG.</returns>
        public string SaveImage(Project project, Image image)
        {
            string directory = Path.Combine(ProjectDirectory(project), "images");
            Directory.CreateDirectory(directory);
            int number = 1;
            string file;
            do { file = Path.Combine(directory, "image-" + (number++).ToString("D3") + ".png"); } while (File.Exists(file));
            string temporary = file + ".tmp";
            try
            {
                image.Save(temporary, ImageFormat.Png);
                File.Move(temporary, file);
                return file;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Validate the header and copy video through a temporary file without replacing earlier attachments.</summary>
        /// <returns>Full path of the stored copy; the source video remains unchanged.</returns>
        public string SaveVideo(Project project, string source)
        {
            VideoFormats.Validate(source);
            string directory = Path.Combine(ProjectDirectory(project), "videos");
            Directory.CreateDirectory(directory);
            string extension = Path.GetExtension(source).ToLowerInvariant();
            int number = 1;
            string file;
            do { file = Path.Combine(directory, "video-" + (number++).ToString("D3") + extension); } while (File.Exists(file));
            string temporary = file + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                VideoFormats.Validate(temporary, source);
                File.Move(temporary, file);
                return file;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Write and flush a temporary file, back up the existing destination, then replace it.</summary>
        /// <param name="file">Absolute destination path.</param>
        /// <param name="contents">Complete new file contents.</param>
        /// <remarks>Requires one writer. Use a Win32 move because File.Replace previously caused a native process failure in this environment.</remarks>
        internal static void WriteAtomic(string file, byte[] contents)
        {
            CheckFilePath(file + ".tmp");
            CheckFilePath(file + ".bak");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = file + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                CommitFile(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Replace the destination using MOVEFILE_REPLACE_EXISTING and MOVEFILE_WRITE_THROUGH; report failures as IOException.</summary>
        /// <param name="temporary">Completed temporary file.</param>
        /// <param name="file">Destination that may be replaced.</param>
        /// <remarks>The temporary file must be on the destination volume. This method may replace the destination but does not create .bak itself.</remarks>
        internal static void CommitFile(string temporary, string file)
        {
            if (!MoveFileEx(temporary, file, 0x1 | 0x8))
                throw new IOException(Ui.T("Cannot save the file atomically (Windows ") + Marshal.GetLastWin32Error() + "): " + file);
        }
    }
}
