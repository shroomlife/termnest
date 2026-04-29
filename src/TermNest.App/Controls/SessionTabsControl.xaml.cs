using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TermNest.Core.Diagnostics;
using TermNest.Core.Scripting;
using TermNest.Core.Sessions;

namespace TermNest.App.Controls;

/// <summary>
/// Multi-tab terminal area. Each tab hosts an <see cref="EmbeddedPuttyHost"/>
/// keyed to a <see cref="SessionData"/>. Tab captions are kept in sync with
/// the embedded PuTTY window title via the EmbeddedPuttyHost.TitleChanged
/// event.
/// </summary>
public sealed partial class SessionTabsControl : UserControl
{
    public event EventHandler<string>? StatusMessage;

    private double _terminalFontSize = 15;

    public SessionTabsControl()
    {
        InitializeComponent();
        UpdateEmptyHint();
    }

    public int TabCount => Tabs.TabItems.Count;

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
            ApplyTerminalFontSizeToOpenTabs();
        }
    }

    /// <summary>
    /// Returns the SessionId of every currently open tab, in tab-strip order,
    /// for layout persistence (auto-restore on next launch).
    /// </summary>
    public IEnumerable<string> GetOpenSessionIds()
    {
        foreach (object? obj in Tabs.TabItems)
        {
            if (obj is TabViewItem item)
            {
                switch (item.Tag)
                {
                    case OpenTabContext puttyCtx: yield return puttyCtx.Session.SessionId; break;
                    case SshTabContext sshCtx:    yield return sshCtx.Session.SessionId; break;
                    case ConsoleTabContext consoleCtx: yield return consoleCtx.Session.SessionId; break;
                }
            }
        }
    }

    public async Task<bool> OpenSessionAsync(SessionData session, string puttyExePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        DebugLog.Write("SessionTabs", $"OpenSessionAsync sessionId={session.SessionId} protocol={session.Protocol}");

        if (TrySelectOpenSession(session.SessionId))
        {
            StatusMessage?.Invoke(this, $"{session.SessionName} is already open.");
            return true;
        }

        // SSH/local console sessions are native ConPTY tabs. This avoids
        // fighting WinUI's compositor with an owned foreign PuTTY HWND. Other
        // protocols still use PuTTY until they get native backends.
        if (session.Protocol is ConnectionProtocol.SSH or ConnectionProtocol.SSH2 or ConnectionProtocol.WINCMD or ConnectionProtocol.PS)
        {
            return await OpenConsoleTabAsync(session).ConfigureAwait(true);
        }
        return await OpenPuttyTabAsync(session, puttyExePath).ConfigureAwait(true);
    }

    private bool TrySelectOpenSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        foreach (object? obj in Tabs.TabItems)
        {
            if (obj is not TabViewItem item) continue;

            string? openId = item.Tag switch
            {
                OpenTabContext puttyCtx => puttyCtx.Session.SessionId,
                SshTabContext sshCtx => sshCtx.Session.SessionId,
                ConsoleTabContext consoleCtx => consoleCtx.Session.SessionId,
                _ => null,
            };

            if (string.Equals(openId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = item;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> OpenSshTabAsync(SessionData session)
    {
        DebugLog.Write("SessionTabs", $"OpenSshTabAsync host={session.Host} user={session.Username} hasPwd={!string.IsNullOrEmpty(session.Password)}");

        // v3-imported sessions intentionally drop passwords. Prompt here in
        // SessionTabsControl where XamlRoot is guaranteed valid (this control
        // is in the visual tree). TerminalView's own XamlRoot is null at the
        // moment we'd want to dialog from inside it.
        if (string.IsNullOrEmpty(session.Password))
        {
            string? password = await PromptForPasswordAsync(session).ConfigureAwait(true);
            if (string.IsNullOrEmpty(password))
            {
                StatusMessage?.Invoke(this, "Cancelled — no password supplied.");
                return false;
            }
            session = CloneWithPassword(session, password);
            DebugLog.Write("SessionTabs", "Password supplied via dialog");
        }

        TerminalView? terminal = null;
        TabViewItem? item = null;
        try
        {
            terminal = new TerminalView
            {
                TerminalFontSize = TerminalFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            item = new TabViewItem
            {
                Header = CreateTabHeader(string.IsNullOrWhiteSpace(session.SessionName) ? session.Host : session.SessionName),
                Content = terminal,
                Tag = new SshTabContext(terminal, session),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                MinWidth = 120,
                MaxWidth = 240,
            };

            TabViewItem capturedItem = item;
            terminal.TitleChanged += (_, title) =>
            {
                SetTabHeader(capturedItem, string.IsNullOrWhiteSpace(title) ? session.SessionName : title);
            };
            terminal.Exited += (_, _) =>
            {
                DebugLog.Write("SessionTabs", $"Removing closed SSH tab sessionId={session.SessionId}");
                DispatcherQueue.TryEnqueue(() => RemoveTab(capturedItem));
            };

            Tabs.TabItems.Add(item);
            Tabs.SelectedItem = item;
            UpdateEmptyHint();

            await terminal.ConnectAsync(session).ConfigureAwait(true);
            StatusMessage?.Invoke(this, $"Connected to {session.SessionName}.");
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Connect failed: {ex.Message}");
            if (item != null) RemoveTab(item);
            try { if (terminal != null) await terminal.CloseAsync().ConfigureAwait(true); } catch { }
            return false;
        }
    }

    private async Task<bool> OpenPuttyTabAsync(SessionData session, string puttyExePath)
    {
        if (!File.Exists(puttyExePath))
        {
            StatusMessage?.Invoke(this, $"PuTTY executable not found at \"{puttyExePath}\".");
            return false;
        }

        EmbeddedPuttyHost? host = null;
        TabViewItem? item = null;
        try
        {
            host = new(App.WindowHandle);
            item = new TabViewItem
            {
                Header = CreateTabHeader(string.IsNullOrWhiteSpace(session.SessionName) ? session.Host : session.SessionName),
                Content = host,
                Tag = new OpenTabContext(host, session),
                MinWidth = 120,
                MaxWidth = 240,
            };

            EmbeddedPuttyHost capturedHost = host;
            TabViewItem capturedItem = item;
            host.TitleChanged += (_, title) =>
            {
                SetTabHeader(capturedItem, string.IsNullOrWhiteSpace(title) ? session.SessionName : title);
            };
            host.Exited += (_, _) =>
            {
                DebugLog.Write("SessionTabs", $"Removing closed PuTTY tab sessionId={session.SessionId}");
                DispatcherQueue.TryEnqueue(() => RemoveTab(capturedItem));
            };

            Tabs.TabItems.Add(item);
            Tabs.SelectedItem = item;
            UpdateEmptyHint();

            await host.StartAsync(session, puttyExePath).ConfigureAwait(true);
            StatusMessage?.Invoke(this, $"Connected to {session.SessionName}.");
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Connect failed: {ex.Message}");
            if (item != null) RemoveTab(item);
            try { if (host != null) await host.CloseAsync().ConfigureAwait(true); } catch { }
            return false;
        }
    }

    private async Task<bool> OpenConsoleTabAsync(SessionData session)
    {
        TerminalView? terminal = null;
        TabViewItem? item = null;
        string tabTitle = GetNativeTerminalHeader(session);
        try
        {
            terminal = new TerminalView
            {
                TerminalFontSize = TerminalFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            item = new TabViewItem
            {
                Header = CreateTabHeader(tabTitle),
                Content = terminal,
                Tag = new ConsoleTabContext(terminal, session),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                MinWidth = 120,
                MaxWidth = 240,
            };

            TabViewItem capturedItem = item;
            terminal.TitleChanged += (_, title) =>
            {
                // Keep session tabs named by the saved session/quick-connect
                // target. Remote OSC titles like "root@host: ~" are useful in
                // terminal windows, but make this tab strip noisy and unstable.
                if (session.Protocol is ConnectionProtocol.WINCMD or ConnectionProtocol.PS)
                {
                    SetTabHeader(capturedItem, string.IsNullOrWhiteSpace(title) ? GetNativeTerminalHeader(session) : title);
                }
            };
            terminal.Exited += (_, _) =>
            {
                DebugLog.Write("SessionTabs", $"Removing closed console tab sessionId={session.SessionId}");
                DispatcherQueue.TryEnqueue(() => RemoveTab(capturedItem));
            };

            Tabs.TabItems.Add(item);
            Tabs.SelectedItem = item;
            UpdateEmptyHint();

            await terminal.StartConsoleAsync(session).ConfigureAwait(true);
            StatusMessage?.Invoke(this, $"Started {tabTitle}.");
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Console start failed: {ex.Message}");
            if (item != null) RemoveTab(item);
            try { if (terminal != null) await terminal.CloseAsync().ConfigureAwait(true); } catch { }
            return false;
        }
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        TabViewItem tab = args.Tab;
        object? tag = tab.Tag;

        // Remove synchronously while TabView's close event still owns a valid
        // item reference. Awaiting terminal disposal first can leave WinUI's
        // ItemCollection in a transient state where Remove throws.
        TryRemoveTab(tab);

        try
        {
            switch (tag)
            {
                case OpenTabContext puttyCtx:
                    await puttyCtx.Host.CloseAsync().ConfigureAwait(true);
                    break;
                case SshTabContext sshCtx:
                    await sshCtx.Terminal.CloseAsync().ConfigureAwait(true);
                    break;
                case ConsoleTabContext consoleCtx:
                    await consoleCtx.Terminal.CloseAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("SessionTabs", $"Close tab failed: {ex.GetType().Name} {ex.Message}");
            StatusMessage?.Invoke(this, $"Close failed: {ex.Message}");
        }
    }

    private void RemoveTab(TabViewItem item) => TryRemoveTab(item);

    private bool TryRemoveTab(TabViewItem item)
    {
        try
        {
            for (int i = 0; i < Tabs.TabItems.Count; i++)
            {
                if (ReferenceEquals(Tabs.TabItems[i], item))
                {
                    Tabs.TabItems.RemoveAt(i);
                    UpdateEmptyHint();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("SessionTabs", $"RemoveTab ignored: {ex.GetType().Name} {ex.Message}");
            StatusMessage?.Invoke(this, $"Tab remove failed: {ex.Message}");
        }

        UpdateEmptyHint();
        return false;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Phase 3 keeps focus handoff minimal — when a tab is selected, the
        // ContentPresenter swaps in the EmbeddedPuttyHost which is sized via
        // its existing layout-tracking math, so nothing extra to do here.
        // Richer focus forwarding (Tab/Alt+Tab into the HWND) lands in
        // Phase 5 alongside the keyboard shortcut work.
    }

    /// <summary>
    /// Sends a single command to every open tab in parallel. Used by the
    /// send-to-all command bar (Phase 5+). Each EmbeddedPuttyHost exposes
    /// the captured PuTTY HWND via its CurrentTitle path; we rely on each
    /// host's <see cref="EmbeddedPuttyHost"/> to forward keystrokes
    /// internally — for now the dispatch is synchronous PostMessage.
    /// </summary>
    public async Task SendToAllAsync(IEnumerable<CommandData> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        IReadOnlyList<CommandData> list = commands.ToList();
        if (list.Count == 0) return;

        IntPtr[] targets = Tabs.TabItems
            .OfType<TabViewItem>()
            .Select(item => item.Tag as OpenTabContext)
            .Where(ctx => ctx != null)
            .Select(ctx => ctx!.Host.ChildHwnd)
            .Where(hwnd => hwnd != IntPtr.Zero)
            .ToArray();

        await Task.Run(() =>
        {
            foreach (CommandData cmd in list)
            {
                foreach (IntPtr hwnd in targets)
                {
                    cmd.SendToTerminal(hwnd);
                }
            }
        }).ConfigureAwait(true);
    }

    public async Task CloseAllAsync()
    {
        foreach (object? obj in Tabs.TabItems.ToList())
        {
            if (obj is TabViewItem item)
            {
                switch (item.Tag)
                {
                    case OpenTabContext puttyCtx:
                        await puttyCtx.Host.CloseAsync().ConfigureAwait(true);
                        break;
                    case SshTabContext sshCtx:
                        await sshCtx.Terminal.CloseAsync().ConfigureAwait(true);
                        break;
                    case ConsoleTabContext consoleCtx:
                        await consoleCtx.Terminal.CloseAsync().ConfigureAwait(true);
                        break;
                }
                RemoveTab(item);
            }
        }
    }

    /// <summary>
    /// Refresh hook only used by the legacy EmbeddedPuttyHost path (foreign
    /// HWND tracking). TerminalView is a XAML control and follows layout
    /// natively — no manual refresh needed.
    /// </summary>
    public void RefreshEmbeddedHostPositions()
    {
        foreach (object? obj in Tabs.TabItems)
        {
            if (obj is TabViewItem item && item.Tag is OpenTabContext ctx)
            {
                ctx.Host.RefreshPosition();
            }
        }
    }

    /// <summary>Per-tab metadata for the legacy reparented-PuTTY path.</summary>
    private sealed record OpenTabContext(EmbeddedPuttyHost Host, SessionData Session);

    /// <summary>Per-tab metadata for the native SSH path.</summary>
    private sealed record SshTabContext(TerminalView Terminal, SessionData Session);

    private static string GetNativeTerminalHeader(SessionData session)
    {
        if (!string.IsNullOrWhiteSpace(session.SessionName))
        {
            return session.SessionName;
        }
        return session.Protocol switch
        {
            ConnectionProtocol.SSH or ConnectionProtocol.SSH2 => string.IsNullOrWhiteSpace(session.Username)
                ? session.Host
                : $"{session.Username}@{session.Host}",
            ConnectionProtocol.PS => "PowerShell",
            _ => "Command Prompt",
        };
    }

    private static TextBlock CreateTabHeader(string? title) => new()
    {
        Text = string.IsNullOrWhiteSpace(title) ? "Session" : title,
        MaxWidth = 170,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 13,
    };

    private static void SetTabHeader(TabViewItem item, string? title)
    {
        string text = string.IsNullOrWhiteSpace(title) ? "Session" : title;
        if (item.Header is TextBlock textBlock)
        {
            textBlock.Text = text;
            return;
        }
        item.Header = CreateTabHeader(text);
    }

    private void ApplyTerminalFontSizeToOpenTabs()
    {
        foreach (object? obj in Tabs.TabItems)
        {
            if (obj is not TabViewItem item) continue;

            switch (item.Tag)
            {
                case SshTabContext sshCtx:
                    sshCtx.Terminal.TerminalFontSize = TerminalFontSize;
                    break;
                case ConsoleTabContext consoleCtx:
                    consoleCtx.Terminal.TerminalFontSize = TerminalFontSize;
                    break;
            }
        }
    }

    private static double NormalizeTerminalFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize))
        {
            return 15;
        }

        return Math.Clamp(Math.Round(fontSize), 10, 28);
    }

    /// <summary>Per-tab metadata for the native ConPTY terminal path.</summary>
    private sealed record ConsoleTabContext(TerminalView Terminal, SessionData Session);

    private async Task<string?> PromptForPasswordAsync(SessionData session)
    {
        PasswordBox passwordBox = new()
        {
            PlaceholderText = "Password",
            PasswordRevealMode = PasswordRevealMode.Peek,
            MinWidth = 280,
        };

        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"{session.Username ?? "root"}@{session.Host}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(passwordBox);

        ContentDialog dialog = new()
        {
            Title = "SSH password",
            Content = content,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        passwordBox.Loaded += (_, _) => passwordBox.Focus(FocusState.Programmatic);
        passwordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
            }
        };

        ContentDialogResult result = await dialog.ShowAsync();
        // Treat Enter-pressed (Hide) the same as Primary if a password was typed.
        if (result == ContentDialogResult.Primary || (result == ContentDialogResult.None && !string.IsNullOrEmpty(passwordBox.Password)))
        {
            return passwordBox.Password;
        }
        return null;
    }

    private static SessionData CloneWithPassword(SessionData source, string password) => new()
    {
        SessionId = source.SessionId,
        SessionName = source.SessionName,
        Host = source.Host,
        Port = source.Port,
        Protocol = source.Protocol,
        PuttySession = source.PuttySession,
        Username = source.Username,
        Password = password,
        ExtraArgs = source.ExtraArgs,
        SpslFileName = source.SpslFileName,
        Notes = source.Notes,
        ImageKey = source.ImageKey,
        RemotePath = source.RemotePath,
        LocalPath = source.LocalPath,
        WorkingDirectory = source.WorkingDirectory,
    };

    private void UpdateEmptyHint()
    {
        bool hasTabs = Tabs.TabItems.Count > 0;
        Tabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
    }
}
