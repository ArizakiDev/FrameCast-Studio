using System.Runtime.InteropServices;

namespace FrameCastStudio.App;

internal sealed class TrayIcon : IDisposable
{
    private const uint WM_USER = 0x0400, WM_TRAY = WM_USER + 1, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint MF_STRING = 0, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;
    private const int ID_SHOW = 1, ID_QUIT = 2;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly WndProcDelegate _proc;
    private IntPtr _hwnd, _hIcon;
    private bool _visible, _disposed;
    private readonly string _tip;
    private readonly string _className = "FrameCastStudioTray_" + Guid.NewGuid().ToString("N");

    public event Action? ShowRequested;
    public event Action? QuitRequested;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr inst);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr inst, string exe, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    public TrayIcon(string tooltip)
    {
        _tip = tooltip;
        _proc = WndProc;
        IntPtr inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, _className, "FrameCastStudioTray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);

        string exe = Environment.ProcessPath ?? "";
        IntPtr h = exe.Length > 0 ? ExtractIcon(inst, exe, 0) : IntPtr.Zero;
        if (h.ToInt64() > 1) _hIcon = h;
        else _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Show()
    {
        if (_visible || _disposed) return;
        var d = Data();
        _visible = Shell_NotifyIcon(NIM_ADD, ref d);
    }

    public void Hide()
    {
        if (!_visible) return;
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _visible = false;
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY, hIcon = _hIcon, szTip = _tip,
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) ShowRequested?.Invoke();
            else if (ev == WM_RBUTTONUP)
            {
                GetCursorPos(out var pt);
                IntPtr menu = CreatePopupMenu();
                AppendMenu(menu, MF_STRING, new UIntPtr(ID_SHOW), "Afficher FrameCast Studio");
                AppendMenu(menu, MF_STRING, new UIntPtr(ID_QUIT), "Quitter");
                SetForegroundWindow(hWnd);
                int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hWnd, IntPtr.Zero);
                DestroyMenu(menu);
                if (cmd == ID_SHOW) ShowRequested?.Invoke();
                else if (cmd == ID_QUIT) QuitRequested?.Invoke();
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        UnregisterClass(_className, GetModuleHandle(null));
    }
}
