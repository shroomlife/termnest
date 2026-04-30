using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TermNest.Core.Sessions;

/// <summary>
/// Terminal session backed by Windows ConPTY. This is the correct integration
/// path for local shells and OpenSSH: the WinUI app owns the terminal surface
/// and ConPTY provides UTF-8 text plus virtual terminal sequences.
/// </summary>
public sealed class ConsoleTerminalSession : IAsyncDisposable
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromMilliseconds(500);

    public event EventHandler<string>? DataReceived;
    public event EventHandler<bool>? Closed;
    public event EventHandler<string>? Error;

    private readonly SessionData _session;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _input;
    private FileStream? _output;
    private IntPtr _pseudoConsole;
    private Process? _process;
    private CancellationTokenSource? _readLoopCts;
    private volatile bool _isClosing;
    private int _closedRaised;

    public ConsoleTerminalSession(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Protocol is not (ConnectionProtocol.WINCMD or ConnectionProtocol.PS or ConnectionProtocol.SSH or ConnectionProtocol.SSH2))
        {
            throw new NotSupportedException($"ConsoleTerminalSession does not handle {session.Protocol}");
        }
        _session = session;
    }

    public bool IsRunning => _process is { HasExited: false };

    public Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY console sessions require Windows.");
        }
        if (_process != null)
        {
            throw new InvalidOperationException("Session already connected.");
        }

        CreatePseudoConsoleSession(cols, rows);
        _output = new FileStream(_outputRead!, FileAccess.Read, bufferSize: 4096, isAsync: false);
        _input = new FileStream(_inputWrite!, FileAccess.Write, bufferSize: 4096, isAsync: false);

        _readLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
        return Task.CompletedTask;
    }

    private void CreatePseudoConsoleSession(int cols, int rows)
    {
        CreatePipe(out SafeFileHandle inputRead, out _inputWrite);
        CreatePipe(out _outputRead, out SafeFileHandle outputWrite);

        try
        {
            COORD size = new()
            {
                X = (short)Math.Clamp(cols, 20, short.MaxValue),
                Y = (short)Math.Clamp(rows, 5, short.MaxValue),
            };
            int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            inputRead.Dispose();
            outputWrite.Dispose();
        }

        STARTUPINFOEX startupInfo = new();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeListSize = IntPtr.Zero;
        try
        {
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            startupInfo.lpAttributeList = attributeList;
            string commandLine = BuildCommandLine(_session);
            string? workingDirectory = Directory.Exists(_session.WorkingDirectory)
                ? _session.WorkingDirectory
                : null;

            bool created = CreateProcess(
                null,
                new StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out PROCESS_INFORMATION processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            _process = Process.GetProcessById(processInfo.dwProcessId);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => RaiseClosed(!_isClosing);
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private static string BuildCommandLine(SessionData session)
    {
        return session.Protocol switch
        {
            ConnectionProtocol.WINCMD => "cmd.exe /k",
            ConnectionProtocol.PS => "powershell.exe -NoExit -NoLogo",
            ConnectionProtocol.SSH or ConnectionProtocol.SSH2 => BuildSshCommandLine(session),
            _ => throw new NotSupportedException($"Unsupported terminal protocol {session.Protocol}"),
        };
    }

    private static string BuildSshCommandLine(SessionData session)
    {
        if (string.IsNullOrWhiteSpace(session.Host))
        {
            throw new InvalidOperationException("SSH host is required.");
        }

        int port = session.Port > 0 ? session.Port : 22;
        string sshPath = ResolveSshPath();
        StringBuilder commandLine = new();
        commandLine.Append(Quote(sshPath));
        commandLine.Append(" -tt");
        commandLine.Append(" -o ServerAliveInterval=30");
        commandLine.Append(" -p ").Append(port);
        if (!string.IsNullOrWhiteSpace(session.Username))
        {
            commandLine.Append(" -l ").Append(Quote(session.Username!));
        }
        commandLine.Append(' ').Append(Quote(session.Host));
        return commandLine.ToString();
    }

    private static string ResolveSshPath()
    {
        string systemSsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "OpenSSH",
            "ssh.exe");
        return File.Exists(systemSsh) ? systemSsh : "ssh.exe";
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static void CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe)
    {
        if (!CreatePipe(out readPipe, out writePipe, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// True when the read loop hit a clean EOF on stdout — i.e. the child
    /// process closed its end naturally (user typed <c>exit</c>, ssh.exe
    /// finished gracefully). Distinguishes that case from a real error in
    /// the read pipeline so the UI doesn't shout "Connection lost" on a
    /// normal shell exit.
    /// </summary>
    private bool _sawCleanEof;

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_output == null) return;

        byte[] buffer = new byte[4096];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _sawCleanEof = true;
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
            // Expected during CloseAsync / DisposeAsync.
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
            // "Unexpected" should mean the read pipeline broke before EOF.
            // A clean EOF (user typed `exit`, ssh.exe finished) or an
            // explicit close are both expected shutdowns — the UI shouldn't
            // surface "Connection lost" in those cases.
            bool unexpected = !_isClosing && !_sawCleanEof;
            RaiseClosed(unexpected);
        }
    }

    private void RaiseClosed(bool unexpected)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0)
        {
            return;
        }
        Closed?.Invoke(this, unexpected);
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_input == null) return;

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int cols, int rows)
    {
        if (_pseudoConsole == IntPtr.Zero) return;

        COORD size = new()
        {
            X = (short)Math.Clamp(cols, 20, short.MaxValue),
            Y = (short)Math.Clamp(rows, 5, short.MaxValue),
        };
        _ = ResizePseudoConsole(_pseudoConsole, size);
    }

    public async ValueTask DisposeAsync()
    {
        _isClosing = true;

        try { _readLoopCts?.Cancel(); } catch { }
        _readLoopCts?.Dispose();
        _readLoopCts = null;

        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    await WriteAsync("exit\r").ConfigureAwait(false);
                    using CancellationTokenSource cts = new(GracefulCloseTimeout);
                    try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* fall through to Kill */ }
                }
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                process.Dispose();
                _process = null;
            }
        }

        _input?.Dispose();
        _input = null;
        _output?.Dispose();
        _output = null;
        _inputWrite?.Dispose();
        _inputWrite = null;
        _outputRead?.Dispose();
        _outputRead = null;

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);
}
