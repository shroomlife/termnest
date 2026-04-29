using System.Runtime.InteropServices;

namespace TermNest.Core.Win32;

/// <summary>
/// Thin wrapper around <c>RegisterHotKey</c> / <c>UnregisterHotKey</c>. The
/// caller subclasses the WinUI window's WM_HOTKEY by hooking
/// SubclassDelegateProc; the host app translates hotkey ids back to actions.
///
/// API surface intentionally minimal for Phase 5; richer keymap editor lands
/// in Phase 7 alongside settings.
/// </summary>
public static class HotkeyManager
{
    [Flags]
    public enum Modifiers : uint
    {
        None    = 0x0000,
        Alt     = 0x0001,
        Control = 0x0002,
        Shift   = 0x0004,
        Win     = 0x0008,
        NoRepeat = 0x4000,
    }

    public const uint WM_HOTKEY = 0x0312;

    // Per-process unique id allocator. RegisterHotKey requires ids in
    // 0x0000-0xBFFF; 0x0001 is the first valid value. Two callers picking
    // the same id silently fail the second registration, so route allocation
    // through Register() instead of constructing ids by hand.
    private static int _nextId;

    /// <summary>
    /// Registers a hotkey and returns its allocated id. Throws if the OS
    /// rejects the registration (combo already grabbed by another app, or
    /// id pool exhausted).
    /// </summary>
    public static int Register(IntPtr hWnd, Modifiers modifiers, uint virtualKey)
    {
        int id = Interlocked.Increment(ref _nextId);
        if (id > 0xBFFF)
        {
            throw new InvalidOperationException("Hotkey id pool exhausted (>0xBFFF).");
        }
        if (!RegisterHotKey(hWnd, id, (uint)modifiers, virtualKey))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterHotKey failed (0x{err:X8})");
        }
        return id;
    }

    public static bool Unregister(IntPtr hWnd, int id) => UnregisterHotKey(hWnd, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
