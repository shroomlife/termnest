using System.Text;

namespace TermNest.Core.Sessions;

/// <summary>
/// Builds the command line args for putty.exe (and its console-protocol
/// siblings) from a <see cref="SessionData"/>. Mirrors the relevant parts of
/// the v3.x PuttyStartInfo, simplified for Phase 1 — only SSH/Telnet/Rlogin/
/// Raw/Serial are wired up; RDP/VNC come later when they're actually used.
/// </summary>
public sealed class PuttyStartInfo
{
    public required string Executable { get; init; }
    public required string Arguments { get; init; }
    public string? WorkingDirectory { get; init; }

    public static PuttyStartInfo Build(SessionData session, string puttyExePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(puttyExePath);

        // Console-protocol shells reuse cmd.exe / pwsh.exe, not putty.
        if (session.Protocol == ConnectionProtocol.WINCMD)
        {
            return new PuttyStartInfo
            {
                Executable = "cmd.exe",
                Arguments = "/k",
                WorkingDirectory = session.WorkingDirectory,
            };
        }
        if (session.Protocol == ConnectionProtocol.PS)
        {
            return new PuttyStartInfo
            {
                Executable = "powershell.exe",
                Arguments = string.Empty,
                WorkingDirectory = session.WorkingDirectory,
            };
        }

        StringBuilder args = new();

        // Load named PuTTY session first so its registry settings apply,
        // then override with the values from SessionData.
        if (!string.IsNullOrWhiteSpace(session.PuttySession))
        {
            args.Append("-load \"").Append(session.PuttySession).Append("\" ");
        }

        switch (session.Protocol)
        {
            case ConnectionProtocol.SSH:
            case ConnectionProtocol.SSH2:
                args.Append("-ssh ");
                break;
            case ConnectionProtocol.Telnet:
                args.Append("-telnet ");
                break;
            case ConnectionProtocol.Rlogin:
                args.Append("-rlogin ");
                break;
            case ConnectionProtocol.Raw:
                args.Append("-raw ");
                break;
            case ConnectionProtocol.Serial:
                args.Append("-serial ");
                break;
        }

        if (!string.IsNullOrWhiteSpace(session.Username))
        {
            args.Append("-l ").Append(Quote(session.Username!)).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(session.Password))
        {
            args.Append("-pw ").Append(Quote(session.Password!)).Append(' ');
        }
        if (session.Port > 0)
        {
            args.Append("-P ").Append(session.Port).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(session.ExtraArgs))
        {
            // ExtraArgs is intentionally passed through verbatim — it is the
            // user's escape hatch for putty flags we don't model. Imported
            // v3 sessions can carry shell metacharacters here; the user is
            // assumed to have authored / vetted them.
            args.Append(session.ExtraArgs).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(session.Host))
        {
            args.Append(Quote(session.Host));
        }

        return new PuttyStartInfo
        {
            Executable = puttyExePath,
            Arguments = args.ToString().TrimEnd(),
            WorkingDirectory = session.WorkingDirectory,
        };
    }

    /// <summary>
    /// Always wraps <paramref name="s"/> in double-quotes and escapes any
    /// embedded quote. Per CreateProcess command-line parsing rules, an
    /// unescaped " inside a quoted argument terminates the argument and
    /// the rest leaks as additional flags — which on Session.Password or
    /// Username would be a command-injection vector.
    /// </summary>
    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
