// Helper utilities for undocumented Windows 11 shenanigans
// Based on: https://github.com/SamsidParty/IgniteView/blob/520c571fc0134f2efd62eead538e41f99913b1f5/IgniteView.Desktop/Types/Win32WebWindow.cs
// Blur is handled by Avalonia (TransparencyLevelHint), so only the corner fix is left here.

using System.Runtime.InteropServices;

namespace SamsidParty
{
    public static class WindowHelpers
    {
        #region Native Imports

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        private enum DWM_WINDOW_CORNER_PREFERENCE
        {
            DWMWA_WINDOW_CORNER_PREFERENCE_UNDEFINED = 0,
            DWMWA_WINDOW_CORNER_PREFERENCE_DONOTROUND = 1,
            DWMWA_WINDOW_CORNER_PREFERENCE_ROUND = 2,
            DWMWA_WINDOW_CORNER_PREFERENCE_ROUNDSMALL = 3
        }

        #endregion

        /// <summary>
        /// Returns true if the system is running windows 11 or later
        /// </summary>
        public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        /// <summary>
        /// Fixes the window corners to be rounded on Windows 11
        /// </summary>
        public static void FixWindows11Corners(IntPtr hwnd)
        {
            if (!IsWindows11) { return; }
            var preference = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWA_WINDOW_CORNER_PREFERENCE_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
    }
}
