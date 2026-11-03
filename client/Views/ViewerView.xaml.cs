using System.Windows;
using System.Windows.Controls;
using YfgDesk.Client.ViewModels;

namespace YfgDesk.Client.Views;

/// <summary>
/// Viewer-role panel code-behind. Wires the "Request" button to the
/// <see cref="ConnectionOrchestrator"/> (via <see cref="App.Orchestrator"/>).
/// </summary>
public partial class ViewerView : UserControl
{
    public ViewerView()
    {
        InitializeComponent();
    }

    private ViewerViewModel Vm => (ViewerViewModel)DataContext;

    /// <summary>Run the three-tier connection orchestrator for the target device ID.</summary>
    private async void OnRequest(object sender, RoutedEventArgs e)
    {
        var targetId = Vm.GetSanitizedTargetId();
        if (string.IsNullOrEmpty(targetId)) return;

        Vm.IsConnecting = true;
        Vm.StatusText = "در حال اتصال…";
        try
        {
            if (App.Orchestrator is null)
            {
                Vm.StatusText = "سرویس راه‌اندازی نشده است.";
                return;
            }

            // Record the target in the recent-id cache.
            App.Cache?.RecordRecent(targetId);

            var result = await App.Orchestrator.ConnectAsync(targetId);

            Vm.StatusText = result.Tier switch
            {
                Services.ConnectionTier.DirectLan => $"اتصال LAN مستقیم ({result.Ip})",
                Services.ConnectionTier.LanDiscovered => $"اتصال LAN کشف‌شده ({result.Ip})",
                Services.ConnectionTier.P2p => "اتصال P2P برقرار شد",
                Services.ConnectionTier.Relay => "اتصال از طریق relay",
                _ => $"خطا: {result.FailureReason ?? "نامشخص"}",
            };
            Vm.IsConnected = result.Tier != Services.ConnectionTier.Failed;
        }
        finally
        {
            Vm.IsConnecting = false;
        }
    }
}
