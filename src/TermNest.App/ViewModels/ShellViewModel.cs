using CommunityToolkit.Mvvm.ComponentModel;

namespace TermNest.App.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string PuttyExePath { get; set; } = ResolveDefaultPuttyPath();

    [ObservableProperty]
    public partial string WinScpExePath { get; set; } = ResolveDefaultWinScpPath();

    [ObservableProperty]
    public partial double TerminalFontSize { get; set; } = 15;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready.";

    [ObservableProperty]
    public partial string LayoutDisplayName { get; set; } = "Layout: default";

    private static string ResolveDefaultPuttyPath()
    {
        string[] candidates =
        {
            @"C:\Program Files\PuTTY\putty.exe",
            @"C:\Program Files (x86)\PuTTY\putty.exe",
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveDefaultWinScpPath()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\WinSCP\WinSCP.exe",
            @"C:\Program Files\WinSCP\WinSCP.exe",
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
