using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ProjektovePoznamky
{
    /// <summary>Supported extensions and lightweight container checks before copying video files.</summary>
    internal static class VideoFormats
    {
        internal const string Pattern = "*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.mpg;*.mpeg";
        private static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg" };
        /// <summary>Check the filename extension against the supported video container list.</summary>
        internal static bool Supports(string file) { return Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase); }

        /// <summary>Validate the extension and basic 16-byte container header; this does not guarantee codec playback.</summary>
        /// <param name="file">File whose header is read.</param>
        /// <param name="originalName">Original name used to validate a .tmp file&#x27;s extension, or null.</param>
        /// <remarks>This checks container type, not complete decoding. MediaElement can later fail on a valid header.</remarks>
        internal static void Validate(string file, string originalName = null)
        {
            string name = originalName ?? file;
            if (!Supports(name)) throw new InvalidDataException(Ui.T("Unsupported video format. Use MP4, M4V, MOV, AVI, WMV or MPEG."));
            byte[] header = new byte[16];
            using (FileStream input = File.OpenRead(file))
                if (input.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException(Ui.T("The video is empty or damaged."));
            string extension = Path.GetExtension(name).ToLowerInvariant();
            bool valid;
            if (extension == ".avi") valid = Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "AVI ";
            else if (extension == ".wmv") valid = header.SequenceEqual(new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0, 0xaa, 0, 0x62, 0xce, 0x6c });
            else if (extension == ".mpg" || extension == ".mpeg") valid = header[0] == 0 && header[1] == 0 && header[2] == 1 && (header[3] == 0xba || header[3] == 0xb3);
            else valid = new[] { "ftyp", "moov", "mdat", "wide", "free", "skip" }.Contains(Encoding.ASCII.GetString(header, 4, 4));
            if (!valid) throw new InvalidDataException(Ui.T("The file does not contain a valid video of this format."));
        }
    }
}
