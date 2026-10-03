using Avalonia.Platform;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BassRouter;

/// <summary>
/// Small platform specific bits that Avalonia doesn't cover: system tray detection and desktop notifications.
/// </summary>
public static class DesktopIntegration
{
    private const string AppName = "BassRouter";

    /// <summary>
    /// Whether the desktop has a system tray the app can hide into.
    /// Windows always does. On Linux this needs a StatusNotifierItem host
    /// (KDE has one built in, GNOME needs the AppIndicator extension).
    /// </summary>
    public static bool IsTrayAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return true;

        string? reply = RunGDBus(
            "call", "--session",
            "--dest", "org.freedesktop.DBus",
            "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner",
            "org.kde.StatusNotifierWatcher");

        return reply?.Contains("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Shows a desktop notification. Failures are ignored, notifications are best effort.
    /// </summary>
    /// <param name="windowHandle">A window handle for the balloon tip on Windows.</param>
    public static void ShowNotification(string message, nint windowHandle)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                WindowsBalloonTip.Show(windowHandle, AppName, message);
            else if (OperatingSystem.IsLinux())
                Task.Run(() => ShowFreedesktopNotification(message));
        }
        catch { }
    }

    public static void Cleanup()
    {
        if (OperatingSystem.IsWindows())
            WindowsBalloonTip.Remove();
    }

    private static void ShowFreedesktopNotification(string message)
    {
        RunGDBus(
            "call", "--session",
            "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.Notify",
            AppName,                                        // app_name
            "0",                                            // replaces_id
            GetNotificationIconPath() ?? "audio-speakers",  // app_icon
            AppName,                                        // summary
            message,                                        // body
            "[]",                                           // actions
            "{'desktop-entry': <'bassrouter'>}",            // hints
            "5000");                                        // expire_timeout (ms)
    }

    /// <summary>
    /// Notification daemons need the icon as a file, so copy the embedded one to the cache directory.
    /// </summary>
    private static string? GetNotificationIconPath()
    {
        try
        {
            string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdgCache
                ? xdgCache
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

            string iconPath = Path.Combine(cacheHome, "BassRouter", "icon.png");
            if (!File.Exists(iconPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                using Stream source = AssetLoader.Open(new Uri("avares://BassRouter/Resources/Icon.png"));
                using FileStream destination = File.Create(iconPath);
                source.CopyTo(destination);
            }

            return iconPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? RunGDBus(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using Process? process = Process.Start(startInfo);
            if (process == null)
                return null;

            // Read asynchronously so the timeout still applies if gdbus hangs (e.g. on an unresponsive session bus)
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                process.Kill();
                return null;
            }

            Task.WaitAll(output, errors);
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            // gdbus isn't installed
            return null;
        }
    }

    /// <summary>
    /// Avalonia's tray icon can't show balloon tips, so a second, short-lived notification icon is used for them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class WindowsBalloonTip
    {
        private const uint NIM_ADD = 0x0;
        private const uint NIM_DELETE = 0x2;
        private const uint NIF_ICON = 0x2;
        private const uint NIF_TIP = 0x4;
        private const uint NIF_INFO = 0x10;
        private const uint NIIF_WARNING = 0x2;
        private const uint IconId = 0xBA55;
        private const int IDI_APPLICATION = 32512;

        private static readonly object Lock = new();
        private static nint CurrentWindow;
        private static nint Icon;
        private static int Generation;

        public static void Show(nint windowHandle, string title, string message)
        {
            if (windowHandle == 0)
                return;

            int generation;
            lock (Lock)
            {
                RemoveNoLock();

                if (Icon == 0)
                    Icon = LoadAppIcon();

                var data = CreateData(windowHandle);
                data.uFlags = NIF_ICON | NIF_TIP | NIF_INFO;
                data.hIcon = Icon;
                data.szTip = title;
                data.szInfoTitle = title;
                data.szInfo = message.Length > 255 ? message[..255] : message;
                data.dwInfoFlags = NIIF_WARNING;

                if (Shell_NotifyIconW(NIM_ADD, ref data))
                    CurrentWindow = windowHandle;

                generation = ++Generation;
            }

            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                lock (Lock)
                {
                    if (generation == Generation)
                        RemoveNoLock();
                }
            });
        }

        public static void Remove()
        {
            lock (Lock)
            {
                RemoveNoLock();
            }
        }

        private static void RemoveNoLock()
        {
            if (CurrentWindow == 0)
                return;

            var data = CreateData(CurrentWindow);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            CurrentWindow = 0;
        }

        private static NOTIFYICONDATAW CreateData(nint windowHandle)
        {
            return new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = windowHandle,
                uID = IconId,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };
        }

        private static nint LoadAppIcon()
        {
            nint icon = Environment.ProcessPath is { } processPath
                ? ExtractIconW(GetModuleHandleW(null), processPath, 0)
                : 0;

            // ExtractIcon returns 1 when the file isn't an executable
            return icon > 1 ? icon : LoadIconW(0, IDI_APPLICATION);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern nint ExtractIconW(nint hInst, string pszExeFileName, uint nIconIndex);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint LoadIconW(nint hInstance, nint lpIconName);
    }
}
