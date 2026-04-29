using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TermNest.Core.Layouts;
using Windows.ApplicationModel;
using Windows.Graphics;

namespace TermNest.App;

public sealed partial class MainWindow : Window
{
    private const int MinimumSavedWindowWidth = 900;
    private const int MinimumSavedWindowHeight = 600;

    public IRelayCommand ShowFromTrayCommand { get; }

    public MainWindow()
    {
        ShowFromTrayCommand = new RelayCommand(BringToFront);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyWindowIcon();

        Shell.LayoutWindowPlacementLoaded += (_, placement) => RestoreWindowPlacement(placement);

        // Embedded PuTTY windows are *owned top-level* windows positioned over
        // a placeholder Border (see EmbeddedPuttyHost). They must follow the
        // shell window when it moves or resizes — re-fire layout updates so
        // every host re-runs ClientToScreen + MoveWindow with the new screen
        // coordinates.
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                Shell.RefreshEmbeddedHostPositions();
            }
        };

        // Persist layout (incl. open-session list) on close. Errors are
        // best-effort but logged so a corrupted save isn't silent.
        Closed += async (_, _) =>
        {
            try { await Shell.SaveCurrentLayoutAsync(CaptureWindowPlacement()).ConfigureAwait(true); }
            catch (Exception ex) { Debug.WriteLine($"[Layout] Save on close failed: {ex}"); }
            try { await Shell.CloseAllSessionsAsync().ConfigureAwait(true); }
            catch (Exception ex) { Debug.WriteLine($"[Sessions] Close on exit failed: {ex}"); }
            try { TrayIcon.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[Tray] Dispose failed: {ex}"); }
        };
    }

    private WindowPlacement CaptureWindowPlacement()
    {
        bool isMaximized = AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized;

        return new WindowPlacement
        {
            X = AppWindow.Position.X,
            Y = AppWindow.Position.Y,
            Width = Math.Max(MinimumSavedWindowWidth, AppWindow.Size.Width),
            Height = Math.Max(MinimumSavedWindowHeight, AppWindow.Size.Height),
            IsMaximized = isMaximized,
        };
    }

    private void RestoreWindowPlacement(WindowPlacement placement)
    {
        if (placement.Width <= 0 || placement.Height <= 0)
        {
            return;
        }

        RectInt32 rect = NormalizeWindowRect(placement);
        AppWindow.MoveAndResize(rect);

        if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private static RectInt32 NormalizeWindowRect(WindowPlacement placement)
    {
        int width = Math.Max(MinimumSavedWindowWidth, placement.Width);
        int height = Math.Max(MinimumSavedWindowHeight, placement.Height);
        int x = placement.X;
        int y = placement.Y;

        try
        {
            DisplayArea displayArea = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
            RectInt32 workArea = displayArea.WorkArea;
            width = Math.Min(width, Math.Max(MinimumSavedWindowWidth, workArea.Width));
            height = Math.Min(height, Math.Max(MinimumSavedWindowHeight, workArea.Height));
            x = Math.Clamp(x, workArea.X, workArea.X + Math.Max(0, workArea.Width - width));
            y = Math.Clamp(y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height));
        }
        catch
        {
            // If display probing fails, the saved placement is still better
            // than dropping back to an arbitrary default.
        }

        return new RectInt32(x, y, width, height);
    }

    private void BringToFront()
    {
        AppWindow.Show();
        _ = TermNest.Core.Win32.NativeMethods.SetForegroundWindow(App.WindowHandle);
    }

    private void ApplyWindowIcon()
    {
        string? iconPath = ResolveAssetPath("AppIcon.ico");
        if (iconPath != null)
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private static string? ResolveAssetPath(string fileName)
    {
        string appBasePath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        try
        {
            string packagePath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", fileName);
            return File.Exists(packagePath) ? packagePath : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnTrayShowClick(object sender, RoutedEventArgs e) => BringToFront();

    private void OnTrayExitClick(object sender, RoutedEventArgs e) => Close();
}
