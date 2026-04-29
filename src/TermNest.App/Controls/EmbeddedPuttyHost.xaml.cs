using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TermNest.Core.Diagnostics;
using TermNest.Core.Sessions;
using TermNest.Core.Win32;
using Windows.Foundation;
using NativeMethods = TermNest.Core.Win32.NativeMethods;
using WindowEvents = TermNest.Core.Win32.WindowEvents;

namespace TermNest.App.Controls;

/// <summary>
/// Hosts a launched <c>putty.exe</c> "inside" the WinUI 3 shell by tracking
/// its top-level window over a placeholder Border in screen coordinates.
///
/// Why not SetParent? WinUI 3 windows render via Microsoft.UI.Composition
/// in NoRedirectionBitmap (NRB) mode — they have no GDI redirection surface.
/// SetParent-ing a top-level HWND into an NRB window leaves the child in a
/// state where window messages still arrive (which is why title-sync works)
/// but it has no surface to render onto. Visually invisible, by design.
///
/// Workaround: keep PuTTY as its own top-level window, declare the WinUI
/// shell as its OWNER via GWL_HWNDPARENT (so it minimizes/closes with us
/// and stays out of the taskbar via WS_EX_TOOLWINDOW), strip the chrome,
/// and continuously reposition it over the placeholder's screen rect.
/// </summary>
public sealed partial class EmbeddedPuttyHost : UserControl
{
    private static readonly TimeSpan HandlePollTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromMilliseconds(250);

    public event EventHandler<bool>? Exited;
    public event EventHandler<string>? TitleChanged;

    private readonly nint _hostWindowHandle;

    private Process? _process;
    private nint _childHwnd;
    private bool _isAttached;
    private volatile bool _isClosing;
    private nint _winEventHook;
    private WindowEvents.WinEventDelegate? _winEventDelegate;
    private readonly Lock _titleSync = new();
    private string _currentTitle = string.Empty;

    public string CurrentTitle
    {
        get { lock (_titleSync) return _currentTitle; }
    }

    public IntPtr ChildHwnd => _childHwnd;

    /// <summary>
    /// Forces a re-position of the embedded PuTTY window. Called by the shell
    /// when its outer window moves or resizes, since PuTTY is an owned top-level
    /// window whose placement is computed from the placeholder's screen rect.
    /// </summary>
    public void RefreshPosition() => OnSizeOrLayoutChanged(this, null);

    private void OnTabBecameVisible(object sender, RoutedEventArgs e)
    {
        if (_childHwnd == 0 || _isClosing) return;
        _ = NativeMethods.ShowWindow(_childHwnd, WindowStyles.SW_SHOW);
        OnSizeOrLayoutChanged(this, null);
    }

    private void OnTabBecameHidden(object sender, RoutedEventArgs e)
    {
        if (_childHwnd == 0 || _isClosing) return;
        // Hide only — never close from this path. The tab is being deselected
        // or the user is dragging it; the PuTTY process must keep running so
        // the SSH session survives a tab switch.
        _ = NativeMethods.ShowWindow(_childHwnd, WindowStyles.SW_HIDE);
    }

    public EmbeddedPuttyHost(nint hostWindowHandle)
    {
        if (hostWindowHandle == 0)
        {
            throw new ArgumentException("Host window HWND must be non-zero.", nameof(hostWindowHandle));
        }

        _hostWindowHandle = hostWindowHandle;
        InitializeComponent();

        // Layout drivers — any of these can move the placeholder; each forces
        // an MoveWindow refresh on the embedded PuTTY top-level window.
        SizeChanged += OnSizeOrLayoutChanged;
        HostPlaceholder.SizeChanged += OnSizeOrLayoutChanged;
        HostPlaceholder.LayoutUpdated += OnSizeOrLayoutChanged;

        // Tab visibility — TabView removes/adds the inactive tab's content
        // from the visual tree, so Loaded/Unloaded are the right hooks for
        // showing/hiding the owned PuTTY window. Important: never call
        // CloseAsync from Unloaded — the tab is being deselected, not closed.
        Loaded += OnTabBecameVisible;
        Unloaded += OnTabBecameHidden;
    }

    public async Task StartAsync(SessionData session, string puttyExePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_process != null)
        {
            throw new InvalidOperationException("EmbeddedPuttyHost already started.");
        }

        // Wait for the UserControl to be connected to the visual tree before
        // we touch any HWND lifecycle. Until Loaded fires, XamlRoot is null
        // and TransformToVisual returns identity — MoveWindow would silently
        // run with bogus coordinates. This is the documented WinUI 3 pattern
        // for "do something once layout is ready".
        if (!IsLoaded)
        {
            TaskCompletionSource loadedTcs = new();
            void OneShotLoaded(object s, RoutedEventArgs e)
            {
                Loaded -= OneShotLoaded;
                loadedTcs.TrySetResult();
            }
            Loaded += OneShotLoaded;
            DebugLog.Write("EmbeddedPuttyHost", "StartAsync waiting for Loaded");
            await loadedTcs.Task.ConfigureAwait(true);
            DebugLog.Write("EmbeddedPuttyHost", "Loaded fired, XamlRoot=" + (XamlRoot is null ? "null" : "ok"));
        }

        PuttyStartInfo info = PuttyStartInfo.Build(session, puttyExePath);

        ProcessStartInfo psi = new()
        {
            FileName = info.Executable,
            Arguments = info.Arguments,
            UseShellExecute = false,
            // Use Normal window style — Maximized would make PuTTY restore
            // to maximized whenever ShowWindow is called, fighting our
            // explicit MoveWindow placement.
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (!string.IsNullOrWhiteSpace(info.WorkingDirectory))
        {
            psi.WorkingDirectory = info.WorkingDirectory!;
        }

        _process = new Process
        {
            EnableRaisingEvents = true,
            StartInfo = psi,
        };
        _process.Exited += OnProcessExited;

        DebugLog.Write("EmbeddedPuttyHost", $"StartAsync exe={info.Executable} args={info.Arguments}");

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start {info.Executable}");
        }
        DebugLog.Write("EmbeddedPuttyHost", $"process started pid={_process.Id}");

        if (session.Protocol is not ConnectionProtocol.WINCMD and not ConnectionProtocol.PS)
        {
            try { _process.WaitForInputIdle(); } catch (InvalidOperationException) { /* console child */ }
        }

        _childHwnd = await CaptureMainWindowHandleAsync(_process, cancellationToken).ConfigureAwait(true);
        DebugLog.Write("EmbeddedPuttyHost", $"captured child HWND=0x{_childHwnd:X}");
        if (_childHwnd == 0)
        {
            throw new InvalidOperationException("Could not capture child process main window handle.");
        }

        AttachToHost();
        DebugLog.Write("EmbeddedPuttyHost", $"attached, isAttached={_isAttached}");
        // Initial position attempt; subsequent layout passes are picked up by
        // SizeChanged + LayoutUpdated + Loaded handlers wired in the ctor.
        OnSizeOrLayoutChanged(this, null);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool unexpected = !_isClosing;
        Exited?.Invoke(this, unexpected);
    }

    private static async Task<nint> CaptureMainWindowHandleAsync(Process process, CancellationToken cancellationToken)
    {
        nint hwnd = process.MainWindowHandle;
        if (hwnd != 0) return hwnd;

        DateTime deadline = DateTime.UtcNow + HandlePollTimeout;
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            process.Refresh();
            hwnd = process.MainWindowHandle;
            if (hwnd != 0) return hwnd;
        }
        return 0;
    }

    private void AttachToHost()
    {
        if (_childHwnd == 0 || _isAttached) return;

        // Step 1 — strip chrome so PuTTY looks "embedded" even though it stays
        // top-level. Keep WS_VISIBLE; remove caption/border/sysmenu/popup so it
        // renders frameless. Don't add WS_CHILD — we are NOT making it a child.
        nint style = NativeMethods.GetWindowLongPtr(_childHwnd, WindowStyles.GWL_STYLE);
        long longStyle = style.ToInt64();
        longStyle &= ~WindowStyles.WS_CAPTION;
        longStyle &= ~WindowStyles.WS_THICKFRAME;
        longStyle &= ~WindowStyles.WS_SYSMENU;
        longStyle &= ~WindowStyles.WS_MINIMIZE;
        longStyle &= ~WindowStyles.WS_MAXIMIZE;
        longStyle &= ~WindowStyles.WS_POPUP;
        longStyle |= WindowStyles.WS_VISIBLE;
        _ = NativeMethods.SetWindowLongPtr(_childHwnd, WindowStyles.GWL_STYLE, (nint)longStyle);

        // Step 2 — extended styles: TOOLWINDOW keeps PuTTY out of the taskbar
        // and Alt-Tab even though it remains top-level. NOACTIVATE prevents
        // it from stealing focus on positioning.
        nint exStyle = NativeMethods.GetWindowLongPtr(_childHwnd, WindowStyles.GWL_EXSTYLE);
        long longExStyle = exStyle.ToInt64();
        longExStyle |= WindowStyles.WS_EX_TOOLWINDOW;
        longExStyle &= ~WindowStyles.WS_EX_APPWINDOW;
        _ = NativeMethods.SetWindowLongPtr(_childHwnd, WindowStyles.GWL_EXSTYLE, (nint)longExStyle);

        // Step 3 — make the WinUI shell the OWNER of PuTTY's top-level window.
        // Owner relationship (GWL_HWNDPARENT on a non-WS_CHILD window) means
        // PuTTY minimizes / restores / closes with us, but stays a sibling in
        // the desktop z-order so DWM composes it normally — i.e. visible.
        _ = NativeMethods.SetWindowLongPtr(_childHwnd, WindowStyles.GWL_HWNDPARENT, _hostWindowHandle);

        _ = NativeMethods.ShowWindow(_childHwnd, WindowStyles.SW_SHOW);
        _ = NativeMethods.SetWindowPos(
            _childHwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        _isAttached = true;

        // Wire EVENT_OBJECT_NAMECHANGE so the WinUI tab caption tracks the
        // PuTTY window title.
        if (_process != null)
        {
            _winEventDelegate = OnWinEvent;
            _winEventHook = WindowEvents.SetWinEventHook(
                WindowEvents.EVENT_OBJECT_NAMECHANGE,
                WindowEvents.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero,
                _winEventDelegate,
                (uint)_process.Id,
                0,
                WindowEvents.WINEVENT_OUTOFCONTEXT);
            UpdateTitle();
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isClosing) return;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        nint hwnd = _childHwnd;
        if (hwnd == 0) return;

        string newTitle = WindowEvents.GetTitle(hwnd);
        if (string.IsNullOrEmpty(newTitle)) return;

        lock (_titleSync)
        {
            if (_currentTitle == newTitle) return;
            _currentTitle = newTitle;
        }

        DispatcherQueue.TryEnqueue(() => TitleChanged?.Invoke(this, newTitle));
    }

    private void OnSizeOrLayoutChanged(object? sender, object? e)
    {
        if (!_isAttached || _childHwnd == 0)
        {
            // Only log "skipped" the first time per state to avoid log spam.
            return;
        }
        if (XamlRoot is null)
        {
            DebugLog.Write("EmbeddedPuttyHost", "OnSizeOrLayoutChanged: XamlRoot null");
            return;
        }

        // Compute the host's screen-space rectangle.
        // 1. TransformToVisual(XamlRoot.Content) — explicit target = window
        //    visual root. Passing null is documented as "window content" but
        //    inside a TabViewItem content presenter that returned identity
        //    (0,0). The explicit XamlRoot.Content target is reliable.
        // 2. Use this UserControl's bounds, not just the inner Border, so we
        //    get a non-zero size as soon as the tab is laid out.
        // 3. Multiply by RasterizationScale → pixels.
        // 4. ClientToScreen on the host HWND → screen pixels for MoveWindow.
        UIElement? rootVisual = XamlRoot.Content;
        if (rootVisual is null) return;

        GeneralTransform transform = TransformToVisual(rootVisual);
        Point origin = transform.TransformPoint(new Point(0, 0));
        double scale = XamlRoot.RasterizationScale;

        double widthDip = ActualWidth > 0 ? ActualWidth : HostPlaceholder.ActualWidth;
        double heightDip = ActualHeight > 0 ? ActualHeight : HostPlaceholder.ActualHeight;

        int xClient = (int)Math.Round(origin.X * scale);
        int yClient = (int)Math.Round(origin.Y * scale);
        int w = (int)Math.Round(widthDip * scale);
        int h = (int)Math.Round(heightDip * scale);
        if (w <= 0 || h <= 0) return;

        NativeMethods.POINT screen = new() { X = xClient, Y = yClient };
        bool clientOk = NativeMethods.ClientToScreen(_hostWindowHandle, ref screen);

        // Diagnostic overlay: show every value that goes into MoveWindow.
        // Removed once the embed positioning is stable.
        if (!clientOk)
        {
            DebugLog.Write("EmbeddedPuttyHost", "ClientToScreen failed");
            return;
        }

        bool moved = NativeMethods.MoveWindow(_childHwnd, screen.X, screen.Y, w, h, repaint: true);
        DebugLog.Write("EmbeddedPuttyHost",
            $"MoveWindow childHwnd=0x{_childHwnd:X} hostHwnd=0x{_hostWindowHandle:X} " +
            $"originDip=({origin.X:F0},{origin.Y:F0}) scale={scale:F2} " +
            $"clientPx=({xClient},{yClient}) size=({w}x{h}) screen=({screen.X},{screen.Y}) " +
            $"actualHost=({ActualWidth:F0},{ActualHeight:F0}) " +
            $"actualPh=({HostPlaceholder.ActualWidth:F0},{HostPlaceholder.ActualHeight:F0}) " +
            $"returned={moved}");

        _ = NativeMethods.InvalidateRect(_childHwnd, IntPtr.Zero, true);
    }

    public async Task CloseAsync()
    {
        if (_process == null || _isClosing) return;

        _isClosing = true;

        if (_winEventHook != 0)
        {
            _ = WindowEvents.UnhookWinEvent(_winEventHook);
            _winEventHook = 0;
        }

        if (_childHwnd != 0)
        {
            _ = NativeMethods.PostMessage(_childHwnd, WindowStyles.WM_CLOSE, 0, 0);
        }

        try
        {
            if (!_process.HasExited)
            {
                using CancellationTokenSource cts = new(GracefulCloseTimeout);
                try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(true); }
                catch (OperationCanceledException) { /* fall through to Kill */ }
            }
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        finally
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
            _childHwnd = 0;
            _winEventDelegate = null;

            // Unwire layout / visibility handlers so a closed host doesn't
            // keep responding to shell events.
            SizeChanged -= OnSizeOrLayoutChanged;
            HostPlaceholder.SizeChanged -= OnSizeOrLayoutChanged;
            HostPlaceholder.LayoutUpdated -= OnSizeOrLayoutChanged;
            Loaded -= OnTabBecameVisible;
            Unloaded -= OnTabBecameHidden;
        }
    }
}
