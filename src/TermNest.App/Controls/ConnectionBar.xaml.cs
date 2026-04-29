using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TermNest.Core.Sessions;

namespace TermNest.App.Controls;

/// <summary>
/// Top-of-window quick-connect bar. Mirrors the v3 tsConnect toolbar but
/// uses native WinUI 3 controls (ComboBox, TextBox, PasswordBox, NumberBox).
/// Raises <see cref="ConnectRequested"/> with a fully-populated SessionData
/// when the user clicks Connect.
/// </summary>
public sealed partial class ConnectionBar : UserControl
{
    public event EventHandler<SessionData>? ConnectRequested;

    public ConnectionBar()
    {
        InitializeComponent();
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        string host = HostBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(host))
        {
            HostBox.Focus(FocusState.Programmatic);
            return;
        }

        string protocolTag = (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "SSH";
        ConnectionProtocol protocol = Enum.TryParse(protocolTag, out ConnectionProtocol parsed) ? parsed : ConnectionProtocol.SSH;

        string user = UserBox.Text?.Trim() ?? string.Empty;
        // NumberBox.Value is double; clamp + round explicitly so a fat-fingered
        // "22.9" doesn't silently truncate, and so a NaN (empty box) falls back.
        int port = double.IsFinite(PortBox.Value) ? (int)Math.Round(PortBox.Value) : 0;
        if (port <= 0)
        {
            port = protocol switch
            {
                ConnectionProtocol.Telnet => 23,
                ConnectionProtocol.Rlogin => 513,
                ConnectionProtocol.Serial => 0,
                _ => 22,
            };
        }

        SessionData session = new()
        {
            SessionId = $"adhoc/{host}",
            SessionName = string.IsNullOrEmpty(user) ? host : $"{user}@{host}",
            Host = host,
            Port = port,
            Protocol = protocol,
            Username = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password,
        };

        ConnectRequested?.Invoke(this, session);
    }
}
