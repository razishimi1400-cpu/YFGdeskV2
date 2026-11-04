using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YfgDesk.Client.ViewModels;

namespace YfgDesk.Client;

/// <summary>
/// Main window code-behind. Hosts the tab control (Host / Connect / Unattended / Status),
/// brand bar, and sticky footer with protocol version + heartbeat state.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainVm ?? new MainViewModel(
            App.Options,
            new ViewModels.HostViewModel(new Services.HostConsentCoordinator()),
            new ViewModels.ViewerViewModel(),
            new ViewModels.UnattendedViewModel(new Services.UnattendedStore(Services.SecureStorage.Default)),
            new ViewModels.StatusViewModel());
    }

    /// <summary>Copy the device ID to the clipboard.</summary>
    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = (DataContext as MainViewModel)?.DeviceId ?? string.Empty;
            if (!string.IsNullOrEmpty(id))
                Clipboard.SetText(id);
        }
        catch { /* best-effort — clipboard may be locked */ }
    }

    /// <summary>Renew the device ID with the server (calls POST /api/hosts/renew).</summary>
    private void OnRenewId(object sender, RoutedEventArgs e)
    {
        // TODO: call App.Identity.RenewBearerWithFailoverAsync(...) and refresh the
        // displayed DeviceId. For now we just refresh the binding.
        try
        {
            var vm = DataContext as MainViewModel;
            vm?.RefreshDeviceId();
        }
        catch { /* best-effort */ }
    }

    /// <summary>Connect to the target ID entered in the input box.</summary>
    private void OnConnect(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = (DataContext as MainViewModel)?.Viewer;
            if (vm is null) return;
            // The ViewerViewModel.TargetId is already bound to the TextBox.
            // Trigger the connect command (if wired in the VM).
            vm.ConnectCommand?.Execute(null);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Toggle FA/EN language. The XAML is bound bidirectionally so
    /// this mostly flips <see cref="App.CurrentCulture"/> and refreshes the UI.</summary>
    private void OnToggleLang(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string lang)
        {
            App.CurrentCulture = lang;
            // Reload flow direction + title.
            FlowDirection = lang == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Title = lang == "fa"
                ? "YFG YASTA — پشتیبانی از راه دور"
                : "YFG YASTA — Remote Support";
        }
    }
}
