using System.Windows;
using System.Windows.Controls;
using YfgDesk.Client.ViewModels;

namespace YfgDesk.Client.Views;

/// <summary>
/// Host-role panel code-behind. Wires "Share screen" / "Accept" / "Reject"
/// buttons to the <see cref="HostViewModel"/> and the global app state
/// (<see cref="YfgDesk.Client.App.Consent"/>, <see cref="YfgDesk.Client.App.Signaling"/>).
/// </summary>
public partial class HostView : UserControl
{
    public HostView()
    {
        InitializeComponent();
    }

    private HostViewModel Vm => (HostViewModel)DataContext;

    private void OnShareScreen(object sender, RoutedEventArgs e)
    {
        Vm.IsSharing = true;
        // Send hostReady envelope via the signaling client (if connected).
        if (App.Signaling is not null)
        {
            _ = App.Signaling.SendAsync(new YfgDesk.Shared.Protocol.Envelope
            {
                Kind = YfgDesk.Shared.Protocol.MessageKinds.HostReady,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            });
        }
    }

    private void OnEmergencyStop(object sender, RoutedEventArgs e)
    {
        Vm.EmergencyStop();
        if (App.Signaling is not null)
        {
            _ = App.Signaling.CloseAsync("emergency_stop");
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Vm.Accept(nowMs))
        {
            if (App.Signaling is not null)
            {
                _ = App.Signaling.SendAsync(new YfgDesk.Shared.Protocol.Envelope
                {
                    Kind = YfgDesk.Shared.Protocol.MessageKinds.ConsentDecision,
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                        new YfgDesk.Shared.Protocol.Payloads.ConsentDecisionPayload(true, Vm.GrantedPermissions)),
                });
            }
        }
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        if (Vm.Reject())
        {
            if (App.Signaling is not null)
            {
                _ = App.Signaling.SendAsync(new YfgDesk.Shared.Protocol.Envelope
                {
                    Kind = YfgDesk.Shared.Protocol.MessageKinds.ConsentDecision,
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                        new YfgDesk.Shared.Protocol.Payloads.ConsentDecisionPayload(false, 0)),
                });
            }
        }
    }
}
