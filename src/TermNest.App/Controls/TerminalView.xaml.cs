using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TermNest.Core.Diagnostics;
using TermNest.Core.Sessions;
using Windows.ApplicationModel;

namespace TermNest.App.Controls;

/// <summary>
/// Native WinUI 3 terminal: WebView2 hosts the terminal surface while ConPTY
/// backs local shells and OpenSSH. Because WebView2 is a real XAML control
/// there are no foreign-HWND compositor fights.
/// </summary>
public sealed partial class TerminalView : UserControl
{
    public event EventHandler<bool>? Exited;
    public event EventHandler<string>? TitleChanged;

    private const double DefaultTerminalFontSize = 15;
    private SshTerminalSession? _ssh;
    private ConsoleTerminalSession? _console;
    private bool _webViewReady;
    private SessionData? _pendingSession;
    private TerminalBackend _pendingBackend = TerminalBackend.None;
    private TaskCompletionSource? _connectTcs;
    private bool _startInProgress;
    private bool _hasRenderedOutput;
    private int _lastCols = 80;
    private int _lastRows = 24;
    private double _terminalFontSize = DefaultTerminalFontSize;
    private static readonly TimeSpan TerminalReadyTimeout = TimeSpan.FromSeconds(15);

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public double TerminalFontSize
    {
        get => _terminalFontSize;
        set
        {
            double next = NormalizeTerminalFontSize(value);
            if (Math.Abs(_terminalFontSize - next) < 0.01)
            {
                return;
            }

            _terminalFontSize = next;
            if (_webViewReady)
            {
                _ = ApplyTerminalFontSizeAsync();
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            DebugLog.Write("TerminalView", "WebView2 ready");

            string terminalHostPath = ResolveTerminalHostPath();
            WebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    DebugLog.Write("TerminalView", $"Terminal page navigation failed: {args.WebErrorStatus}");
                }
            };

            // Navigate to the packaged file so the local xterm.js/css assets
            // resolve relative to index.html without network or CDN access.
            WebView.CoreWebView2.Navigate(new Uri(terminalHostPath).AbsoluteUri);
            DebugLog.Write("TerminalView", $"Terminal page navigating: {terminalHostPath}");

            // The page posts a {type:"ready"} message from local script; that's
            // when we kick off the terminal process if a session is queued.
        }
        catch (Exception ex)
        {
            DebugLog.Write("TerminalView", $"WebView2 init failed: {ex.Message}");
            ShowStatus("Terminal init failed", ex.Message, spinner: false);
            _connectTcs?.TrySetException(ex);
        }
    }

    private static string ResolveTerminalHostPath()
    {
        string? path = ResolveAssetPath(Path.Combine("Terminal", "index.html"));
        if (path == null)
        {
            throw new FileNotFoundException("Terminal host page was not found in packaged assets.");
        }
        return path;
    }

    private static string? ResolveAssetPath(string relativeAssetPath)
    {
        string appBasePath = Path.Combine(AppContext.BaseDirectory, "Assets", relativeAssetPath);
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        try
        {
            string packagePath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", relativeAssetPath);
            return File.Exists(packagePath) ? packagePath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Begins an SSH connection. Caller is responsible for ensuring the
    /// session has a password (TerminalView is a XAML control whose own
    /// XamlRoot may not yet be valid — password prompts must happen at the
    /// SessionTabsControl level where XamlRoot is guaranteed).
    /// </summary>
    public Task ConnectAsync(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        DebugLog.Write("TerminalView", $"ConnectAsync called, host={session.Host} hasPassword={!string.IsNullOrEmpty(session.Password)} webViewReady={_webViewReady}");

        ShowStatus("Connecting", $"to {session.Username}@{session.Host}:{session.Port}", spinner: true);
        return QueueStartAsync(TerminalBackend.Ssh, session);
    }

    /// <summary>
    /// Starts a local shell or OpenSSH session through ConPTY.
    /// </summary>
    public Task StartConsoleAsync(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        DebugLog.Write("TerminalView", $"StartConsoleAsync called, protocol={session.Protocol} webViewReady={_webViewReady}");

        string shellName = session.Protocol switch
        {
            ConnectionProtocol.SSH or ConnectionProtocol.SSH2 => $"SSH {FormatSshTitle(session)}",
            ConnectionProtocol.PS => "PowerShell",
            _ => "Command Prompt",
        };
        ShowStatus("Starting", shellName, spinner: true);
        return QueueStartAsync(TerminalBackend.Console, session);
    }

    private Task QueueStartAsync(TerminalBackend backend, SessionData session)
    {
        if (_connectTcs != null)
        {
            throw new InvalidOperationException("Terminal session already starting.");
        }

        _pendingBackend = backend;
        _pendingSession = session;
        _connectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_webViewReady)
        {
            _ = StartPendingAsync();
        }
        else
        {
            _ = FailIfTerminalPageDoesNotBecomeReadyAsync(_connectTcs);
        }
        return _connectTcs.Task;
    }

    private async Task FailIfTerminalPageDoesNotBecomeReadyAsync(TaskCompletionSource connectTcs)
    {
        await Task.Delay(TerminalReadyTimeout).ConfigureAwait(true);
        if (!_webViewReady && ReferenceEquals(_connectTcs, connectTcs))
        {
            TimeoutException ex = new("Terminal page did not become ready. Check WebView2 and bundled terminal assets.");
            DebugLog.Write("TerminalView", ex.Message);
            ShowStatus("Terminal unavailable", ex.Message, spinner: false);
            connectTcs.TrySetException(ex);
        }
    }

    private async Task StartPendingAsync()
    {
        if (_startInProgress || _pendingSession == null || _pendingBackend == TerminalBackend.None)
        {
            DebugLog.Write("TerminalView", $"StartPendingAsync skipped inProgress={_startInProgress} hasSession={_pendingSession != null} backend={_pendingBackend}");
            return;
        }

        _startInProgress = true;
        SessionData session = _pendingSession;
        TerminalBackend backend = _pendingBackend;
        DebugLog.Write("TerminalView", $"StartPendingAsync backend={backend} sessionId={session.SessionId}");

        try
        {
            switch (backend)
            {
                case TerminalBackend.Ssh:
                    await StartSshAsync(session).ConfigureAwait(true);
                    break;
                case TerminalBackend.Console:
                    await StartConsoleAsyncCore(session).ConfigureAwait(true);
                    break;
            }

            await ExecuteJsAsync("host_focus()").ConfigureAwait(true);
            _connectTcs?.TrySetResult();
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex)
        {
            DebugLog.Write("TerminalView", $"SSH auth failed: {ex.Message}");
            ShowStatus("Authentication failed", ex.Message, spinner: false);
            _connectTcs?.TrySetException(ex);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            DebugLog.Write("TerminalView", $"Network error: {ex.Message}");
            ShowStatus("Network error", ex.Message, spinner: false);
            _connectTcs?.TrySetException(ex);
        }
        catch (Exception ex)
        {
            DebugLog.Write("TerminalView", $"Terminal start failed: {ex.GetType().Name} {ex.Message}");
            ShowStatus("Start failed", ex.Message, spinner: false);
            _connectTcs?.TrySetException(ex);
        }
    }

    private async Task StartSshAsync(SessionData session)
    {
        _ssh = new SshTerminalSession(session);
        _ssh.DataReceived += OnSshDataReceived;
        _ssh.Closed += OnSshClosed;
        _ssh.Error += (_, msg) => DispatcherQueue.TryEnqueue(() => ShowStatus("Error", msg, spinner: false));

        await _ssh.ConnectAsync(_lastCols, _lastRows).ConfigureAwait(true);
        TitleChanged?.Invoke(this, $"{session.Username}@{session.Host}");
        DebugLog.Write("TerminalView", $"Connected: {session.Host}:{session.Port}");
    }

    private async Task StartConsoleAsyncCore(SessionData session)
    {
        _console = new ConsoleTerminalSession(session);
        _console.DataReceived += OnConsoleDataReceived;
        _console.Closed += OnConsoleClosed;
        _console.Error += (_, msg) => DispatcherQueue.TryEnqueue(() => ShowStatus("Error", msg, spinner: false));

        await _console.ConnectAsync(_lastCols, _lastRows).ConfigureAwait(true);
        TitleChanged?.Invoke(this, session.Protocol is ConnectionProtocol.SSH or ConnectionProtocol.SSH2
            ? FormatSshTitle(session)
            : session.Protocol == ConnectionProtocol.PS ? "PowerShell" : "Command Prompt");
        DebugLog.Write("TerminalView", $"ConPTY terminal started: {session.Protocol}");
    }

    private static string FormatSshTitle(SessionData session)
    {
        if (string.IsNullOrWhiteSpace(session.Username))
        {
            return session.Host;
        }
        return $"{session.Username}@{session.Host}";
    }

    private void OnSshDataReceived(object? sender, string text)
        => WriteTerminalText(text);

    private void OnConsoleDataReceived(object? sender, string text)
        => WriteTerminalText(text);

    private void WriteTerminalText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Marshal backend output onto the UI thread and feed it into the
        // terminal page via host_write.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ExecuteJsAsync($"host_write({JsonSerializer.Serialize(text)})");
            }
            catch (Exception ex) { DebugLog.Write("TerminalView", $"host_write failed: {ex.Message}"); }
        });
    }

    private void OnSshClosed(object? sender, bool unexpected)
        => OnTerminalClosed(unexpected);

    private void OnConsoleClosed(object? sender, bool unexpected)
        => OnTerminalClosed(unexpected);

    private void OnTerminalClosed(bool unexpected)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DebugLog.Write("TerminalView", $"Terminal closed unexpected={unexpected}");
            ShowStatus(
                unexpected ? "Connection lost" : "Session closed",
                unexpected ? "The remote disconnected unexpectedly." : "Disconnected.",
                spinner: false);
            Exited?.Invoke(this, unexpected);
        });
    }

    private void OnWebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        // The xterm.js page posts JSON: {type:"ready"|"data"|"resize", ...}
        try
        {
            string raw = args.WebMessageAsJson;
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("type", out JsonElement typeEl)) return;
            string? type = typeEl.GetString();

            switch (type)
            {
                case "ready":
                    _webViewReady = true;
                    DebugLog.Write("TerminalView", $"terminal page ready backend={_pendingBackend} hasPending={_pendingSession != null}");
                    _ = ApplyTerminalFontSizeAsync();
                    if (_pendingSession != null)
                    {
                        _ = StartPendingAsync();
                    }
                    break;

                case "data":
                    if (doc.RootElement.TryGetProperty("data", out JsonElement dataEl) &&
                        dataEl.GetString() is { } data)
                    {
                        if (_ssh is { IsConnected: true })
                        {
                            _ = _ssh.WriteAsync(data);
                        }
                        else if (_console is { IsRunning: true })
                        {
                            _ = _console.WriteAsync(data);
                        }
                    }
                    break;

                case "resize":
                    if (doc.RootElement.TryGetProperty("cols", out JsonElement colsEl) &&
                        doc.RootElement.TryGetProperty("rows", out JsonElement rowsEl))
                    {
                        _lastCols = colsEl.GetInt32();
                        _lastRows = rowsEl.GetInt32();
                        _ssh?.Resize(_lastCols, _lastRows);
                        _console?.Resize(_lastCols, _lastRows);
                    }
                    break;

                case "painted":
                    if (!_hasRenderedOutput)
                    {
                        _hasRenderedOutput = true;
                        HideStatus();
                        DebugLog.Write("TerminalView", "First visible terminal frame rendered");
                    }
                    break;

                case "title":
                    if (doc.RootElement.TryGetProperty("title", out JsonElement titleEl) &&
                        titleEl.GetString() is { Length: > 0 } title &&
                        !title.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase) &&
                        !title.EndsWith("ssh.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        TitleChanged?.Invoke(this, title);
                    }
                    break;

                case "log":
                    if (doc.RootElement.TryGetProperty("message", out JsonElement messageEl) &&
                        messageEl.GetString() is { Length: > 0 } message)
                    {
                        DebugLog.Write("TerminalPage", message);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("TerminalView", $"OnWebMessageReceived failed: {ex.Message}");
        }
    }

    private async Task ExecuteJsAsync(string js)
    {
        if (WebView.CoreWebView2 == null) return;
        await WebView.CoreWebView2.ExecuteScriptAsync(js);
    }

    private async Task ApplyTerminalFontSizeAsync()
    {
        try
        {
            await ExecuteJsAsync($"host_setFontSize({JsonSerializer.Serialize(_terminalFontSize)})");
        }
        catch (Exception ex)
        {
            DebugLog.Write("TerminalView", $"host_setFontSize failed: {ex.Message}");
        }
    }

    private static double NormalizeTerminalFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize))
        {
            return DefaultTerminalFontSize;
        }

        return Math.Clamp(Math.Round(fontSize), 10, 28);
    }

    private void ShowStatus(string title, string detail, bool spinner)
    {
        StatusOverlay.Visibility = Visibility.Visible;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusRing.IsActive = spinner;
        StatusRing.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideStatus() => StatusOverlay.Visibility = Visibility.Collapsed;

    public async Task CloseAsync()
    {
        if (_ssh != null)
        {
            await _ssh.DisposeAsync().ConfigureAwait(true);
            _ssh = null;
        }
        if (_console != null)
        {
            await _console.DisposeAsync().ConfigureAwait(true);
            _console = null;
        }
    }

    private enum TerminalBackend
    {
        None,
        Ssh,
        Console,
    }
}
