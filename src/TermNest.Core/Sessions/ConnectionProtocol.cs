namespace TermNest.Core.Sessions;

/// <summary>
/// PuTTY-supported connection protocols. Mirrors the upstream enum so v3 XML
/// imports stay forward-compatible.
/// </summary>
public enum ConnectionProtocol
{
    SSH,
    Telnet,
    Rlogin,
    Raw,
    Serial,
    Cygterm,
    Mintty,
    SSH2,
    RDP,
    VNC,
    WINCMD,
    PS // PowerShell
}
