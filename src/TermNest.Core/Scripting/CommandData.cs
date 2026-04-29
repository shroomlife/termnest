using System.Runtime.InteropServices;
using TermNest.Core.Win32;

namespace TermNest.Core.Scripting;

/// <summary>
/// One scripting step — either a literal string to type into the terminal,
/// or a key combination, optionally followed by a delay. Mirrors the v3
/// TermNest.Utils.CommandData but uses IntPtr-based Send/PostMessage
/// throughout so x64 handles aren't truncated.
/// </summary>
public sealed class CommandData
{
    public string? Command { get; init; }
    public KeyCombo? KeyCombo { get; init; }
    public TimeSpan Delay { get; init; }

    /// <summary>
    /// Posts the command into the target HWND. <b>Synchronous</b>: includes
    /// <see cref="Thread.Sleep"/> for the <see cref="Delay"/> tail. Never call
    /// from the UI thread — wrap in <c>Task.Run</c> or call from an
    /// <c>async</c> path that has already moved off the dispatcher.
    /// <para>
    /// LIMITATION: <c>WM_CHAR</c> is sent per UTF-16 code unit. Non-BMP
    /// characters (CJK Extension B, emoji) are surrogate pairs that PuTTY
    /// rejects individually — silent data loss for codepoints above U+FFFF.
    /// </para>
    /// </summary>
    public void SendToTerminal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (!string.IsNullOrEmpty(Command))
        {
            foreach (char c in Command!)
            {
                NativeMethods.PostMessage(hwnd, WM_CHAR, new IntPtr(c), IntPtr.Zero);
            }
        }

        if (KeyCombo != null)
        {
            if (KeyCombo.Control) NativeMethods.PostMessage(hwnd, WM_KEYDOWN, new IntPtr(VK_CONTROL), IntPtr.Zero);
            if (KeyCombo.Shift)   NativeMethods.PostMessage(hwnd, WM_KEYDOWN, new IntPtr(VK_SHIFT),   IntPtr.Zero);

            NativeMethods.PostMessage(hwnd, WM_KEYDOWN, new IntPtr(KeyCombo.VirtualKey), IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, WM_KEYUP,   new IntPtr(KeyCombo.VirtualKey), IntPtr.Zero);

            if (KeyCombo.Shift)   NativeMethods.PostMessage(hwnd, WM_KEYUP, new IntPtr(VK_SHIFT),   IntPtr.Zero);
            if (KeyCombo.Control) NativeMethods.PostMessage(hwnd, WM_KEYUP, new IntPtr(VK_CONTROL), IntPtr.Zero);
        }

        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }
    }

    private const uint WM_CHAR    = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP   = 0x0101;
    private const int VK_CONTROL  = 0x11;
    private const int VK_SHIFT    = 0x10;
}

public sealed class KeyCombo
{
    public required int VirtualKey { get; init; }
    public bool Control { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
}
