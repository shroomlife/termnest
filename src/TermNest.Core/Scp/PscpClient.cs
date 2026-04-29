using System.Diagnostics;
using System.Text;
using TermNest.Core.Sessions;

namespace TermNest.Core.Scp;

/// <summary>
/// Thin async wrapper around <c>pscp.exe</c> for non-interactive transfers.
/// Phase 5 covers single-file upload + download; the v3 dual-pane browser
/// arrives in 4.1 once the transfer pipeline has matured. Captures pscp's
/// stdout/stderr as raw text — parsing the progress percentages can layer
/// on top later.
/// </summary>
public sealed class PscpClient
{
    public required string PscpExePath { get; init; }
    public required SessionData Session { get; init; }

    public async Task<PscpResult> UploadAsync(string localPath, string remotePath, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        string args = BuildArgs(Quote(localPath) + " " + Quote(BuildRemoteSpec(remotePath)));
        return await RunAsync(args, log, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PscpResult> DownloadAsync(string remotePath, string localPath, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        string args = BuildArgs(Quote(BuildRemoteSpec(remotePath)) + " " + Quote(localPath));
        return await RunAsync(args, log, cancellationToken).ConfigureAwait(false);
    }

    private string BuildArgs(string trailing)
    {
        StringBuilder sb = new();
        sb.Append("-batch ");
        if (Session.Port > 0) sb.Append("-P ").Append(Session.Port).Append(' ');
        if (!string.IsNullOrWhiteSpace(Session.PuttySession))
        {
            sb.Append("-load ").Append(Quote(Session.PuttySession!)).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(Session.Password))
        {
            // SECURITY: -pw exposes the password in the process argument list,
            // visible to any process with the same integrity level via
            // NtQueryInformationProcess / Get-Process. Inherent pscp limit;
            // a credential-service path lands in Phase 6+.
            sb.Append("-pw ").Append(Quote(Session.Password!)).Append(' ');
        }
        sb.Append(trailing);
        return sb.ToString();
    }

    private string BuildRemoteSpec(string remotePath)
    {
        string user = string.IsNullOrEmpty(Session.Username) ? string.Empty : Session.Username + "@";
        return $"{user}{Session.Host}:{remotePath}";
    }

    /// <summary>
    /// Always wraps <paramref name="s"/> in double-quotes and escapes any
    /// embedded quotes. Per Microsoft's CreateProcess command-line parsing
    /// rules, an unescaped " inside a quoted argument terminates the
    /// argument and lets the rest leak as additional flags — which on
    /// Session.Password would be an injection. Quote unconditionally.
    /// </summary>
    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private async Task<PscpResult> RunAsync(string args, IProgress<string>? log, CancellationToken cancellationToken)
    {
        if (!File.Exists(PscpExePath))
        {
            throw new FileNotFoundException("pscp.exe not found", PscpExePath);
        }

        ProcessStartInfo psi = new()
        {
            FileName = PscpExePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using Process process = new() { StartInfo = psi, EnableRaisingEvents = true };
        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            log?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            log?.Report(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start pscp.exe");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync only cancels the wait, not the child. Kill the
            // pscp tree so cancellation actually stops the transfer.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }

        return new PscpResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }
}

public sealed class PscpResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool IsSuccess => ExitCode == 0;
}
