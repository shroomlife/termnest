namespace TermNest.Core.Win32;

/// <summary>
/// Window style constants used when reparenting / restyling the embedded
/// PuTTY window. Mirrors the subset of values that the 3.x WinForms
/// NativeMethods.cs declared inline.
/// </summary>
public static class WindowStyles
{
    // --- GetWindowLong / SetWindowLong indexes ---
    public const int GWL_STYLE       = -16;
    public const int GWL_EXSTYLE     = -20;
    public const int GWL_HWNDPARENT  = -8;

    // --- WS_EX_* styles ---
    public const long WS_EX_TOOLWINDOW   = 0x00000080L;
    public const long WS_EX_NOACTIVATE   = 0x08000000L;
    public const long WS_EX_APPWINDOW    = 0x00040000L;

    // --- WS_* style flags relevant to embedding ---
    public const long WS_BORDER     = 0x00800000L;
    public const long WS_DLGFRAME   = 0x00400000L;
    public const long WS_CAPTION    = 0x00C00000L;  // WS_BORDER | WS_DLGFRAME
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU    = 0x00080000L;
    public const long WS_MINIMIZE   = 0x20000000L;
    public const long WS_MAXIMIZE   = 0x01000000L;
    public const long WS_HSCROLL    = 0x00100000L;
    public const long WS_VSCROLL    = 0x00200000L;
    public const long WS_VISIBLE    = 0x10000000L;
    public const long WS_CHILD      = 0x40000000L;
    public const long WS_POPUP      = 0x80000000L;

    // --- ShowWindow nCmdShow ---
    public const int SW_HIDE     = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOW     = 5;

    // --- Window messages ---
    public const uint WM_CLOSE   = 0x0010;
    public const uint WM_DESTROY = 0x0002;
}
