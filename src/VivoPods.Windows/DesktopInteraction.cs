using System.Runtime.InteropServices;

namespace VivoPods.Windows;

public static class DesktopInteraction
{
    public static TimeSpan DoubleClickInterval => TimeSpan.FromMilliseconds(GetDoubleClickTime());
    public static (int X, int Y) CursorPosition
    {
        get { GetCursorPos(out var point); return (point.X, point.Y); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
}
