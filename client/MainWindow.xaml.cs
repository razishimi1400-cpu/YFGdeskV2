using System.Windows;
using System.Windows.Controls;
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
        try { Clipboard.SetText((DataContext as MainViewModel)?.DeviceId ?? string.Empty); }
        catch { /* best-effort — clipboard may be locked */ }
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
                ? "YFGDESK — پشتیبانی از راه دور"
                : "YFGDESK — Remote Support";
        }
    }
}
