using System.Runtime.InteropServices;
using System.Text;

namespace FrameCastStudio.Core.Capture;

public enum SourceKind { Monitor, Window }

public sealed record CaptureSource(SourceKind Kind, IntPtr Handle, string Name, bool IsPrimary = false);

public static class CaptureSources
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size; public Rect Monitor; public Rect WorkArea; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
    private const int DWMWA_CLOAKED = 14; private const uint GA_ROOT = 2;

    public static List<CaptureSource> GetMonitors()
    {
        var list = new List<CaptureSource>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Rect r, IntPtr __) =>
        {
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool primary = (mi.Flags & MONITORINFOF_PRIMARY) != 0;
                int w = mi.Monitor.R - mi.Monitor.L, h = mi.Monitor.B - mi.Monitor.T;
                string name = $"{(primary ? "Écran principal" : "Écran")} · {w}x{h}";
                list.Add(new CaptureSource(SourceKind.Monitor, hMon, name, primary));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ToList();
    }

    public static List<CaptureSource> GetWindows()
    {
        var list = new List<CaptureSource>(); var shell = GetShellWindow(); int self = Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            if (GetAncestor(hWnd, GA_ROOT) != hWnd) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(hWnd); if (len == 0) return true;
            var sb = new StringBuilder(len + 1); GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString(); if (string.IsNullOrWhiteSpace(title)) return true;
            list.Add(new CaptureSource(SourceKind.Window, hWnd, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
