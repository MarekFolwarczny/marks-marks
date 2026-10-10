using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace ProjektovePoznamky
{
    /// <summary>Minimal transfer ZIP metadata; format 1 contains images and format 2 supports videos.</summary>
    public sealed class ProjectPackage
    {
        /// <summary>Archive format: 1 for text/images, 2 when videos are included.</summary>
        public int Version = 1;
        /// <summary>Transferred project name; import allocates a new local Id and folder.</summary>
        public string Name;
    }

    /// <summary>Validated transfer of one project through ZIP, excluding application preferences.</summary>
    internal static class ProjectArchive
    {
        private const long MaxBytes = 1024L * 1024 * 1024;
        private const int MaxNoteBytes = 32 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly XmlSerializer Metadata = new XmlSerializer(typeof(ProjectPackage));

        /// <summary>Create a project archive and atomically replace the ZIP after completing all writes.</summary>
        /// <remarks>The caller saves edits first. Use format 1 without videos and format 2 with videos; exclude preferences, local backups, and Ids.</remarks>
        internal static void Export(NotesStore store, Project project, string file)
        {
            file = Path.GetFullPath(file);
            if (!string.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new IOException(Ui.T("The archive must have a .zip extension."));
            string folder = store.ProjectDirectory(project) + Path.DirectorySeparatorChar;
            if (file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || string.Equals(file, Path.Combine(store.DirectoryPath, "poznamky.xml"), StringComparison.OrdinalIgnoreCase))
                throw new IOException(Ui.T("Save the archive outside the exported project's folder."));
            string[] images = store.ImageFiles(project);
            string[] videos = store.VideoFiles(project);
            byte[] note = Utf8.GetBytes(project.Text);
            if (note.Length > MaxNoteBytes || images.Length > 10000 || videos.Length > 1000 || images.Concat(videos).Sum(p => new FileInfo(p).Length) + note.Length > MaxBytes)
                throw new IOException(Ui.T("The project exceeds the archive limit: 1 GiB, 10,000 images, 1,000 videos or 32 MiB of text."));
            string temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, true, Utf8))
                    {
                        using (Stream stream = archive.CreateEntry("projekt.xml").Open())
                        using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8 }))
                            Metadata.Serialize(writer, new ProjectPackage { Name = project.Name, Version = videos.Length == 0 ? 1 : 2 });
                        using (Stream stream = archive.CreateEntry("poznamky.txt").Open()) stream.Write(note, 0, note.Length);
                        foreach (string image in images)
                            using (Stream input = File.OpenRead(image))
                            using (Stream stream = archive.CreateEntry("snimky/" + Path.GetFileName(image), CompressionLevel.Optimal).Open()) input.CopyTo(stream);
                        foreach (string video in videos)
                            using (Stream input = File.OpenRead(video))
                            using (Stream stream = archive.CreateEntry("videa/" + Path.GetFileName(video), CompressionLevel.NoCompression).Open()) input.CopyTo(stream);
                    }
                    output.Flush(true);
                }
                NotesStore.CommitFile(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Validate/unpack into separate staging space before adding an independent project.</summary>
        /// <returns>New project with its own Id and folder, added to the supplied state.</returns>
        /// <remarks>Allow up to 1 GiB of declared uncompressed data, 32 MiB of text, 10,000 PNGs, and 1,000 videos. Do not replace existing projects.</remarks>
        internal static Project Import(NotesStore store, NotesState state, string file)
        {
            string staging = Path.Combine(store.DirectoryPath, ".import-" + Guid.NewGuid().ToString("N"));
            string destination = null;
            bool ownsDestination = false;
            Project project = null;
            string oldSelected = state.SelectedId;
            int longestAttachment = 0;
            Dictionary<string, string> extracted = new Dictionary<string, string>();
            try
            {
                using (FileStream input = File.OpenRead(file))
                using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, false, Utf8))
                {
                    HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long size = 0;
                    int images = 0;
                    int videos = 0;
                    if (archive.Entries.Count > 11004) throw new InvalidDataException(Ui.T("The archive contains too many files."));
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string name = entry.FullName;
                        if (!paths.Add(name)) throw new InvalidDataException(Ui.T("The archive contains a duplicate path: ") + name);
                        bool image = name.StartsWith("snimky/", StringComparison.Ordinal) && name.Length > 7;
                        bool video = name.StartsWith("videa/", StringComparison.Ordinal) && name.Length > 6;
                        if (image && ++images > 10000) throw new InvalidDataException(Ui.T("The archive contains too many images."));
                        if (video && ++videos > 1000) throw new InvalidDataException(Ui.T("The archive contains too many videos."));
                        if ((name == "snimky/" || name == "videa/") && entry.Length != 0) throw new InvalidDataException(Ui.T("Invalid folder entry in the archive."));
                        string leaf = image ? name.Substring(7) : video ? name.Substring(6) : name;
                        if (image || video) longestAttachment = Math.Max(longestAttachment, leaf.Length);
                        if (name != "projekt.xml" && name != "poznamky.txt" && name != "snimky/" && name != "videa/" &&
                            ((!image && !video) || !ValidLeaf(leaf) || (image && !leaf.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) || (video && !VideoFormats.Supports(leaf))))
                            throw new InvalidDataException(Ui.T("Disallowed path in the archive: ") + name);
                        size = checked(size + entry.Length);
                        if (size > MaxBytes) throw new InvalidDataException(Ui.T("The unpacked project exceeds 1 GiB."));
                    }
                    ZipArchiveEntry metadata = archive.GetEntry("projekt.xml"), note = archive.GetEntry("poznamky.txt");
                    if (metadata == null || note == null || metadata.Length > 65536 || note.Length > MaxNoteBytes)
                        throw new InvalidDataException(Ui.T("The archive is not a valid Mark´s Marks project."));
                    ProjectPackage package;
                    using (Stream stream = metadata.Open())
                    using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                        package = (ProjectPackage)Metadata.Deserialize(reader);
                    if (package == null || (package.Version != 1 && package.Version != 2) || (package.Version == 1 && videos != 0) || string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 1024)
                        throw new InvalidDataException(Ui.T("Unsupported version or invalid project name."));
                    Directory.CreateDirectory(staging);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName == "projekt.xml" || entry.FullName == "snimky/" || entry.FullName == "videa/") continue;
                        // Short staging names must not increase the original attachment's path length.
                        string temporaryName = entry.FullName == "poznamky.txt" ? "poznamky.txt" : "media-" + extracted.Count + Path.GetExtension(entry.FullName);
                        string target = Path.Combine(staging, temporaryName);
                        extracted.Add(entry.FullName, temporaryName);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (Stream stream = entry.Open())
                        using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            CopyLimited(stream, output, entry.Length);
                        if (entry.FullName.StartsWith("snimky/")) ValidateImage(target);
                        if (entry.FullName.StartsWith("videa/")) VideoFormats.Validate(target, entry.FullName);
                    }
                    byte[] text = File.ReadAllBytes(Path.Combine(staging, "poznamky.txt"));
                    int start = text.Length >= 3 && text[0] == 0xef && text[1] == 0xbb && text[2] == 0xbf ? 3 : 0;
                    project = new Project { Name = package.Name.Trim(), Text = Utf8.GetString(text, start, text.Length - start) };
                }
                string originalName = project.Name;
                int suffix = 2;
                while (state.Projects.Any(p => string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase)))
                    project.Name = originalName.Substring(0, Math.Min(1000, originalName.Length)) + " (" + suffix++ + ")";
                state.Projects.Add(project);
                store.AllocateFolders(state, longestAttachment);
                destination = store.ProjectDirectory(project);
                Directory.Move(staging, destination);
                ownsDestination = true;
                foreach (KeyValuePair<string, string> pair in extracted)
                    if (pair.Key != "poznamky.txt")
                    {
                        string target = Path.Combine(destination, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Move(Path.Combine(destination, pair.Value), target);
                    }
                state.SelectedId = project.Id;
                store.Save(state);
                return project;
            }
            catch
            {
                if (project != null) state.Projects.Remove(project);
                state.SelectedId = oldSelected;
                if (ownsDestination && Directory.Exists(destination)) Directory.Delete(destination, true);
                throw;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        /// <summary>Reject unsafe path components and reserved Windows device names.</summary>
        private static bool ValidLeaf(string leaf)
        {
            if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || leaf.EndsWith(".") || leaf.EndsWith(" ")) return false;
            string device = leaf.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device);
        }

        /// <summary>Copy exactly the declared byte count and reject length mismatches or limit overruns.</summary>
        private static void CopyLimited(Stream input, Stream output, long expected)
        {
            byte[] buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                copied += count;
                if (copied > expected || copied > MaxBytes) throw new InvalidDataException(Ui.T("Invalid file size in the archive."));
                output.Write(buffer, 0, count);
            }
            if (copied != expected) throw new InvalidDataException(Ui.T("The archive contains an incomplete file."));
        }

        /// <summary>Validate the PNG header, dimension limits, and decoding through GDI+.</summary>
        private static void ValidateImage(string file)
        {
            byte[] header = new byte[24];
            using (Stream input = File.OpenRead(file))
                if (input.Read(header, 0, header.Length) != header.Length || !header.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidDataException(Ui.T("The archive contains an invalid PNG image."));
            long width = (uint)((header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19]);
            long height = (uint)((header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23]);
            if (width == 0 || height == 0 || width > 30000 || height > 30000 || width * height > 100000000)
                throw new InvalidDataException(Ui.T("The image dimensions are too large or invalid."));
            using (Image image = Image.FromFile(file))
                if (image.Width != width || image.Height != height) throw new InvalidDataException(Ui.T("Invalid image dimensions."));
        }
    }
}
