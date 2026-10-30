using System.Runtime.InteropServices;
using System.Text;

namespace ManagerAgent;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_BACK = 0x08, VK_CTRL = 0x11, VK_A = 0x41;

    /// <summary>Seconds since any keyboard/mouse input on this PC.</summary>
    public static double IdleSeconds
    {
        get
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (!GetLastInputInfo(ref info)) return 0;
                return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
            }
            catch { return 0; }
        }
    }

    public static void Key(byte vk, bool up)
    {
        byte scan = (byte)MapVirtualKey(vk, 0);
        keybd_event(vk, scan, up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public const int VK_LBUTTON = 0x01;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12; // Alt

    public static bool IsDown(int vk)
    {
        try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        catch { return false; }
    }
}

/// <summary>Finds the Unity game window of this process (class "UnityWndClass").</summary>
internal static class GameWindow
{
    private static IntPtr _hwnd;
    private static double _nextLookup;
    private static readonly uint Pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    public static IntPtr Handle
    {
        get
        {
            if (_hwnd != IntPtr.Zero && Clock.Now < _nextLookup && Win32.IsWindow(_hwnd)) return _hwnd;
            _nextLookup = Clock.Now + 5;
            var found = Find();
            if (found != IntPtr.Zero) _hwnd = found;
            return _hwnd;
        }
    }

    public static bool IsForeground
    {
        get
        {
            try
            {
                var h = Handle;
                return h != IntPtr.Zero && Win32.GetForegroundWindow() == h;
            }
            catch { return false; }
        }
    }

    private static IntPtr Find()
    {
        IntPtr best = IntPtr.Zero;
        try
        {
            var sb = new StringBuilder(256);
            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowThreadProcessId(h, out var pid);
                if (pid != Pid || !Win32.IsWindowVisible(h)) return true;
                sb.Clear();
                Win32.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "UnityWndClass") { best = h; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Guard.Report("window", ex); }

        if (best == IntPtr.Zero)
        {
            // Fallback: the foreground window if it belongs to us.
            var fg = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(fg, out var pid);
            if (pid == Pid) best = fg;
        }
        return best;
    }

    public static bool TryClientSize(out int w, out int h)
    {
        w = h = 0;
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetClientRect(hwnd, out var r)) return false;
        w = r.Right - r.Left;
        h = r.Bottom - r.Top;
        return w > 0 && h > 0;
    }

    public static bool TryCursorClient(out int x, out int y)
    {
        x = y = 0;
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetCursorPos(out var p)) return false;
        if (!Win32.ScreenToClient(hwnd, ref p)) return false;
        x = p.X; y = p.Y;
        return true;
    }

    public static bool TryClientToScreen(int cx, int cy, out int sx, out int sy)
    {
        sx = sy = 0;
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return false;
        var p = new Win32.POINT { X = cx, Y = cy };
        if (!Win32.ClientToScreen(hwnd, ref p)) return false;
        sx = p.X; sy = p.Y;
        return true;
    }
}

/// <summary>A hotkey such as "Ctrl+Shift+H".</summary>
internal readonly struct KeyCombo
{
    public readonly int Vk;
    public readonly bool Ctrl, Shift, Alt;
    public readonly string Text;

    private KeyCombo(int vk, bool ctrl, bool shift, bool alt, string text)
    {
        Vk = vk; Ctrl = ctrl; Shift = shift; Alt = alt; Text = text;
    }

    public bool IsValid => Vk != 0;

    public static KeyCombo Parse(string text)
    {
        bool ctrl = false, shift = false, alt = false;
        int vk = 0;
        foreach (var raw in (text ?? "").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.Trim().ToUpperInvariant();
            switch (p)
            {
                case "CTRL": case "CONTROL": ctrl = true; continue;
                case "SHIFT": shift = true; continue;
                case "ALT": alt = true; continue;
            }
            vk = KeyToVk(p);
        }
        return new KeyCombo(vk, ctrl, shift, alt, text);
    }

    private static int KeyToVk(string k)
    {
        if (k.Length == 1)
        {
            char c = k[0];
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k.Substring(1), out var f) && f >= 1 && f <= 24)
            return 0x70 + (f - 1);
        return k switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "`" or "BACKQUOTE" or "TILDE" => 0xC0,
            _ => 0,
        };
    }
}

/// <summary>Per-frame keyboard/mouse edge detection (only while the game window is focused).</summary>
/// <summary>
/// Plays real keyboard input one event per frame (so the game sees every key), only while the
/// game window has focus.
/// </summary>
internal sealed class KeyboardPlayer
{
    private readonly Queue<(byte vk, bool up)> _q = new();
    public bool Busy => _q.Count > 0;

    public bool Type(string text, bool selectAll, bool enter)
    {
        if (!GameWindow.IsForeground) return false;
        if (selectAll)
        {
            // Ctrl+A, then the digits replace the selection. Never Backspace: outside a text box it's FM's "Back".
            _q.Enqueue((Win32.VK_CTRL, false)); _q.Enqueue((Win32.VK_A, false));
            _q.Enqueue((Win32.VK_A, true)); _q.Enqueue((Win32.VK_CTRL, true));
        }
        foreach (var ch in text ?? "")
        {
            byte vk = ch >= '0' && ch <= '9' ? (byte)ch : ch == '.' ? (byte)0xBE : ch == ',' ? (byte)0xBC : (byte)0;
            if (vk == 0) continue;
            _q.Enqueue((vk, false)); _q.Enqueue((vk, true));
        }
        if (enter) { _q.Enqueue((Win32.VK_RETURN, false)); _q.Enqueue((Win32.VK_RETURN, true)); }
        return true;
    }

    public bool Press(byte vk)
    {
        if (!GameWindow.IsForeground) return false;
        _q.Enqueue((vk, false)); _q.Enqueue((vk, true));
        return true;
    }

    public void Tick()
    {
        if (_q.Count == 0) return;
        if (!GameWindow.IsForeground)
        {
            // Lost focus: release anything held and give up.
            Win32.Key(Win32.VK_CTRL, true);
            _q.Clear();
            return;
        }
        var (vk, up) = _q.Dequeue();
        Win32.Key(vk, up);
    }

    public void Cancel()
    {
        if (_q.Count > 0) Win32.Key(Win32.VK_CTRL, true);
        _q.Clear();
    }
}

internal sealed class InputState
{
    private readonly HashSet<int> _tracked = new() { Win32.VK_LBUTTON, Win32.VK_SHIFT, Win32.VK_CONTROL, Win32.VK_MENU };
    private readonly Dictionary<int, bool> _now = new();
    private readonly Dictionary<int, bool> _prev = new();

    public bool Focused { get; private set; }

    public void Track(KeyCombo combo)
    {
        if (combo.IsValid) _tracked.Add(combo.Vk);
    }

    public void Update()
    {
        Focused = GameWindow.IsForeground;
        foreach (var vk in _tracked)
        {
            _prev[vk] = _now.TryGetValue(vk, out var was) && was;
            _now[vk] = Focused && Win32.IsDown(vk);
        }
    }

    public bool Down(int vk) => _now.TryGetValue(vk, out var d) && d;
    public bool Pressed(int vk) => Down(vk) && !(_prev.TryGetValue(vk, out var p) && p);

    public bool Pressed(KeyCombo c)
    {
        if (!c.IsValid || !Pressed(c.Vk)) return false;
        return Down(Win32.VK_CONTROL) == c.Ctrl && Down(Win32.VK_SHIFT) == c.Shift && Down(Win32.VK_MENU) == c.Alt;
    }

    public bool LeftClicked => Pressed(Win32.VK_LBUTTON);
}
