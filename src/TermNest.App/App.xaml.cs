using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TermNest.Core.Diagnostics;
using LaunchArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using ActivationArgs = Microsoft.Windows.AppLifecycle.AppActivationArguments;

namespace TermNest.App;

/// <summary>
/// Application entry point. Hosts MainWindow and runs the single-instance
/// guard via Microsoft.Windows.AppLifecycle.AppInstance — the second launch
/// of the same Identity Name redirects its activation to the existing
/// process, which then brings its window forward.
///
/// Reference: https://learn.microsoft.com/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing
/// </summary>
public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        // Default RequestedTheme is "Default" (system-driven). Leave it that
        // way so light/dark/auto sync follows Windows Settings without any
        // extra code on our side. Mica handles tinting per system theme too.
        InitializeComponent();

        // Last-resort crash logging — anything that escapes a catch lands in
        // <LocalState>/debug.log so we can diagnose post-mortem.
        UnhandledException += (_, e) =>
        {
            DebugLog.Write("App", $"UNHANDLED: {e.Exception}");
            // Don't set e.Handled — let the runtime handle the crash so we
            // see the WER dialog and dump.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            DebugLog.Write("App", $"DOMAIN UNHANDLED ({e.IsTerminating}): {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DebugLog.Write("App", $"TASK UNOBSERVED: {e.Exception}");
        };
    }

    protected override async void OnLaunched(LaunchArgs args)
    {
        // Wire up the file logger before anything else so EmbeddedPuttyHost
        // diagnostics land in <LocalState>/debug.log even before the shell
        // has loaded.
        try
        {
            string localState = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            DebugLog.Configure(localState);
            DebugLog.Write("App", $"OnLaunched, kind={args.UWPLaunchActivatedEventArgs?.Kind}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] DebugLog setup failed: {ex.Message}");
        }

        // Single-instance: register/find a key. If we're not the primary, hand
        // foreground rights to the primary and redirect the activation, then
        // exit cleanly. Per docs the canonical place is Program.Main before
        // Application.Start; doing it in OnLaunched costs one extra process
        // spawn vs. the early exit pattern but works for our project shape.
        AppInstance keyed = AppInstance.FindOrRegisterForKey("TermNest-4");
        if (!keyed.IsCurrent)
        {
            // Without AllowSetForegroundWindow the primary's later
            // SetForegroundWindow silently fails under Win32 foreground-lock
            // because we — the second instance — own the foreground at this
            // moment. Grant the primary process the one-shot right.
            _ = TermNest.Core.Win32.NativeMethods.AllowSetForegroundWindow(keyed.ProcessId);

            ActivationArgs activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            await keyed.RedirectActivationToAsync(activationArgs);

            // Environment.Exit allows the runtime host to tear down the
            // WinRT/COM apartment cleanly. Process.Kill skips that and shows
            // up as a crash-class telemetry event in MSIX-packaged builds.
            Environment.Exit(0);
            return;
        }

        // DispatcherQueue must be set BEFORE Activated subscription so a fast
        // redirect doesn't observe a null queue.
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        keyed.Activated += OnRedirectedActivation;

        Window = new MainWindow();
        Window.Activate();
    }

    private void OnRedirectedActivation(object? sender, ActivationArgs e)
    {
        // A second instance handed off to us — bring our window to the front.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (Window is { } window)
            {
                window.AppWindow.Show();
                _ = TermNest.Core.Win32.NativeMethods.SetForegroundWindow(WindowHandle);
            }
        });
    }
}
