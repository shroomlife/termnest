using System.Globalization;

namespace TermNest.Core.Scripting;

/// <summary>
/// TermNest Scripting Language parser. v4 supports a tightened subset of
/// the v3 SPSL grammar:
///
///   SLEEP &lt;ms&gt;
///   SENDLINE &lt;text&gt;
///   SENDCHAR &lt;text&gt;
///   SENDKEY  &lt;keyname&gt;[+&lt;keyname&gt;...]
///
/// Lines starting with '#' are comments. Empty lines are skipped. Anything
/// else throws <see cref="FormatException"/>.
/// </summary>
public static class Spsl
{
    /// <summary>Maximum SLEEP duration in milliseconds. 60s is more than any
    /// reasonable interactive script needs and prevents pathological values.</summary>
    public const int MaxSleepMs = 60_000;

    public static IReadOnlyList<CommandData> Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        List<CommandData> result = new();

        foreach (string raw in script.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int sp = line.IndexOf(' ');
            string verb = sp < 0 ? line : line[..sp];
            string arg = sp < 0 ? string.Empty : line[(sp + 1)..];

            CommandData cmd = verb.ToUpperInvariant() switch
            {
                "SLEEP"    => new CommandData { Delay = ParseSleep(arg) },
                "SENDLINE" => new CommandData { Command = arg + "\n" },
                "SENDCHAR" => new CommandData { Command = arg },
                "SENDKEY"  => new CommandData { KeyCombo = ParseKeyCombo(arg) },
                _ => throw new FormatException($"Unknown SPSL verb on line: {line}")
            };

            result.Add(cmd);
        }

        return result;
    }

    private static TimeSpan ParseSleep(string arg)
    {
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
        {
            throw new FormatException($"SLEEP argument must be an integer: '{arg}'");
        }
        if (ms < 0)
        {
            throw new FormatException($"SLEEP argument cannot be negative: {ms}");
        }
        if (ms > MaxSleepMs)
        {
            throw new FormatException($"SLEEP argument exceeds maximum {MaxSleepMs}ms: {ms}");
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    private static KeyCombo ParseKeyCombo(string spec)
    {
        bool ctrl = false, shift = false, alt = false;
        int? key = null;

        foreach (string part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":  case "CONTROL": ctrl = true; break;
                case "SHIFT": shift = true; break;
                case "ALT": alt = true; break;
                default:
                    key = ResolveVirtualKey(part);
                    break;
            }
        }
        if (key == null)
        {
            throw new FormatException($"SENDKEY argument has no key: '{spec}'");
        }
        return new KeyCombo { VirtualKey = key.Value, Control = ctrl, Shift = shift, Alt = alt };
    }

    /// <summary>
    /// Maps a key name (e.g. ENTER, F1, A, ESC) to a Win32 virtual-key code.
    /// Single-character keys map directly via uppercase ASCII.
    /// </summary>
    private static int ResolveVirtualKey(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return c;
            }
        }

        return name.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB"      => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE"    => 0x20,
            "BACKSPACE" or "BS" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "LEFT"     => 0x25,
            "UP"       => 0x26,
            "RIGHT"    => 0x27,
            "DOWN"     => 0x28,
            "HOME"     => 0x24,
            "END"      => 0x23,
            "PAGEUP"   => 0x21,
            "PAGEDOWN" => 0x22,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => throw new FormatException($"Unknown key name: '{name}'")
        };
    }
}
