using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TermNest.Core.Diagnostics;
using TermNest.Core.Sessions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace TermNest.App.Controls;

/// <summary>
/// Side-rail panel that shows the session hierarchy. Phase 2 covers the
/// browse / search / import flows; per-node context menus and drag-drop
/// arrive in Phase 3.
/// </summary>
public sealed partial class SessionsPanel : UserControl
{
    public event EventHandler<SessionData>? SessionInvoked;

    /// <summary>
    /// Bubbles transient status text (clipboard copies, errors, save results)
    /// up to the shell so a single bottom-strip footer renders every message.
    /// The shell handles auto-clear timing.
    /// </summary>
    public event EventHandler<string>? StatusMessage;

    private const double SessionEditorDialogWidth = 900;
    private const double SessionEditorDialogMaxWidth = 980;
    private const double SessionEditorDialogMaxHeight = 720;

    private SessionStore? _store;
    private List<SessionData> _allSessions = new();
    private List<string> _emptyFolders = new();
    private SessionTreeNode _allTree = new() { Name = "Sessions", Path = string.Empty, IsFolder = true };
    private static readonly TimeSpan DuplicateInvokeWindow = TimeSpan.FromMilliseconds(750);
    private string? _lastInvokedSessionId;
    private DateTimeOffset _lastInvokedAtUtc = DateTimeOffset.MinValue;
    private string? _lastEditedSessionId;
    private DateTimeOffset _lastEditedAtUtc = DateTimeOffset.MinValue;
    private HashSet<string>? _expandedFolderPaths;
    private bool _isApplyingTreeState;
    private bool _isSessionEditorDialogOpen;

    public string WinScpExePath { get; set; } = @"C:\Program Files (x86)\WinSCP\WinSCP.exe";

    public SessionsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private string _localStateDirectory = string.Empty;
    private bool _isLoaded;

    /// <summary>
    /// Where the sessions.json store lives. Set this from the parent shell.
    /// Setting it after the panel has already loaded triggers an immediate
    /// reload so the order of WinUI Loaded events between parent and child
    /// doesn't matter.
    /// </summary>
    public string LocalStateDirectory
    {
        get => _localStateDirectory;
        set
        {
            if (_localStateDirectory == value) return;
            _localStateDirectory = value;
            if (_isLoaded && !string.IsNullOrEmpty(value))
            {
                _store = new SessionStore(value);
                _ = ReloadAsync();
            }
        }
    }

    public IReadOnlyCollection<string>? ExpandedFolderPaths
    {
        get => _expandedFolderPaths?.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        set
        {
            _expandedFolderPaths = value == null
                ? null
                : new HashSet<string>(value.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);

            if (_allTree.Children.Count > 0)
            {
                ApplyFilter(SearchBox.Text);
            }
        }
    }

    public List<string> GetExpandedFolderPaths()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        CollectExpandedFolderPaths(_allTree, paths);
        return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>x:Bind helper: Segoe Fluent Icons code points for the tree glyphs.
    /// E8B7 = Folder, EC7A = CommandPrompt.</summary>
    public static string GlyphFor(bool isFolder) => isFolder ? "" : "";

    public static Visibility ActionVisibilityFor(bool isFolder)
        => isFolder ? Visibility.Collapsed : Visibility.Visible;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;

        if (string.IsNullOrEmpty(_localStateDirectory))
        {
            // Parent shell hasn't pushed the path yet — set the placeholder
            // status; the LocalStateDirectory setter will trigger ReloadAsync
            // as soon as the parent assigns a value.
            SetStatus("Waiting for shell to configure session store…");
            return;
        }

        _store = new SessionStore(_localStateDirectory);
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (_store == null) return;

        SessionStoreSnapshot snapshot = await _store.LoadAsync().ConfigureAwait(true);
        _allSessions = snapshot.Sessions;
        _emptyFolders = snapshot.EmptyFolders;
        _allTree = SessionTreeNode.BuildTree(_allSessions, _emptyFolders);
        ApplyFilter(SearchBox.Text);
        SetStatus($"{_allSessions.Count} session(s) loaded.");
    }

    private Task PersistAsync() =>
        _store == null
            ? Task.CompletedTask
            : _store.SaveAsync(_allSessions, _emptyFolders);

    private void ApplyFilter(string? query)
    {
        SessionTree.ItemsSource = null;
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        SessionTreeNode tree = _allTree;

        if (hasQuery)
        {
            // Build a filtered subtree: keep folders that contain matching sessions,
            // keep sessions whose name or host or id matches.
            IEnumerable<SessionData> matches = _allSessions.Where(s =>
                s.SessionId.Contains(query!, StringComparison.OrdinalIgnoreCase) ||
                (s.Host?.Contains(query!, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Username?.Contains(query!, StringComparison.OrdinalIgnoreCase) ?? false));

            tree = SessionTreeNode.BuildTree(matches.ToList());
        }

        _isApplyingTreeState = true;
        try
        {
            ApplyExpansionState(tree, expandAll: hasQuery);
            SessionTree.ItemsSource = tree.Children;
        }
        finally
        {
            _isApplyingTreeState = false;
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyFilter(sender.Text);
        }
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // ItemInvoked fires on double-tap / Enter / Space. Single-tap is
        // already handled by OnTreeItemTapped (copy host) and the inline
        // Connect button, so this path's job is keyboard activation:
        // pressing Enter on a focused leaf should open the session, never
        // the editor (the editor only opens via the explicit Edit button or
        // context menu).
        DebugLog.Write("SessionsPanel", $"ItemInvoked invokedItem={args.InvokedItem?.GetType().Name}");
        TryInvoke(args.InvokedItem);
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
        => SetFolderExpanded(args.Item, expanded: true);

    private void OnTreeCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
        => SetFolderExpanded(args.Item, expanded: false);

    private void OnTreeItemTapped(object sender, TappedRoutedEventArgs e)
    {
        DebugLog.Write("SessionsPanel", $"Tapped sender={sender?.GetType().Name}");
        if (IsInsideSessionActionButtons(e.OriginalSource as DependencyObject))
        {
            // Inline button handles its own click; do not bubble into the
            // row's "copy IP" behaviour, otherwise every action button would
            // also stamp the clipboard.
            e.Handled = true;
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is SessionTreeNode node && !node.IsFolder && node.Session != null)
        {
            e.Handled = TryCopyHost(node.Session);
        }
    }

    private void OnSessionTreeRowPointerEntered(object sender, PointerRoutedEventArgs e)
        => SetSessionActionButtonsVisible(sender, visible: true);

    private void OnSessionTreeRowPointerExited(object sender, PointerRoutedEventArgs e)
        => SetSessionActionButtonsVisible(sender, visible: false);

    private void OnConnectSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionTreeNode node })
        {
            TryInvoke(node);
        }
    }

    private void OnOpenWinScpSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionTreeNode { Session: { } session } })
        {
            OpenInWinScp(session);
        }
    }

    private async void OnEditSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionTreeNode node })
        {
            await TryEditAsync(node).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Raises a transient footer message. The shell owns the actual TextBlock
    /// and the auto-clear timer — this control is just an emitter so all
    /// status messages converge in one place.
    /// </summary>
    private void SetStatus(string text) => StatusMessage?.Invoke(this, text ?? string.Empty);

    private bool TryCopyHost(SessionData session)
    {
        string? host = session.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            SetStatus($"{DisplayNameFor(session)} has no host configured.");
            return true;
        }

        try
        {
            DataPackage package = new();
            package.SetText(host);
            Clipboard.SetContent(package);
            SetStatus($"Copied {host} to clipboard.");
            DebugLog.Write("SessionsPanel", $"Copied host {host} for sessionId={session.SessionId}");
        }
        catch (Exception ex)
        {
            DebugLog.Write("SessionsPanel", $"Clipboard copy failed: {ex}");
            SetStatus($"Copy failed: {ex.Message}");
        }
        return true;
    }

    private void OnTreeItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SessionTreeNode node)
        {
            return;
        }

        e.Handled = true;
        if (node.IsFolder)
        {
            ShowFolderContextMenu(fe, node);
            return;
        }

        if (node.Session == null)
        {
            return;
        }

        ShowSessionContextMenu(fe, node);
    }

    private bool TryInvoke(object? candidate)
    {
        if (candidate is SessionTreeNode node && !node.IsFolder && node.Session != null)
        {
            string sessionId = node.Session.SessionId;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastInvokedSessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                now - _lastInvokedAtUtc < DuplicateInvokeWindow)
            {
                DebugLog.Write("SessionsPanel", $"Suppressing duplicate invoke for {sessionId}");
                return true;
            }

            _lastInvokedSessionId = sessionId;
            _lastInvokedAtUtc = now;

            DebugLog.Write("SessionsPanel", $"Invoking session {sessionId}");
            SetStatus($"Opening {node.Session.SessionName}…");
            SessionInvoked?.Invoke(this, node.Session);
            return true;
        }
        return false;
    }

    private async Task<bool> TryEditAsync(object? candidate)
    {
        if (candidate is SessionTreeNode node && !node.IsFolder && node.Session != null)
        {
            string sessionId = node.Session.SessionId;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastEditedSessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                now - _lastEditedAtUtc < DuplicateInvokeWindow)
            {
                DebugLog.Write("SessionsPanel", $"Suppressing duplicate edit for {sessionId}");
                return true;
            }

            _lastEditedSessionId = sessionId;
            _lastEditedAtUtc = now;

            DebugLog.Write("SessionsPanel", $"Editing session {sessionId}");
            SetStatus($"Editing {DisplayNameFor(node.Session)}...");
            await EditSessionAsync(node.Session).ConfigureAwait(true);
            return true;
        }

        return false;
    }

    private static void SetSessionActionButtonsVisible(object sender, bool visible)
    {
        if (sender is not DependencyObject root)
        {
            return;
        }

        FrameworkElement? actionButtons = FindDescendantByName<FrameworkElement>(root, "SessionActionButtons");
        if (actionButtons == null || actionButtons.Visibility != Visibility.Visible)
        {
            return;
        }

        actionButtons.Opacity = visible ? 1 : 0;
    }

    private static bool IsInsideSessionActionButtons(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { Name: "SessionActionButtons" })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            T? nested = FindDescendantByName<T>(child, name);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private void SetFolderExpanded(object? item, bool expanded)
    {
        if (_isApplyingTreeState || item is not SessionTreeNode { IsFolder: true } node)
        {
            return;
        }

        EnsureExplicitExpansionSet();
        node.IsExpanded = expanded;

        if (expanded)
        {
            _expandedFolderPaths!.Add(node.Path);
        }
        else
        {
            _expandedFolderPaths!.Remove(node.Path);
        }
    }

    private void EnsureExplicitExpansionSet()
    {
        if (_expandedFolderPaths != null)
        {
            return;
        }

        _expandedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedFolderPaths(_allTree, _expandedFolderPaths);
    }

    private void ApplyExpansionState(SessionTreeNode node, bool expandAll)
    {
        foreach (SessionTreeNode child in node.Children)
        {
            if (!child.IsFolder)
            {
                continue;
            }

            child.IsExpanded = expandAll || (_expandedFolderPaths?.Contains(child.Path) ?? true);
            ApplyExpansionState(child, expandAll);
        }
    }

    private static void CollectExpandedFolderPaths(SessionTreeNode node, ISet<string> paths)
    {
        foreach (SessionTreeNode child in node.Children)
        {
            if (!child.IsFolder)
            {
                continue;
            }

            if (child.IsExpanded)
            {
                paths.Add(child.Path);
            }
            CollectExpandedFolderPaths(child, paths);
        }
    }

    private async void OnNewSessionClick(object sender, RoutedEventArgs e)
        => await NewSessionAsync(folderPath: null).ConfigureAwait(true);

    private void ShowFolderContextMenu(FrameworkElement target, SessionTreeNode node)
    {
        MenuFlyout flyout = new();
        MenuFlyoutItem newHereItem = new()
        {
            Text = "New session here",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        newHereItem.Click += async (_, _) => await NewSessionAsync(node.Path).ConfigureAwait(true);
        flyout.Items.Add(newHereItem);
        flyout.ShowAt(target);
    }

    private void ShowSessionContextMenu(FrameworkElement target, SessionTreeNode node)
    {
        if (node.Session == null)
        {
            return;
        }

        SessionData session = node.Session;
        MenuFlyout flyout = new();

        MenuFlyoutItem openItem = new()
        {
            Text = "Open",
            Icon = new FontIcon { Glyph = "\uE8A7" },
        };
        openItem.Click += (_, _) => TryInvoke(node);
        flyout.Items.Add(openItem);

        MenuFlyoutItem winScpItem = new()
        {
            Text = "Open in WinSCP",
            Icon = new FontIcon { Glyph = "\uE8D4" },
            IsEnabled = CanOpenInWinScp(session),
        };
        winScpItem.Click += (_, _) => OpenInWinScp(session);
        flyout.Items.Add(winScpItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem editItem = new()
        {
            Text = "Edit session...",
            Icon = new FontIcon { Glyph = "\uE70F" },
        };
        editItem.Click += async (_, _) => await EditSessionAsync(session).ConfigureAwait(true);
        flyout.Items.Add(editItem);

        MenuFlyoutItem deleteItem = new()
        {
            Text = "Delete session...",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        deleteItem.Click += async (_, _) => await DeleteSessionAsync(session).ConfigureAwait(true);
        flyout.Items.Add(deleteItem);

        flyout.ShowAt(target);
    }

    private void OpenInWinScp(SessionData session)
    {
        if (!CanOpenInWinScp(session))
        {
            SetStatus("WinSCP supports SSH/SFTP sessions here.");
            return;
        }

        string path = string.IsNullOrWhiteSpace(WinScpExePath)
            ? @"C:\Program Files (x86)\WinSCP\WinSCP.exe"
            : WinScpExePath.Trim();
        if (!File.Exists(path))
        {
            SetStatus($"WinSCP executable not found at \"{path}\".");
            return;
        }

        try
        {
            string sessionUrl = BuildWinScpSessionUrl(session);
            System.Diagnostics.ProcessStartInfo startInfo = new()
            {
                FileName = path,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            };
            startInfo.ArgumentList.Add(sessionUrl);

            System.Diagnostics.Process.Start(startInfo);
            SetStatus($"Opening {DisplayNameFor(session)} in WinSCP.");
            DebugLog.Write("SessionsPanel", $"Launching WinSCP for sessionId={session.SessionId}");
        }
        catch (Exception ex)
        {
            DebugLog.Write("SessionsPanel", $"WinSCP launch failed: {ex}");
            SetStatus($"WinSCP launch failed: {ex.Message}");
        }
    }

    private async Task EditSessionAsync(SessionData session)
    {
        if (_isSessionEditorDialogOpen)
        {
            return;
        }

        _isSessionEditorDialogOpen = true;
        try
        {
            await ShowSessionEditorAsync(session, isNew: false).ConfigureAwait(true);
        }
        finally
        {
            _isSessionEditorDialogOpen = false;
        }
    }

    private async Task NewSessionAsync(string? folderPath)
    {
        if (_isSessionEditorDialogOpen)
        {
            return;
        }

        _isSessionEditorDialogOpen = true;
        try
        {
            await ShowSessionEditorAsync(CreateNewSessionDraft(folderPath), isNew: true).ConfigureAwait(true);
        }
        finally
        {
            _isSessionEditorDialogOpen = false;
        }
    }

    private async Task ShowSessionEditorAsync(SessionData session, bool isNew)
    {
        if (_store == null)
        {
            SetStatus(isNew
                ? "Cannot create - session store not initialised."
                : "Cannot edit - session store not initialised.");
            return;
        }

        string originalSessionId = session.SessionId;
        SessionData draft = CloneSession(session);

        TextBox sessionIdBox = new()
        {
            Text = draft.SessionId,
            PlaceholderText = "Folder/SessionName",
            MinWidth = 280,
        };
        TextBox nameBox = new()
        {
            Text = string.IsNullOrWhiteSpace(draft.SessionName) ? Path.GetFileName(draft.SessionId) : draft.SessionName,
            PlaceholderText = "Display name",
            MinWidth = 280,
        };
        ComboBox protocolBox = new()
        {
            ItemsSource = Enum.GetValues<ConnectionProtocol>(),
            SelectedItem = draft.Protocol,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        TextBox hostBox = new()
        {
            Text = draft.Host,
            PlaceholderText = "host or IP",
            MinWidth = 280,
        };
        NumberBox portBox = new()
        {
            Value = draft.Port > 0 ? draft.Port : DefaultPortFor(draft.Protocol),
            Minimum = 1,
            Maximum = 65535,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        TextBox usernameBox = new()
        {
            Text = draft.Username ?? string.Empty,
            PlaceholderText = "user",
            MinWidth = 280,
        };
        TextBox puttySessionBox = new()
        {
            Text = draft.PuttySession ?? string.Empty,
            PlaceholderText = "PuTTY saved session",
        };
        TextBox extraArgsBox = new()
        {
            Text = draft.ExtraArgs ?? string.Empty,
            PlaceholderText = "-L 8080:localhost:80",
        };
        TextBox workingDirectoryBox = new()
        {
            Text = draft.WorkingDirectory ?? string.Empty,
            PlaceholderText = "C:\\path\\to\\folder",
        };
        TextBox notesBox = new()
        {
            Text = draft.Notes ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 118,
            PlaceholderText = "Notes",
        };

        protocolBox.SelectionChanged += (_, _) =>
        {
            if (protocolBox.SelectedItem is ConnectionProtocol protocol && (!double.IsFinite(portBox.Value) || portBox.Value <= 0))
            {
                portBox.Value = DefaultPortFor(protocol);
            }
        };

        Grid topGrid = new()
        {
            ColumnSpacing = 28,
        };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel identitySection = CreateFormSection(
            "Identity",
            ("Session path", sessionIdBox),
            ("Display name", nameBox),
            ("Protocol", protocolBox));

        StackPanel connectionSection = CreateFormSection(
            "Connection",
            ("Host", hostBox),
            ("Port", portBox),
            ("User", usernameBox));

        Grid.SetColumn(identitySection, 0);
        Grid.SetColumn(connectionSection, 1);
        topGrid.Children.Add(identitySection);
        topGrid.Children.Add(connectionSection);

        StackPanel advancedSection = CreateFormSection(
            "Advanced",
            ("PuTTY session", puttySessionBox),
            ("Extra args", extraArgsBox),
            ("Working dir", workingDirectoryBox),
            ("Notes", notesBox));

        StackPanel contentPanel = new()
        {
            Width = SessionEditorDialogWidth - 48,
            MaxWidth = SessionEditorDialogMaxWidth - 48,
            Spacing = 22,
        };
        contentPanel.Children.Add(topGrid);
        contentPanel.Children.Add(advancedSection);

        ScrollViewer scrollViewer = new()
        {
            Content = contentPanel,
            MaxHeight = SessionEditorDialogMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        ContentDialog dialog = new()
        {
            Title = isNew ? "New session" : "Edit session",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            MinWidth = SessionEditorDialogWidth,
            MaxWidth = SessionEditorDialogMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dialog.Resources["ContentDialogMinWidth"] = SessionEditorDialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = SessionEditorDialogMaxWidth;

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? validationError = ValidateSessionEdit(
                isNew ? null : originalSessionId,
                sessionIdBox.Text,
                nameBox.Text,
                protocolBox.SelectedItem is ConnectionProtocol protocol ? protocol : ConnectionProtocol.SSH,
                hostBox.Text,
                portBox.Value);

            if (validationError != null)
            {
                args.Cancel = true;
                SetStatus(validationError);
            }
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        draft.SessionId = NormalizeSessionId(sessionIdBox.Text);
        draft.SessionName = string.IsNullOrWhiteSpace(nameBox.Text) ? LastPathSegment(draft.SessionId) : nameBox.Text.Trim();
        draft.Protocol = protocolBox.SelectedItem is ConnectionProtocol protocol ? protocol : ConnectionProtocol.SSH;
        draft.Host = hostBox.Text.Trim();
        draft.Port = (int)Math.Clamp(Math.Round(portBox.Value), 1, 65535);
        draft.Username = NullIfWhiteSpace(usernameBox.Text);
        draft.PuttySession = NullIfWhiteSpace(puttySessionBox.Text);
        draft.ExtraArgs = NullIfWhiteSpace(extraArgsBox.Text);
        draft.WorkingDirectory = NullIfWhiteSpace(workingDirectoryBox.Text);
        draft.Notes = NullIfWhiteSpace(notesBox.Text);

        if (isNew)
        {
            _allSessions.Add(draft);
        }
        else
        {
            int index = _allSessions.FindIndex(s => string.Equals(s.SessionId, originalSessionId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                SetStatus("Cannot save - session no longer exists.");
                return;
            }

            _allSessions[index] = draft;
        }

        // The session now occupies its folder path, so drop any matching
        // empty-folder placeholder; nested ancestors stay (no-op if absent).
        if (!string.IsNullOrWhiteSpace(draft.FolderPath))
        {
            _emptyFolders.RemoveAll(f => string.Equals(f, draft.FolderPath, StringComparison.OrdinalIgnoreCase));
        }

        await PersistAsync().ConfigureAwait(true);
        EnsureExplicitExpansionSet();
        if (!string.IsNullOrWhiteSpace(draft.FolderPath))
        {
            _expandedFolderPaths!.Add(draft.FolderPath);
        }
        await ReloadAsync().ConfigureAwait(true);
        SetStatus(isNew ? $"Created {draft.SessionName}." : $"Saved {draft.SessionName}.");
    }

    private async Task DeleteSessionAsync(SessionData session)
    {
        if (_store == null)
        {
            SetStatus("Cannot delete - session store not initialised.");
            return;
        }

        ContentDialog dialog = new()
        {
            Title = "Delete session",
            Content = $"Delete \"{DisplayNameFor(session)}\"?\n\nThis removes it from sessions.json.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        int removed = _allSessions.RemoveAll(s => string.Equals(s.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            SetStatus("Cannot delete - session no longer exists.");
            return;
        }

        await PersistAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        SetStatus($"Deleted {DisplayNameFor(session)}.");
    }

    private string? ValidateSessionEdit(string? originalSessionId, string sessionId, string displayName, ConnectionProtocol protocol, string host, double port)
    {
        string normalizedId = NormalizeSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return "Session path is required.";
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "Display name is required.";
        }

        if (RequiresHost(protocol) && string.IsNullOrWhiteSpace(host))
        {
            return "Host is required.";
        }

        if (!double.IsFinite(port) || port < 1 || port > 65535)
        {
            return "Port must be between 1 and 65535.";
        }

        bool duplicate = _allSessions.Any(s =>
            !string.Equals(s.SessionId, originalSessionId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.SessionId, normalizedId, StringComparison.OrdinalIgnoreCase));
        return duplicate ? $"A session named \"{normalizedId}\" already exists." : null;
    }

    private static StackPanel CreateFormSection(string title, params (string Label, FrameworkElement Editor)[] rows)
    {
        StackPanel section = new()
        {
            Spacing = 10,
        };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });

        Grid form = new()
        {
            ColumnSpacing = 12,
            RowSpacing = 10,
        };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        foreach ((string label, FrameworkElement editor) in rows)
        {
            AddFormRow(form, label, editor);
        }

        section.Children.Add(form);
        return section;
    }

    private static void AddFormRow(Grid form, string label, FrameworkElement editor)
    {
        int row = form.RowDefinitions.Count;
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool multiline = editor is TextBox { AcceptsReturn: true };

        TextBlock labelBlock = new()
        {
            Text = label,
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = multiline ? new Thickness(0, 7, 0, 0) : new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        form.Children.Add(labelBlock);
        form.Children.Add(editor);
    }

    private static SessionData CloneSession(SessionData source) => new()
    {
        SessionId = source.SessionId,
        SessionName = source.SessionName,
        Host = source.Host,
        Port = source.Port,
        Protocol = source.Protocol,
        PuttySession = source.PuttySession,
        Username = source.Username,
        ExtraArgs = source.ExtraArgs,
        SpslFileName = source.SpslFileName,
        Notes = source.Notes,
        ImageKey = source.ImageKey,
        RemotePath = source.RemotePath,
        LocalPath = source.LocalPath,
        WorkingDirectory = source.WorkingDirectory,
    };

    private SessionData CreateNewSessionDraft(string? folderPath)
    {
        string sessionId = GenerateUniqueSessionId(folderPath);
        return new SessionData
        {
            SessionId = sessionId,
            SessionName = LastPathSegment(sessionId),
            Protocol = ConnectionProtocol.SSH,
            Host = string.Empty,
            Port = 22,
            Username = "root",
        };
    }

    private string GenerateUniqueSessionId(string? folderPath)
    {
        string prefix = string.IsNullOrWhiteSpace(folderPath) ? string.Empty : NormalizeSessionId(folderPath);
        string baseName = string.IsNullOrWhiteSpace(prefix) ? "New Session" : $"{prefix}/New Session";
        if (!SessionIdExists(baseName))
        {
            return baseName;
        }

        for (int i = 2; i < 10000; i++)
        {
            string candidate = string.IsNullOrWhiteSpace(prefix) ? $"New Session {i}" : $"{prefix}/New Session {i}";
            if (!SessionIdExists(candidate))
            {
                return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(prefix)
            ? $"New Session {DateTimeOffset.Now:yyyyMMddHHmmss}"
            : $"{prefix}/New Session {DateTimeOffset.Now:yyyyMMddHHmmss}";
    }

    private bool SessionIdExists(string sessionId)
        => _allSessions.Any(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    private static int DefaultPortFor(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Telnet => 23,
        ConnectionProtocol.Rlogin => 513,
        ConnectionProtocol.Raw => 23,
        ConnectionProtocol.RDP => 3389,
        ConnectionProtocol.VNC => 5900,
        _ => 22,
    };

    private static bool CanOpenInWinScp(SessionData session)
        => session.Protocol is ConnectionProtocol.SSH or ConnectionProtocol.SSH2 &&
           !string.IsNullOrWhiteSpace(session.Host);

    private static string BuildWinScpSessionUrl(SessionData session)
    {
        int port = session.Port > 0 ? session.Port : 22;
        string username = string.IsNullOrWhiteSpace(session.Username)
            ? string.Empty
            : Uri.EscapeDataString(session.Username.Trim()) + "@";
        string host = EscapeWinScpHost(session.Host.Trim());
        string portPart = port == 22 ? string.Empty : $":{port}";
        string path = EscapeWinScpRemotePath(session.RemotePath);
        return $"sftp://{username}{host}{portPart}{path}";
    }

    private static string EscapeWinScpHost(string host)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private static string EscapeWinScpRemotePath(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return "/";
        }

        string normalized = remotePath.Replace('\\', '/').Trim();
        normalized = "/" + normalized.Trim('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string encoded = "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
        return encoded.EndsWith('/') ? encoded : encoded + "/";
    }

    private static bool RequiresHost(ConnectionProtocol protocol) => protocol is not (ConnectionProtocol.WINCMD or ConnectionProtocol.PS);

    private static string DisplayNameFor(SessionData session)
        => string.IsNullOrWhiteSpace(session.SessionName) ? LastPathSegment(session.SessionId) : session.SessionName;

    private static string LastPathSegment(string path)
    {
        string normalized = NormalizeSessionId(path);
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string NormalizeSessionId(string value)
        => string.Join('/', value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_store == null)
        {
            SetStatus("Cannot create folder — session store not initialised.");
            return;
        }

        TextBox nameBox = new()
        {
            PlaceholderText = "Folder name (use / for nesting)",
            MinWidth = 320,
        };

        ContentDialog dialog = new()
        {
            Title = "New folder",
            Content = nameBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        string folderPath = NormalizeSessionId(nameBox.Text);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetStatus("Folder name is required.");
            return;
        }

        // Reject if a session of the same name already exists at this path.
        if (_allSessions.Any(s => string.Equals(s.SessionId, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A session already uses the path \"{folderPath}\".");
            return;
        }

        if (_emptyFolders.Any(f => string.Equals(f, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"Folder \"{folderPath}\" already exists.");
            return;
        }

        _emptyFolders.Add(folderPath);
        await PersistAsync().ConfigureAwait(true);

        EnsureExplicitExpansionSet();
        _expandedFolderPaths!.Add(folderPath);

        await ReloadAsync().ConfigureAwait(true);
        SetStatus($"Created folder \"{folderPath}\".");
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_store == null)
        {
            SetStatus("Cannot import — session store not initialised.");
            return;
        }

        FileOpenPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".xml");

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            IReadOnlyList<SessionData> imported = ImportSuperPuttySessionsXml(file.Path);
            if (imported.Count == 0)
            {
                SetStatus($"No sessions found in {Path.GetFileName(file.Path)}.");
                return;
            }

            // Merge by SessionId — imported entries overwrite existing matches.
            Dictionary<string, SessionData> map = _allSessions.ToDictionary(
                s => s.SessionId, StringComparer.OrdinalIgnoreCase);
            foreach (SessionData s in imported)
            {
                map[s.SessionId] = s;
            }
            _allSessions = map.Values.ToList();

            await PersistAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            SetStatus($"Imported {imported.Count} session(s) from {Path.GetFileName(file.Path)}.");
        }
        catch (Exception ex)
        {
            DebugLog.Write("SessionsPanel", $"Import failed: {ex}");
            SetStatus($"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a SuperPuTTY 3.x <c>sessions.xml</c> and converts each entry to
    /// a TermNest <see cref="SessionData"/>. The legacy file is an
    /// <c>&lt;ArrayOfSessionData&gt;</c> with each session as a
    /// <c>&lt;SessionData&gt;</c> element whose fields live on attributes.
    /// Unknown attributes are ignored; missing ones get TermNest defaults.
    /// </summary>
    private static IReadOnlyList<SessionData> ImportSuperPuttySessionsXml(string xmlFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlFilePath);
        if (!File.Exists(xmlFilePath))
        {
            throw new FileNotFoundException("sessions.xml not found", xmlFilePath);
        }

        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(xmlFilePath);
        if (doc.Root == null)
        {
            return Array.Empty<SessionData>();
        }

        List<SessionData> result = new();
        foreach (System.Xml.Linq.XElement element in doc.Root.Elements("SessionData"))
        {
            string sessionId = (string?)element.Attribute("SessionId") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            string sessionName = (string?)element.Attribute("SessionName") ?? LastPathSegment(sessionId);
            string protocolText = (string?)element.Attribute("Proto") ?? "SSH";
            ConnectionProtocol protocol = Enum.TryParse(protocolText, ignoreCase: true, out ConnectionProtocol p)
                ? p
                : ConnectionProtocol.SSH;

            int port = 22;
            string? portText = (string?)element.Attribute("Port");
            if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText, out int parsedPort) && parsedPort is > 0 and <= 65535)
            {
                port = parsedPort;
            }

            result.Add(new SessionData
            {
                SessionId = NormalizeSessionId(sessionId),
                SessionName = sessionName,
                Host = (string?)element.Attribute("Host") ?? string.Empty,
                Port = port,
                Protocol = protocol,
                Username = NullIfWhiteSpace((string?)element.Attribute("Username") ?? string.Empty),
                PuttySession = NullIfWhiteSpace((string?)element.Attribute("PuttySession") ?? string.Empty),
                ExtraArgs = NullIfWhiteSpace((string?)element.Attribute("ExtraArgs") ?? string.Empty),
                SpslFileName = NullIfWhiteSpace((string?)element.Attribute("SPSLFileName") ?? string.Empty),
                ImageKey = NullIfWhiteSpace((string?)element.Attribute("ImageKey") ?? string.Empty),
                RemotePath = NullIfWhiteSpace((string?)element.Attribute("RemotePath") ?? string.Empty) ?? string.Empty,
                LocalPath = NullIfWhiteSpace((string?)element.Attribute("LocalPath") ?? string.Empty) ?? string.Empty,
            });
        }
        return result;
    }
}
