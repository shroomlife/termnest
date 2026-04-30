using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TermNest.Core.Sessions;

/// <summary>
/// In-process SSH terminal session backed by SSH.NET. Replaces the legacy
/// "spawn putty.exe + reparent its HWND" approach with a clean async
/// pipe model: the host pumps stdout/stderr bytes via <see cref="DataReceived"/>
/// and writes user input via <see cref="WriteAsync"/>. No external process,
/// no Win32 HWND, no NRB compositor games.
///
/// Host-key verification: a <see cref="KnownHostsStore"/> plus a
/// <see cref="HostKeyPromptDelegate"/> implement the equivalent of OpenSSH's
/// <c>StrictHostKeyChecking ask</c> policy. First-time connects prompt the
/// user with the SHA-256 fingerprint and persist on accept; subsequent
/// matches connect silently; a mismatch is rejected hard without prompting.
/// </summary>
public sealed class SshTerminalSession : IAsyncDisposable
{
    public event EventHandler<string>? DataReceived;
    public event EventHandler<bool>? Closed;
    public event EventHandler<string>? Error;

    private readonly SessionData _session;
    private readonly KnownHostsStore _knownHosts;
    private readonly HostKeyPromptDelegate _hostKeyPrompt;
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private string? _hostKeyRejection;

    public SshTerminalSession(
        SessionData session,
        KnownHostsStore knownHosts,
        HostKeyPromptDelegate hostKeyPrompt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(knownHosts);
        ArgumentNullException.ThrowIfNull(hostKeyPrompt);
        if (session.Protocol is not (ConnectionProtocol.SSH or ConnectionProtocol.SSH2))
        {
            throw new NotSupportedException($"SshTerminalSession does not handle {session.Protocol}");
        }
        _session = session;
        _knownHosts = knownHosts;
        _hostKeyPrompt = hostKeyPrompt;
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>
    /// Connects, opens an interactive shell, starts pumping data into
    /// <see cref="DataReceived"/>. Throws on auth / network / host-key failure.
    /// </summary>
    public async Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (_client != null)
        {
            throw new InvalidOperationException("Session already connected.");
        }

        ConnectionInfo info = BuildConnectionInfo();
        _client = new SshClient(info);
        _client.HostKeyReceived += OnHostKeyReceived;

        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (_hostKeyRejection != null)
        {
            // Translate the generic "connection aborted" into the actionable
            // host-key error so the UI can show the real reason.
            throw new HostKeyVerificationException(_hostKeyRejection);
        }

        // Open an interactive PTY of the requested size; xterm-256color is
        // what every modern remote expects.
        _shell = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)Math.Max(20, cols),
            rows: (uint)Math.Max(5, rows),
            width: 0,
            height: 0,
            bufferSize: 8192);

        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        // The fingerprint format mirrors OpenSSH's `ssh-keygen -lf` output:
        // SHA-256 of the public-key DER, base64-encoded, no padding.
        string fingerprint = e.FingerPrintSHA256;
        string host = _session.Host;
        int port = _session.Port > 0 ? _session.Port : 22;
        string algorithm = e.HostKeyName ?? "(unknown)";

        string? known = _knownHosts.Lookup(host, port);
        if (known == null)
        {
            // First-time connect to this host. Ask the user; persist on yes.
            HostKeyPrompt prompt = new()
            {
                Host = host,
                Port = port,
                FingerprintSha256 = fingerprint,
                Algorithm = algorithm,
                IsKeyChange = false,
            };
            bool accepted = _hostKeyPrompt(prompt).GetAwaiter().GetResult();
            if (accepted)
            {
                _knownHosts.Save(host, port, fingerprint, algorithm);
                e.CanTrust = true;
            }
            else
            {
                _hostKeyRejection = $"Host key for {host}:{port} was not accepted.";
                e.CanTrust = false;
            }
            return;
        }

        if (string.Equals(known, fingerprint, StringComparison.Ordinal))
        {
            // Match — silent accept.
            e.CanTrust = true;
            return;
        }

        // Mismatch. Refuse without prompting. The user must explicitly remove
        // the old entry via UI before re-pinning. This is intentional —
        // prompting on mismatch trains people to click through MITM warnings.
        _hostKeyRejection =
            $"Host key for {host}:{port} has CHANGED.\n" +
            $"Expected: {known}\nReceived: {fingerprint}\n" +
            $"This could indicate a man-in-the-middle attack. " +
            $"If the change is legitimate, remove the entry from known_hosts.json and reconnect.";
        e.CanTrust = false;
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        string username = string.IsNullOrWhiteSpace(_session.Username) ? "root" : _session.Username!;
        int port = _session.Port > 0 ? _session.Port : 22;

        // Phase 1: password auth only. A None auth method will hang against
        // any real server — require an actual password upfront via the
        // PromptForPassword dialog the host shows before ConnectAsync.
        if (string.IsNullOrEmpty(_session.Password))
        {
            throw new InvalidOperationException("Password required for SSH connection.");
        }
        AuthenticationMethod auth = new PasswordAuthenticationMethod(username, _session.Password);

        return new ConnectionInfo(_session.Host, port, username, auth)
        {
            // Fail fast instead of hanging on a black-holed network.
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_shell == null) return;

        byte[] buffer = new byte[4096];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _shell.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                int charCount = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (charCount > 0)
                {
                    DataReceived?.Invoke(this, new string(chars, 0, charCount));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Dispose
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            int charCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            if (charCount > 0)
            {
                DataReceived?.Invoke(this, new string(chars, 0, charCount));
            }
            Closed?.Invoke(this, !cancellationToken.IsCancellationRequested);
        }
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_shell == null) return Task.CompletedTask;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return _shell.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    public void Resize(int cols, int rows)
    {
        // SSH.NET's public ShellStream API doesn't expose mid-stream
        // SendWindowChangeRequest. We open the shell at the initial size and
        // accept a static PTY for now — most modern servers handle SIGWINCH
        // via cooperative re-detection. Full resize support lands when we
        // move to ConPTY-based renderer in 4.x.
        _ = cols; _ = rows;
    }

    public async ValueTask DisposeAsync()
    {
        try { _readLoopCts?.Cancel(); }
        catch (Exception ex) { TermNest.Core.Diagnostics.DebugLog.Write("SshSession", $"cancel failed: {ex.Message}"); }

        // Wait for the read loop to actually stop. Without this, _shell.Dispose
        // can race the in-flight ReadAsync and surface as an
        // ObjectDisposedException in TaskScheduler.UnobservedTaskException.
        if (_readLoopTask != null)
        {
            try { await _readLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { TermNest.Core.Diagnostics.DebugLog.Write("SshSession", $"read loop end: {ex.Message}"); }
        }
        _readLoopCts?.Dispose();
        _readLoopCts = null;
        _readLoopTask = null;

        if (_shell != null)
        {
            try { _shell.Close(); }
            catch (Exception ex) { TermNest.Core.Diagnostics.DebugLog.Write("SshSession", $"shell close: {ex.Message}"); }
            _shell.Dispose();
            _shell = null;
        }
        if (_client != null)
        {
            _client.HostKeyReceived -= OnHostKeyReceived;
            try { _client.Disconnect(); }
            catch (Exception ex) { TermNest.Core.Diagnostics.DebugLog.Write("SshSession", $"client disconnect: {ex.Message}"); }
            _client.Dispose();
            _client = null;
        }
    }
}

/// <summary>
/// Asynchronous prompt the host UI implements to ask the user whether to
/// trust an unknown SSH host key. Returning <c>true</c> persists the
/// fingerprint and proceeds with the connect; <c>false</c> aborts.
///
/// The delegate is invoked on a background SSH.NET thread, so any UI work
/// must marshal to the UI dispatcher on its own.
/// </summary>
public delegate Task<bool> HostKeyPromptDelegate(HostKeyPrompt prompt);

public sealed class HostKeyPrompt
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string FingerprintSha256 { get; init; }
    public required string Algorithm { get; init; }

    /// <summary>
    /// True when the host already had a different fingerprint pinned. Reserved
    /// for future "I really meant to re-pin" UX; currently the policy refuses
    /// mismatches without prompting, so this stays false in 1.0.
    /// </summary>
    public bool IsKeyChange { get; init; }
}

/// <summary>
/// Thrown by <see cref="SshTerminalSession.ConnectAsync"/> when the host
/// key was rejected (either by the user on first connect, or automatically
/// on a fingerprint mismatch). The message is suitable for direct UI
/// surfacing — it explains what went wrong and what the user can do.
/// </summary>
public sealed class HostKeyVerificationException : Exception
{
    public HostKeyVerificationException(string message) : base(message) { }
}
