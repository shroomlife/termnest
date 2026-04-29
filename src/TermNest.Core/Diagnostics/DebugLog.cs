using System.Text;

namespace TermNest.Core.Diagnostics;

/// <summary>
/// Lightweight file logger for the embedded-window diagnostics + everything
/// else that needs visibility post-install. Writes to
/// <c>&lt;LocalState&gt;/debug.log</c>; rotates at 1 MB to a single .old file.
/// Thread-safe.
/// </summary>
public static class DebugLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly Lock _gate = new();
    private static string? _path;
    private static StreamWriter? _writer;

    public static void Configure(string localStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localStateDirectory);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(localStateDirectory);
                _path = Path.Combine(localStateDirectory, "debug.log");
                RotateIfNeeded();
                _writer?.Dispose();
                _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                WriteLineUnsynced($"--- session start {DateTime.Now:O} pid={Environment.ProcessId} ---");
            }
            catch
            {
                // Logging is best-effort; never crash the app for it.
                _writer = null;
            }
        }
    }

    public static void Write(string category, string message)
    {
        lock (_gate)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[{category}] {message}");
                if (_writer == null) return;
                WriteLineUnsynced($"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}");
            }
            catch { /* best effort */ }
        }
    }

    private static void WriteLineUnsynced(string line)
    {
        _writer?.WriteLine(line);
    }

    private static void RotateIfNeeded()
    {
        if (_path == null) return;
        FileInfo info = new(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        string archive = _path + ".old";
        try { if (File.Exists(archive)) File.Delete(archive); } catch { }
        try { File.Move(_path, archive, overwrite: true); } catch { }
    }
}
