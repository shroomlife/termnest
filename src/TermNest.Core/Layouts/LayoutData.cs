namespace TermNest.Core.Layouts;

/// <summary>
/// Persisted shell layout — captures the side-rail width, bottom-strip
/// visibility, and which session ids should auto-restore on launch. Phase 4
/// trades the v3 detachable-dock model (DockPanelSuite XML) for a fixed
/// shell whose configurable points are these few measures plus the open-
/// session list.
/// </summary>
public sealed class LayoutData
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Width of the left rail (sessions tree) in DIPs.</summary>
    public double SideRailWidth { get; set; } = 320;

    /// <summary>Whether the optional bottom strip (status / log) is visible.</summary>
    public bool BottomStripVisible { get; set; } = false;

    /// <summary>Height of the bottom strip in DIPs.</summary>
    public double BottomStripHeight { get; set; } = 200;

    /// <summary>Window placement (saved on close, restored on launch).</summary>
    public WindowPlacement WindowPlacement { get; set; } = new();

    /// <summary>Session IDs that should reopen automatically when this layout loads.</summary>
    public List<string> OpenSessionIds { get; set; } = new();

    /// <summary>
    /// Expanded folder paths in the session tree. Null means first-run/default
    /// behavior where folders open expanded; an empty list means all folders
    /// were intentionally collapsed.
    /// </summary>
    public List<string>? ExpandedSessionFolderPaths { get; set; }

    public bool IsDefault { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 900;
    public bool IsMaximized { get; set; }
}
