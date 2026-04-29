using System.Text.Json.Serialization;

namespace TermNest.Core.Sessions;

/// <summary>
/// Persistent session description.
///
/// Storage format is JSON (System.Text.Json) under
/// <c>%LocalAppData%\Packages\&lt;identity&gt;\LocalState\sessions.json</c>.
/// </summary>
public sealed class SessionData
{
    /// <summary>Hierarchical path: e.g. "FranchisePORTAL/FP001". Folder is everything before the last "/".</summary>
    public string SessionId { get; set; } = string.Empty;

    public string SessionName { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public ConnectionProtocol Protocol { get; set; } = ConnectionProtocol.SSH;

    public string? PuttySession { get; set; }

    public string? Username { get; set; }

    /// <summary>
    /// Connection password. Always excluded from JSON serialisation —
    /// plaintext password storage on disk was a v3 regression that v4
    /// declines to inherit. Pass via the connection bar at runtime, or
    /// store via the future credential service (Phase 6+).
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    public string? ExtraArgs { get; set; }

    public string? SpslFileName { get; set; }

    public string? Notes { get; set; }

    public string? ImageKey { get; set; }

    public string? RemotePath { get; set; }

    public string? LocalPath { get; set; }

    /// <summary>Optional working directory; only meaningful for cygterm / console children.</summary>
    public string? WorkingDirectory { get; set; }

    [JsonIgnore]
    public string FolderPath
    {
        get
        {
            int lastSlash = SessionId.LastIndexOf('/');
            return lastSlash > 0 ? SessionId[..lastSlash] : string.Empty;
        }
    }
}
