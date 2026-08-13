using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Win32 interop helpers.</summary>
    public static class NativeMethods
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 2004+; 19 on older 1809/1903 builds).
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Paints the window's title bar / caption dark to match the app theme.</summary>
        public static void UseImmersiveDarkTitleBar(Window window, bool enabled = true)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;
                int value = enabled ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
            }
            catch
            {
                // Older Windows without dwmapi support — ignore.
            }
        }
    }
}
