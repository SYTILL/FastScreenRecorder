using System.Runtime.InteropServices;
using System.Text;

static class Native
{
    public const int WM_HOTKEY = 0x0312, WM_NCHITTEST = 0x84, HTCAPTION = 2, HTTRANSPARENT = -1;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    /// Keeps our own UI out of every recording.
    public static void ExcludeFromCapture(IntPtr h) => SetWindowDisplayAffinity(h, 0x11);

    public static IntPtr Monitor(Screen s) => MonitorFromPoint(new Point(s.Bounds.Left + 1, s.Bounds.Top + 1), 2);

    /// Top-level windows a person would recognize as "an app".
    public static List<(IntPtr Hwnd, string Title)> Windows()
    {
        var list = new List<(IntPtr, string)>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // hidden, minimized, owned
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                                // WS_EX_TOOLWINDOW
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // other virtual desktop etc.
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length > 0 && title != "Program Manager") list.Add((h, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
