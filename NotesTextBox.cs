using System;
using System.Linq;
using System.Windows.Forms;

namespace ProjektovePoznamky
{
    /// <summary>Native text editor with sub-bullets, attachment pasting, and custom vertical scrolling.</summary>
    internal sealed class NotesTextBox : TextBox
    {
        /// <summary>Return true when attachment pasting consumes WM_PASTE; false preserves normal text pasting.</summary>
        internal Func<bool> TryPasteAttachment;
        /// <summary>Notify viewport changes so the custom scrollbar can be updated.</summary>
        internal event Action ViewChanged;
        private bool updatingView;
        private int wheelDelta;
        /// <summary>First visible visual line, including wrapped lines rather than only newline-delimited rows.</summary>
        internal int FirstVisibleLine { get { return ScrollNative.SendMessage(Handle, 0x00ce, IntPtr.Zero, IntPtr.Zero).ToInt32(); } }
        /// <summary>Number of visual lines after wrapping to the editor width.</summary>
        internal int VisualLineCount { get { return ScrollNative.SendMessage(Handle, 0x00ba, IntPtr.Zero, IntPtr.Zero).ToInt32(); } }
        /// <summary>Estimated complete visible lines based on client size and font.</summary>
        internal int VisibleLines { get { return Math.Max(1, (ClientSize.Height - 4) / Font.Height); } }
        /// <summary>Clamp the requested visual line and scroll the native editor by the difference.</summary>
        internal void ScrollToLine(int line)
        {
            line = Math.Max(0, Math.Min(Math.Max(0, VisualLineCount - VisibleLines), line));
            ScrollNative.SendMessage(Handle, 0x00b6, IntPtr.Zero, new IntPtr(line - FirstVisibleLine));
        }

        /// <summary>Intercept attachment pasting and the wheel, then report viewport changes without recursion.</summary>
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0302 && TryPasteAttachment != null && TryPasteAttachment()) return;
            if (message.Msg == 0x020a)
            {
                wheelDelta += unchecked((short)((long)message.WParam >> 16));
                int steps = wheelDelta / 120;
                wheelDelta %= 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                ScrollToLine(FirstVisibleLine - steps * (lines < 0 ? VisibleLines : lines));
                return;
            }
            base.WndProc(ref message);
            if (updatingView || !IsHandleCreated) return;
            updatingView = true;
            try { if (ViewChanged != null) ViewChanged(); }
            finally { updatingView = false; }
        }

        /// <summary>Handle Tab as indentation of selected lines instead of moving focus.</summary>
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Tab) { IndentSelection(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }

        /// <summary>Prepend a tab to existing indentation and add a dash only when no bullet exists.</summary>
        internal static string IndentLine(string line)
        {
            string body = line.TrimStart(' ', '\t');
            string whitespace = line.Substring(0, line.Length - body.Length);
            return "\t" + whitespace + (body.StartsWith("- ") || body == "-" ? "" : "- ") + body;
        }

        /// <summary>Indent all affected lines while preserving line endings and cursor/selection continuity.</summary>
        internal void IndentSelection()
        {
            int caret = SelectionStart;
            int length = SelectionLength;
            int start = caret == 0 ? 0 : Text.LastIndexOf('\n', caret - 1) + 1;
            int end = Text.IndexOf('\n', Math.Max(caret, caret + length - 1));
            if (end < 0) end = TextLength;
            string old = Text.Substring(start, end - start);
            string indented = string.Join("\n", old.Split('\n').Select(IndentLine));
            Select(start, end - start);
            SelectedText = indented;
            if (length == 0) Select(caret + indented.Length - old.Length, 0);
            else Select(start, indented.Length);
        }
    }
}
