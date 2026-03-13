// Helper utilities for undocumented Windows 11 shenanigans
// Based on: https://github.com/SamsidParty/IgniteView/blob/520c571fc0134f2efd62eead538e41f99913b1f5/IgniteView.Desktop/Types/Win32WebWindow.cs


using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;

namespace SamsidParty
{
    public class WindowHelpers
    {
        #region Native Imports

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, int[] pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);


        [DllImport("dwmapi.dll")]
        public static extern void DwmGetColorizationColor(ref uint pcrColorization, ref bool pfOpaqueBlend);

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        public enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }


        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        public enum DWM_WINDOW_CORNER_PREFERENCE
        {
            DWMWA_WINDOW_CORNER_PREFERENCE_UNDEFINED = 0,
            DWMWA_WINDOW_CORNER_PREFERENCE_DONOTROUND = 1,
            DWMWA_WINDOW_CORNER_PREFERENCE_ROUND = 2,
            DWMWA_WINDOW_CORNER_PREFERENCE_ROUNDSMALL = 3
        }

        public enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_INVALID_STATE = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, UInt32 uFlags);

        public static bool RefreshWindow(IntPtr hWnd) => SetWindowPos(hWnd, 0, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020);

        public enum WindowBackgroundMode
        {
            Disabled = 0,
            Mica = 2,
            Acrylic = 3,
            DarkMica = 4,
            BlurBehind = 5,
        }

        /// <summary>
        /// Returns true if the system is running windows 11 or later
        /// </summary>
        public static bool IsWindows11
        {
            get
            {
                var reg = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

                var currentBuildStr = (string)reg.GetValue("CurrentBuild");
                var currentBuild = int.Parse(currentBuildStr);

                return currentBuild >= 22000;
            }
        }

        #endregion

        /// <summary>
        /// Sets the window background mode in Windows 11. The window needs to be transparent first.
        /// </summary>
        public static void SetWindowBackgroundMode(IntPtr hwnd, WindowBackgroundMode backgroundMode)
        {
            if (!IsWindows11) { return; }

            int enable = (int)backgroundMode < 5 ? (int)backgroundMode : 0;
            DwmSetWindowAttribute(hwnd, 38, ref enable, Marshal.SizeOf(typeof(int)));

            if (backgroundMode == WindowBackgroundMode.BlurBehind)
            {
                var accent = new AccentPolicy();
                var accentStructSize = Marshal.SizeOf(accent);
                accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;

                var accentPtr = Marshal.AllocHGlobal(accentStructSize);
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData();
                data.Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY;
                data.SizeOfData = accentStructSize;
                data.Data = accentPtr;

                SetWindowCompositionAttribute(hwnd, ref data);

                Marshal.FreeHGlobal(accentPtr);
            }
        }

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
