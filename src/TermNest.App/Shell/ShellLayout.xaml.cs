using CommunityToolkit.WinUI.Controls;
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
/// The single fixed shell that hosts the session tree, tab area and a status
/// strip at the bottom. The bottom strip carries every transient status
/// message (clipboard copies, "opening …", connection results) and a settings
/// button that opens an Edit-Session-style dialog for app-wide preferences.
/// Layout dimensions live in <see cref="LayoutData"/> JSON.
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
    private static readonly TimeSpan StatusAutoClearDelay = TimeSpan.FromSeconds(5);

    public ShellViewModel ViewModel { get; } = new();

    private LayoutStore? _layoutStore;
    private KnownHostsStore? _knownHostsStore;
    private SessionStore? _sessionStore;
    private LayoutData _activeLayout = new() { Name = "default", IsDefault = true };
    private bool _settingsLoaded;
    private bool _isDraggingSideRail;
    private double _sideRailDragStartWidth;
    private double _sideRailDragStartX;
    private uint _sideRailDragPointerId;
    private DispatcherTimer? _statusClearTimer;
    private bool _isSettingsDialogOpen;
    private const double MinSideRailWidth = 200;
    private const double MaxSideRailWidth = 800;

    public ShellLayout()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Permanent column-resize cursor on the splitter handle. Setting it
        // once here avoids per-pointer toggling and keeps the cursor stable
        // mid-drag even if the pointer briefly leaves the 6px hit area.
        SideRailSplitter.HandleCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop the status auto-clear timer before the dispatcher tears down.
        // A late tick on a destroyed XAML tree wouldn't crash (StatusMessage
        // is just a property), but stopping it eagerly is the conservative
        // shutdown order.
        _statusClearTimer?.Stop();
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
            SetStatus($"Startup error: {ex.Message}");
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
        _knownHostsStore = new KnownHostsStore(localState);

        // Wire host-key verification: SshTerminalSession will call back into
        // PromptForHostKeyAsync on first connect, marshalled to the UI thread.
        SessionTabs.HostKeyStore = _knownHostsStore;
        SessionTabs.HostKeyPrompt = PromptForHostKeyAsync;

        await LoadActiveLayoutAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Asks the user whether to trust an unknown SSH host key. Invoked from a
    /// background SSH.NET thread, so it marshals to the UI thread for the
    /// dialog and then back. The fingerprint is shown in the SHA-256 base64
    /// form OpenSSH uses, prefixed with <c>SHA256:</c> so users can compare
    /// against <c>ssh-keygen -lf</c> output.
    /// </summary>
    private Task<bool> PromptForHostKeyAsync(HostKeyPrompt prompt)
    {
        TaskCompletionSource<bool> tcs = new();

        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                StackPanel panel = new() { Spacing = 10 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"You are connecting to {prompt.Host}:{prompt.Port} for the first time.",
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "Verify the fingerprint below matches the one your administrator gave you (or what `ssh-keygen -lf` reports on the server).",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });

                Border fingerprintBox = new()
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerOnAcrylicFillColorDefaultBrush"],
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                };
                StackPanel fpStack = new() { Spacing = 4 };
                fpStack.Children.Add(new TextBlock
                {
                    Text = $"Algorithm: {prompt.Algorithm}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
                fpStack.Children.Add(new TextBlock
                {
                    Text = $"SHA256:{prompt.FingerprintSha256}",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                });
                fingerprintBox.Child = fpStack;
                panel.Children.Add(fingerprintBox);

                ContentDialog dialog = new()
                {
                    Title = "Trust this host key?",
                    Content = panel,
                    PrimaryButtonText = "Trust and connect",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                ContentDialogResult result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                TermNest.Core.Diagnostics.DebugLog.Write("Shell", $"Host key prompt failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        });

        if (!queued)
        {
            tcs.TrySetResult(false);
        }
        return tcs.Task;
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
            SetStatus("Previous session tabs were not auto-restored.");
            _activeLayout.OpenSessionIds.Clear();
        }
    }

    private void ApplyLayoutToUi()
    {
        SideRailColumn.Width = new GridLength(_activeLayout.SideRailWidth);
        BottomStripRow.Height = GridLength.Auto;
        BottomStrip.Visibility = Visibility.Visible;
        SessionsPanelControl.ExpandedFolderPaths = _activeLayout.ExpandedSessionFolderPaths;
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
            SetStatus($"Failed to open {session.SessionName}: {ex.Message}");
        }
    }

    private async void OnSessionInvoked(object sender, SessionData session)
    {
        TermNest.Core.Diagnostics.DebugLog.Write("Shell", $"OnSessionInvoked received {session.SessionId}");
        await SafeOpenAsync(session).ConfigureAwait(true);
    }

    private void OnTabsStatusMessage(object? sender, string message) => SetStatus(message);

    private void OnSessionsPanelStatusMessage(object? sender, string message) => SetStatus(message);

    /// <summary>
    /// Single sink for every transient footer message — clipboard copies,
    /// session opens, save/load results, errors. Restarts an auto-clear
    /// timer so a quiet UI eventually drops back to an empty status.
    /// </summary>
    private void SetStatus(string text)
    {
        ViewModel.StatusMessage = text ?? string.Empty;

        if (_statusClearTimer == null)
        {
            _statusClearTimer = new DispatcherTimer { Interval = StatusAutoClearDelay };
            _statusClearTimer.Tick += (_, _) =>
            {
                _statusClearTimer!.Stop();
                ViewModel.StatusMessage = string.Empty;
            };
        }

        _statusClearTimer.Stop();
        if (!string.IsNullOrEmpty(text))
        {
            _statusClearTimer.Start();
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_isSettingsDialogOpen) return;

        _isSettingsDialogOpen = true;
        try
        {
            await ShowSettingsDialogAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus($"Settings failed: {ex.Message}");
        }
        finally
        {
            _isSettingsDialogOpen = false;
        }
    }

    private const double SettingsDialogWidth = 640;

    private async Task ShowSettingsDialogAsync()
    {
        TextBox puttyBox = new()
        {
            Text = ViewModel.PuttyExePath,
            PlaceholderText = @"C:\Program Files\PuTTY\putty.exe",
            MinWidth = 320,
        };
        TextBox winScpBox = new()
        {
            Text = ViewModel.WinScpExePath,
            PlaceholderText = @"C:\Program Files (x86)\WinSCP\WinSCP.exe",
            MinWidth = 320,
        };
        NumberBox fontSizeBox = new()
        {
            Value = ViewModel.TerminalFontSize,
            Minimum = MinTerminalFontSize,
            Maximum = MaxTerminalFontSize,
            SmallChange = 1,
            LargeChange = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 160,
        };

        Button browsePutty = CreateBrowseButton();
        browsePutty.Click += async (_, _) =>
        {
            string? picked = await PickExecutablePathAsync(
                "Select putty.exe", puttyBox.Text, "putty.exe").ConfigureAwait(true);
            if (picked != null) puttyBox.Text = picked;
        };
        Button browseWinScp = CreateBrowseButton();
        browseWinScp.Click += async (_, _) =>
        {
            string? picked = await PickExecutablePathAsync(
                "Select winscp.exe", winScpBox.Text, "WinSCP.exe").ConfigureAwait(true);
            if (picked != null) winScpBox.Text = picked;
        };

        StackPanel content = new()
        {
            Spacing = 28,
            Width = SettingsDialogWidth - 48,
        };

        content.Children.Add(BuildSettingsSectionHeader("External tools"));
        content.Children.Add(BuildPathSettingsCard(
            "PuTTY executable",
            "Used for any session whose protocol still routes through PuTTY (Telnet, RDP, VNC, …).",
            puttyBox,
            browsePutty));
        content.Children.Add(BuildPathSettingsCard(
            "WinSCP executable",
            "Launched from the WinSCP action on each session row.",
            winScpBox,
            browseWinScp));

        content.Children.Add(BuildSettingsSectionHeader("Terminal"));
        content.Children.Add(new SettingsCard
        {
            Header = "Font size",
            Description = "Applied immediately to open tabs and to every newly opened terminal.",
            Content = fontSizeBox,
            ContentAlignment = ContentAlignment.Right,
        });

        ContentDialog dialog = new()
        {
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        string newPutty = puttyBox.Text?.Trim() ?? string.Empty;
        string newWinScp = winScpBox.Text?.Trim() ?? string.Empty;
        double newFontSize = NormalizeTerminalFontSize(fontSizeBox.Value);

        ViewModel.PuttyExePath = newPutty;
        ViewModel.WinScpExePath = newWinScp;
        ViewModel.TerminalFontSize = newFontSize;
        SessionsPanelControl.WinScpExePath = newWinScp;
        SessionTabs.TerminalFontSize = newFontSize;

        if (_settingsLoaded)
        {
            SaveStringSetting(PuttyExePathSettingKey, newPutty);
            SaveStringSetting(WinScpExePathSettingKey, newWinScp);
            SaveTerminalFontSizeSetting(newFontSize);
        }

        SetStatus("Settings saved.");
    }

    private static Button CreateBrowseButton()
    {
        Button button = new()
        {
            Content = "Browse…",
            Padding = new Thickness(14, 6, 14, 6),
        };
        ToolTipService.SetToolTip(button, "Browse for an executable");
        return button;
    }

    /// <summary>
    /// Section header for the settings dialog: BodyStrong label with a top
    /// margin so consecutive sections breathe.
    /// </summary>
    private static TextBlock BuildSettingsSectionHeader(string title) => new()
    {
        Text = title,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 4, 0, 4),
    };

    /// <summary>
    /// Microsoft-style SettingsCard whose action area combines a path
    /// TextBox with a trailing Browse… button. Header + Description follow
    /// the standard SettingsCard layout (Header bold, Description caption).
    /// </summary>
    private static SettingsCard BuildPathSettingsCard(string header, string description, TextBox editor, Button browse)
    {
        Grid actionRow = new() { ColumnSpacing = 8 };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(editor, 0);
        Grid.SetColumn(browse, 1);
        actionRow.Children.Add(editor);
        actionRow.Children.Add(browse);

        return new SettingsCard
        {
            Header = header,
            Description = description,
            Content = actionRow,
            ContentAlignment = ContentAlignment.Vertical,
        };
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
            SetStatus($"File picker failed: {ex.Message}");
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
                SetStatus($"Could not save layout: {ex.Message}");
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
