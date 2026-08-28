using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace TavernDesk.App.Presentation;

internal static class WindowChromeService
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int WmGetMinMaxInfo = 0x0024;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(
            window,
            new WindowChrome
            {
                CaptionHeight = 40,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ApplyRoundedCorners(handle);
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        };
    }

    public static void Minimize(Window window) =>
        SystemCommands.MinimizeWindow(window);

    public static void ToggleMaximize(Window window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
            return;
        }

        SystemCommands.MaximizeWindow(window);
    }

    public static void Close(Window window) =>
        SystemCommands.CloseWindow(window);

    private static void ApplyRoundedCorners(IntPtr handle)
    {
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(
            handle,
            DwmwaWindowCornerPreference,
            ref preference,
            sizeof(int));
    }

    private static IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.Work;
                var monitorArea = monitorInfo.Monitor;
                info.MaxPosition.X = Math.Abs(work.Left - monitorArea.Left);
                info.MaxPosition.Y = Math.Abs(work.Top - monitorArea.Top);
                info.MaxSize.X = Math.Abs(work.Right - work.Left);
                info.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
                Marshal.StructureToPtr(info, lParam, true);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }
}
