using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermNest.Core.Layouts;

/// <summary>
/// JSON-backed layout store. One file per named layout under
/// &lt;LocalState&gt;/layouts/&lt;name&gt;.json. A single
/// &lt;LocalState&gt;/active-layout file records which layout name to
/// auto-restore on launch.
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _layoutsDir;
    private readonly string _activePointerFile;

    public LayoutStore(string localStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localStateDirectory);
        _layoutsDir = Path.Combine(localStateDirectory, "layouts");
        _activePointerFile = Path.Combine(localStateDirectory, "active-layout");
        Directory.CreateDirectory(_layoutsDir);
    }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_layoutsDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_layoutsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<LayoutData?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string path = PathFor(name);
        if (!File.Exists(path)) return null;

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LayoutData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(LayoutData layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(layout.Name))
        {
            throw new ArgumentException("Layout.Name is required.", nameof(layout));
        }

        string path = PathFor(layout.Name);
        string tempPath = path + ".tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, layout, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public string? GetActiveLayoutName()
    {
        if (!File.Exists(_activePointerFile)) return null;
        try { return File.ReadAllText(_activePointerFile).Trim(); }
        catch { return null; }
    }

    public void SetActiveLayoutName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        File.WriteAllText(_activePointerFile, name);
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private string PathFor(string name)
    {
        // Layout name lands in a file path, so reject anything that could escape
        // the layouts directory. Defence in depth: invalid chars + relative path
        // segments + Windows reserved device names + a final canonical-prefix
        // check (Path.GetFullPath).
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            if (name.Contains(c))
            {
                throw new ArgumentException($"Layout name contains invalid character '{c}'.", nameof(name));
            }
        }
        if (name == "." || name == "..")
        {
            throw new ArgumentException("Layout name cannot be a relative path segment.", nameof(name));
        }
        if (ReservedDeviceNames.Contains(name))
        {
            throw new ArgumentException("Layout name is a reserved Windows device name.", nameof(name));
        }

        string candidate = Path.Combine(_layoutsDir, name + ".json");
        string canonical = Path.GetFullPath(candidate);
        string layoutsCanonical = Path.GetFullPath(_layoutsDir) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(layoutsCanonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Layout name resolved outside the layouts directory.", nameof(name));
        }
        return canonical;
    }
}
