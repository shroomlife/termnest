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

    public async Task<List<SessionData>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new List<SessionData>();
        }

        await using FileStream stream = File.OpenRead(_filePath);
        List<SessionData>? sessions = await JsonSerializer
            .DeserializeAsync<List<SessionData>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return sessions ?? new List<SessionData>();
    }

    public async Task SaveAsync(IEnumerable<SessionData> sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        // Atomic-ish replace: write to a sibling temp file, then move into place.
        string tempPath = _filePath + ".tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, sessions.ToList(), JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
