using System.Runtime.InteropServices;
using System.Text;

namespace TermNest.Core.Win32;

/// <summary>
/// SetWinEventHook P/Invoke + the few EVENT_* values we use to keep tab
/// captions in sync with the embedded PuTTY window title.
/// </summary>
public static class WindowEvents
{
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        StringBuilder buffer = new(256);
        int len = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return len > 0 ? buffer.ToString() : string.Empty;
    }
}
