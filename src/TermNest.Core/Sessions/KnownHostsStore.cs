using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermNest.Core.Sessions;

/// <summary>
/// Persistent record of SSH server host keys the user has accepted, modeled
/// after the OpenSSH <c>~/.ssh/known_hosts</c> idea. We store the SHA-256
/// fingerprint (base64, as SSH.NET emits it) keyed by host+port.
///
/// Why this exists: without host-key pinning, every connect blindly trusts
/// whatever public key the server presents — i.e. a passive MITM with TCP
/// reach can capture every keystroke including the password. This store is
/// the persistence side of the prompt-on-first-connect / refuse-on-mismatch
/// flow implemented by <see cref="SshTerminalSession"/>.
///
/// On-disk format: <c>&lt;LocalState&gt;/known_hosts.json</c>, atomic write
/// via temp+rename like the other stores.
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private List<KnownHostEntry>? _cache;

    public KnownHostsStore(string localStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localStateDirectory);
        _filePath = Path.Combine(localStateDirectory, "known_hosts.json");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Returns the previously accepted SHA-256 fingerprint for this host+port,
    /// or <c>null</c> if the user hasn't seen this host before.
    /// </summary>
    public string? Lookup(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _cache!
                .FirstOrDefault(e =>
                    string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    e.Port == port)
                ?.FingerprintSha256;
        }
    }

    /// <summary>
    /// Persists (or replaces) the accepted fingerprint for this host+port.
    /// </summary>
    public void Save(string host, int port, string fingerprintSha256, string? algorithm = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintSha256);
        lock (_gate)
        {
            EnsureLoadedLocked();
            _cache!.RemoveAll(e =>
                string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) &&
                e.Port == port);
            _cache.Add(new KnownHostEntry
            {
                Host = host,
                Port = port,
                FingerprintSha256 = fingerprintSha256,
                Algorithm = algorithm,
                AddedUtc = DateTimeOffset.UtcNow,
            });
            PersistLocked();
        }
    }

    /// <summary>
    /// Forgets a previously accepted host so the next connect prompts again.
    /// </summary>
    public void Remove(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        lock (_gate)
        {
            EnsureLoadedLocked();
            int removed = _cache!.RemoveAll(e =>
                string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) &&
                e.Port == port);
            if (removed > 0)
            {
                PersistLocked();
            }
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_cache != null) return;

        if (!File.Exists(_filePath))
        {
            _cache = new List<KnownHostEntry>();
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<KnownHostEntry>>(json, JsonOptions)
                ?? new List<KnownHostEntry>();
        }
        catch (Exception)
        {
            // Corrupted file: start fresh rather than crashing the connect path.
            // The user will be prompted again, which is the safe default.
            _cache = new List<KnownHostEntry>();
        }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        string json = JsonSerializer.Serialize(_cache, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}

public sealed class KnownHostEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string FingerprintSha256 { get; set; } = string.Empty;
    public string? Algorithm { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
}
