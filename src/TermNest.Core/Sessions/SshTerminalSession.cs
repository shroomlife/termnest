using System.Text;
using Renci.SshNet;

namespace TermNest.Core.Sessions;

/// <summary>
/// In-process SSH terminal session backed by SSH.NET. Replaces the legacy
/// "spawn putty.exe + reparent its HWND" approach with a clean async
/// pipe model: the host pumps stdout/stderr bytes via <see cref="DataReceived"/>
/// and writes user input via <see cref="WriteAsync"/>. No external process,
/// no Win32 HWND, no NRB compositor games.
/// </summary>
public sealed class SshTerminalSession : IAsyncDisposable
{
    public event EventHandler<string>? DataReceived;
    public event EventHandler<bool>? Closed;
    public event EventHandler<string>? Error;

    private readonly SessionData _session;
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readLoopCts;

    public SshTerminalSession(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Protocol is not (ConnectionProtocol.SSH or ConnectionProtocol.SSH2))
        {
            throw new NotSupportedException($"SshTerminalSession does not handle {session.Protocol}");
        }
        _session = session;
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>
    /// Connects, opens an interactive shell, starts pumping data into
    /// <see cref="DataReceived"/>. Throws on auth / network failure.
    /// </summary>
    public async Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (_client != null)
        {
            throw new InvalidOperationException("Session already connected.");
        }

        ConnectionInfo info = BuildConnectionInfo();
        _client = new SshClient(info);
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

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
        _ = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
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
        try { _readLoopCts?.Cancel(); } catch { }
        _readLoopCts?.Dispose();

        if (_shell != null)
        {
            try { _shell.Close(); } catch { }
            _shell.Dispose();
            _shell = null;
        }
        if (_client != null)
        {
            try { _client.Disconnect(); } catch { }
            _client.Dispose();
            _client = null;
        }
        await Task.CompletedTask;
    }
}
