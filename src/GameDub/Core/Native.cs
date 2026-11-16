using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GameDub.Core;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("xinput1_4.dll")] public static extern int XInputGetState(int idx, out XINPUT_STATE s);
    [StructLayout(LayoutKind.Sequential)] public struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [DllImport("xinput1_4.dll")] public static extern int XInputSetState(int idx, ref XINPUT_VIBRATION v);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint GW_OWNER = 4;

    public static string? GetProcessPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    public static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Client area of a window in screen (physical) pixels.</summary>
    public static System.Drawing.Rectangle GetClientScreenRect(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out var r)) return System.Drawing.Rectangle.Empty;
        var p = new POINT();
        ClientToScreen(hWnd, ref p);
        return new System.Drawing.Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Visible top-level windows (no owner, non-empty client) with their pid.</summary>
    public static System.Collections.Generic.List<(IntPtr hWnd, int pid)> TopLevelWindows()
    {
        var list = new System.Collections.Generic.List<(IntPtr, int)>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint pid);
                list.Add((h, (int)pid));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
