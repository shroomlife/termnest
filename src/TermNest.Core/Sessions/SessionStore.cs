using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermNest.Core.Sessions;

/// <summary>
/// JSON-backed session store. Persists in a single
/// <c>sessions.json</c> file under the directory passed to the constructor
/// (typically <c>ApplicationData.Current.LocalFolder.Path</c>).
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    public SessionStore(string localStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localStateDirectory);
        _filePath = Path.Combine(localStateDirectory, "sessions.json");
    }

    public string FilePath => _filePath;

    public bool Exists => File.Exists(_filePath);

    /// <summary>
    /// Loads the persisted state. Falls back gracefully when the file is
    /// missing, empty, or written in the legacy plain-array shape (early
    /// development builds before empty-folder support).
    /// </summary>
    public async Task<SessionStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return SessionStoreSnapshot.Empty;
        }

        await using FileStream stream = File.OpenRead(_filePath);
        using JsonDocument doc = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Two on-disk shapes are accepted:
        //   1. New: { "sessions": [...], "folders": ["A", "A/B"] }
        //   2. Legacy: [ ... session objects ... ]
        // Reading both keeps stores written by earlier dev builds usable.
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            List<SessionData> legacy = JsonSerializer.Deserialize<List<SessionData>>(
                doc.RootElement.GetRawText(), JsonOptions) ?? new List<SessionData>();
            return new SessionStoreSnapshot(legacy, new List<string>());
        }

        SessionStoreSnapshot? parsed = JsonSerializer.Deserialize<SessionStoreSnapshot>(
            doc.RootElement.GetRawText(), JsonOptions);
        return parsed ?? SessionStoreSnapshot.Empty;
    }

    public async Task SaveAsync(
        IEnumerable<SessionData> sessions,
        IEnumerable<string>? emptyFolders = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        SessionStoreSnapshot snapshot = new(
            sessions.ToList(),
            emptyFolders?.ToList() ?? new List<string>());

        // Atomic-ish replace: write to a sibling temp file, then move into place.
        string tempPath = _filePath + ".tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }
}

/// <summary>
/// On-disk shape of <c>sessions.json</c>. <see cref="EmptyFolders"/> tracks
/// folders that have no sessions yet — without it they would vanish on the
/// next reload because the tree is reconstructed from session paths.
/// </summary>
public sealed class SessionStoreSnapshot
{
    public List<SessionData> Sessions { get; set; } = new();
    public List<string> EmptyFolders { get; set; } = new();

    public SessionStoreSnapshot() { }

    public SessionStoreSnapshot(List<SessionData> sessions, List<string> emptyFolders)
    {
        Sessions = sessions;
        EmptyFolders = emptyFolders;
    }

    public static SessionStoreSnapshot Empty { get; } = new(new List<SessionData>(), new List<string>());
}
