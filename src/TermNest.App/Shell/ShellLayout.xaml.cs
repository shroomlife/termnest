using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TermNest.App.Interop;
using TermNest.App.ViewModels;
using TermNest.Core.Layouts;
using TermNest.Core.Sessions;
using Windows.Storage;

namespace TermNest.App.Shell;

/// <summary>
/// The single fixed shell that hosts the connection bar, session tree, tab
/// area and an optional bottom status strip. Layout dimensions and the
/// open-session list live in <see cref="LayoutData"/> JSON files; ContentSizer
/// (CommunityToolkit.WinUI) gives users splitter-based resizing without
/// pulling in a full docking library — Phase 4 trade-off, detachable docks
/// land in 4.1.
/// </summary>
public sealed partial class ShellLayout : UserControl
{
    public event EventHandler<WindowPlacement>? LayoutWindowPlacementLoaded;

    private const string TerminalFontSizeSettingKey = "TerminalFontSize";
    private const string PuttyExePathSettingKey = "PuttyExePath";
    private const string WinScpExePathSettingKey = "WinScpExePath";
    private const double DefaultTerminalFontSize = 15;
    private const double MinTerminalFontSize = 10;
    private const double MaxTerminalFontSize = 28;

    public ShellViewModel ViewModel { get; } = new();

    private LayoutStore? _layoutStore;
    private SessionStore? _sessionStore;
    private LayoutData _activeLayout = new() { Name = "default", IsDefault = true };
    private bool _settingsLoaded;
    private bool _isDraggingSideRail;
    private double _sideRailDragStartWidth;
    private double _sideRailDragStartX;
    private uint _sideRailDragPointerId;
    private const double MinSideRailWidth = 200;
    private const double MaxSideRailWidth = 800;

    public ShellLayout()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // Permanent column-resize cursor on the splitter handle. Setting it
        // once here avoids per-pointer toggling and keeps the cursor stable
        // mid-drag even if the pointer briefly leaves the 6px hit area.
        SideRailSplitter.HandleCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await BootstrapAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // OnLoaded is async void; surface any post-await failure in the
            // status bar so it isn't silently dropped into the unhandled
            // exception sink.
            ViewModel.StatusMessage = $"Startup error: {ex.Message}";
        }
    }

    private async Task BootstrapAsync()
    {
        string localState = ResolveLocalStateDirectory();
        ViewModel.TerminalFontSize = LoadTerminalFontSizeSetting();
        ViewModel.PuttyExePath = LoadStringSetting(PuttyExePathSettingKey, ViewModel.PuttyExePath);
        ViewModel.WinScpExePath = LoadStringSetting(WinScpExePathSettingKey, ViewModel.WinScpExePath);
        SessionTabs.TerminalFontSize = ViewModel.TerminalFontSize;
        SessionsPanelControl.WinScpExePath = ViewModel.WinScpExePath;
        _settingsLoaded = true;

        SessionsPanelControl.LocalStateDirectory = localState;
        _sessionStore = new SessionStore(localState);
        _layoutStore = new LayoutStore(localState);

        await LoadActiveLayoutAsync().ConfigureAwait(true);
    }

    private static string ResolveLocalStateDirectory()
    {
        try { return ApplicationData.Current.LocalFolder.Path; }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TermNest");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private async Task LoadActiveLayoutAsync()
    {
        if (_layoutStore == null) return;

        string? activeName = _layoutStore.GetActiveLayoutName();
        if (!string.IsNullOrEmpty(activeName))
        {
            LayoutData? loaded = await _layoutStore.LoadAsync(activeName).ConfigureAwait(true);
            if (loaded != null)
            {
                _activeLayout = loaded;
            }
        }

        ApplyLayoutToUi();

        // Do not auto-open remote sessions on launch. A saved SSH tab can
        // immediately create network connections and host-key/password prompts
        // before the user has asked for anything, which is jarring while the
        // terminal integration is still evolving.
        if (_activeLayout.OpenSessionIds.Count > 0)
        {
            ViewModel.StatusMessage = "Previous session tabs were not auto-restored.";
            _activeLayout.OpenSessionIds.Clear();
        }
    }

    private void ApplyLayoutToUi()
    {
        SideRailColumn.Width = new GridLength(_activeLayout.SideRailWidth);
        BottomStripRow.Height = GridLength.Auto;
        BottomStrip.Visibility = Visibility.Visible;
        SessionsPanelControl.ExpandedFolderPaths = _activeLayout.ExpandedSessionFolderPaths;
        ViewModel.LayoutDisplayName = $"Layout: {_activeLayout.Name}";
        LayoutWindowPlacementLoaded?.Invoke(this, _activeLayout.WindowPlacement);
    }

    public async Task SaveCurrentLayoutAsync(WindowPlacement? windowPlacement = null)
    {
        if (_layoutStore == null) return;

        // Capture UI-thread state synchronously *before* the first await — by
        // the time the continuation resumes the XAML tree may have been torn
        // down (window already destroying its compositor) and reading
        // SideRailColumn.Width or TabItems would throw ObjectDisposedException.
        double railWidth = SideRailColumn.Width.Value;

        _activeLayout.SideRailWidth = railWidth;
        if (windowPlacement != null)
        {
            _activeLayout.WindowPlacement = windowPlacement;
        }
        _activeLayout.ExpandedSessionFolderPaths = SessionsPanelControl.GetExpandedFolderPaths();
        _activeLayout.OpenSessionIds.Clear();

        await _layoutStore.SaveAsync(_activeLayout).ConfigureAwait(true);
        _layoutStore.SetActiveLayoutName(_activeLayout.Name);
    }

    private async Task SafeOpenAsync(SessionData session)
    {
        try
        {
            TermNest.Core.Diagnostics.DebugLog.Write("Shell", $"SafeOpenAsync invoked sessionId={session.SessionId}");
            await SessionTabs.OpenSessionAsync(session, ViewModel.PuttyExePath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TermNest.Core.Diagnostics.DebugLog.Write("Shell", $"SafeOpenAsync caught {ex.GetType().Name}: {ex}");
            ViewModel.StatusMessage = $"Failed to open {session.SessionName}: {ex.Message}";
        }
    }

    private async void OnSessionInvoked(object sender, SessionData session)
    {
        TermNest.Core.Diagnostics.DebugLog.Write("Shell", $"OnSessionInvoked received {session.SessionId}");
        await SafeOpenAsync(session).ConfigureAwait(true);
    }

    private async void OnConnectRequested(object sender, SessionData session)
    {
        await SafeOpenAsync(session).ConfigureAwait(true);
    }

    private void OnTabsStatusMessage(object? sender, string message)
    {
        ViewModel.StatusMessage = message;
    }

    private void OnTerminalFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double fontSize = NormalizeTerminalFontSize(args.NewValue);
        if (Math.Abs(fontSize - args.NewValue) > 0.01)
        {
            sender.Value = fontSize;
            return;
        }

        ViewModel.TerminalFontSize = fontSize;
        SessionTabs.TerminalFontSize = fontSize;

        if (_settingsLoaded)
        {
            SaveTerminalFontSizeSetting(fontSize);
            ViewModel.StatusMessage = $"Terminal font size: {fontSize:0}px";
        }
    }

    private void OnWinScpPathChanged(object sender, TextChangedEventArgs e)
    {
        string path = sender is TextBox textBox ? textBox.Text : ViewModel.WinScpExePath;
        SessionsPanelControl.WinScpExePath = path;
        if (_settingsLoaded)
        {
            SaveStringSetting(WinScpExePathSettingKey, path);
        }
    }

    private void OnPuttyPathChanged(object sender, TextChangedEventArgs e)
    {
        string path = sender is TextBox textBox ? textBox.Text : ViewModel.PuttyExePath;
        if (_settingsLoaded)
        {
            SaveStringSetting(PuttyExePathSettingKey, path);
        }
    }

    private async void OnBrowsePuttyPathClick(object sender, RoutedEventArgs e)
    {
        string? path = await PickExecutablePathAsync(
            "Select putty.exe",
            ViewModel.PuttyExePath,
            "putty.exe").ConfigureAwait(true);
        if (path == null)
        {
            return;
        }

        ViewModel.PuttyExePath = path;
        PuttyPathBox.Text = path;
        SaveStringSetting(PuttyExePathSettingKey, path);
        ViewModel.StatusMessage = $"PuTTY executable: {path}";
    }

    private async void OnBrowseWinScpPathClick(object sender, RoutedEventArgs e)
    {
        string? path = await PickExecutablePathAsync(
            "Select winscp.exe",
            ViewModel.WinScpExePath,
            "WinSCP.exe").ConfigureAwait(true);
        if (path == null)
        {
            return;
        }

        ViewModel.WinScpExePath = path;
        WinScpPathBox.Text = path;
        SessionsPanelControl.WinScpExePath = path;
        SaveStringSetting(WinScpExePathSettingKey, path);
        ViewModel.StatusMessage = $"WinSCP executable: {path}";
    }

    private Task<string?> PickExecutablePathAsync(string title, string currentPath, string fallbackFileName)
    {
        try
        {
            string? initialFolder = ResolveInitialExecutableFolder(currentPath);
            string suggestedFileName = ResolveSuggestedExecutableName(currentPath, fallbackFileName);
            string? path = NativeFileDialog.PickExecutable(App.WindowHandle, title, initialFolder, suggestedFileName);
            return Task.FromResult(path);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"File picker failed: {ex.Message}";
            return Task.FromResult<string?>(null);
        }
    }

    private static string? ResolveInitialExecutableFolder(string? currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string trimmed = currentPath.Trim().Trim('"');
            if (Directory.Exists(trimmed))
            {
                return trimmed;
            }

            string? directory = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }

        string[] fallbackFolders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return fallbackFolders.FirstOrDefault(Directory.Exists);
    }

    private static string ResolveSuggestedExecutableName(string? currentPath, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string fileName = Path.GetFileName(currentPath.Trim().Trim('"'));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return fallbackFileName;
    }

    private static double LoadTerminalFontSizeSetting()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(TerminalFontSizeSettingKey, out object? value))
            {
                return value switch
                {
                    double d => NormalizeTerminalFontSize(d),
                    int i => NormalizeTerminalFontSize(i),
                    string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                        => NormalizeTerminalFontSize(d),
                    _ => DefaultTerminalFontSize,
                };
            }
        }
        catch
        {
            // Unpackaged/dev fallback: keep the default for this session.
        }

        return DefaultTerminalFontSize;
    }

    private static void SaveTerminalFontSizeSetting(double fontSize)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[TerminalFontSizeSettingKey] = NormalizeTerminalFontSize(fontSize);
        }
        catch
        {
            // Settings persistence is best-effort in unpackaged/dev runs.
        }
    }

    private static string LoadStringSetting(string key, string fallback)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) &&
                value is string { Length: > 0 } text
                    ? text
                    : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SaveStringSetting(string key, string value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // Settings persistence is best-effort in unpackaged/dev runs.
        }
    }

    private static double NormalizeTerminalFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize))
        {
            return DefaultTerminalFontSize;
        }

        return Math.Clamp(Math.Round(fontSize), MinTerminalFontSize, MaxTerminalFontSize);
    }

    private void OnSideRailSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement ui) return;

        _sideRailDragStartX = e.GetCurrentPoint(this).Position.X;
        _sideRailDragStartWidth = SideRailColumn.ActualWidth > 0
            ? SideRailColumn.ActualWidth
            : SideRailColumn.Width.Value;
        _sideRailDragPointerId = e.Pointer.PointerId;
        _isDraggingSideRail = ui.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSideRailSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSideRail || e.Pointer.PointerId != _sideRailDragPointerId) return;

        double deltaX = e.GetCurrentPoint(this).Position.X - _sideRailDragStartX;
        double next = Math.Clamp(_sideRailDragStartWidth + deltaX, MinSideRailWidth, MaxSideRailWidth);
        SideRailColumn.Width = new GridLength(next);
        e.Handled = true;
    }

    private async void OnSideRailSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSideRail) return;

        if (sender is UIElement ui)
        {
            ui.ReleasePointerCapture(e.Pointer);
        }
        _isDraggingSideRail = false;
        e.Handled = true;

        // Persist the new width immediately so a crash before window-close
        // doesn't lose the user's resize.
        if (_layoutStore != null)
        {
            _activeLayout.SideRailWidth = SideRailColumn.Width.Value;
            try
            {
                await _layoutStore.SaveAsync(_activeLayout).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Could not save layout: {ex.Message}";
            }
        }
    }

    public Task CloseAllSessionsAsync() => SessionTabs.CloseAllAsync();

    /// <summary>
    /// Forwards a "shell window moved/resized" tick to every open
    /// EmbeddedPuttyHost so the owned top-level PuTTY windows follow.
    /// </summary>
    public void RefreshEmbeddedHostPositions() => SessionTabs.RefreshEmbeddedHostPositions();
}
