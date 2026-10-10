using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ProjektovePoznamky
{
    /// <summary>Manage premultiplied alpha and temporary GDI resources for the floating logo.</summary>
    internal static class LayeredOverlay
    {
        /// <summary>Single-byte BLENDFUNCTION fields for Win32 alpha composition.</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { public byte Operation, Flags, Alpha, AlphaFormat; }
        /// <summary>Acquire the screen DC; the caller must return it using ReleaseDC.</summary>
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        /// <summary>Return a DC acquired by GetDC, rather than one created by CreateCompatibleDC.</summary>
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        /// <summary>Create a screen-compatible memory DC; release it using DeleteDC.</summary>
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        /// <summary>Release a memory DC after restoring its original GDI objects.</summary>
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        /// <summary>Select a bitmap into a DC and return the original object to restore before cleanup.</summary>
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
        /// <summary>Release an HBITMAP after it is deselected from every DC.</summary>
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr bitmap);
        /// <summary>Upload a premultiplied-alpha bitmap to a layered window without a solid background.</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dc, ref Point location, ref Size size, IntPtr source, ref Point origin, int color, ref Blend blend, int flags);
        /// <summary>Read HWND style flags, also used to verify actual transparency.</summary>
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int WindowStyle(IntPtr window, int index);

        /// <summary>Create an independent premultiplied-alpha bitmap owned by the caller.</summary>
        internal static Bitmap Render(Image logo, Size size)
        {
            Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(logo, new Rectangle(Point.Empty, size));
            }
            return bitmap;
        }

        /// <summary>Upload the bitmap to an HWND and restore/release all GDI resources in finally.</summary>
        internal static void Apply(IntPtr window, Point location, Bitmap bitmap)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr handle = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                handle = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memory, handle);
                Point origin = Point.Empty;
                Size size = bitmap.Size;
                Blend blend = new Blend { Operation = 0, Flags = 0, Alpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(window, screen, ref location, ref size, memory, ref origin, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (handle != IntPtr.Zero) DeleteObject(handle);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
