using System.Windows;
using System.Windows.Controls;
using YfgDesk.Client.ViewModels;

namespace YfgDesk.Client.Views;

/// <summary>
/// Unattended-access panel code-behind. Wires PasswordBoxes (whose contents
/// can't be bound in WPF for security reasons) to the view-model manually.
/// </summary>
public partial class UnattendedView : UserControl
{
    public UnattendedView()
    {
        InitializeComponent();
    }

    private UnattendedViewModel Vm => (UnattendedViewModel)DataContext;

    private void OnPwdChanged(object sender, RoutedEventArgs e)
        => Vm.Password = PwdBox.Password;

    private void OnPwdConfirmChanged(object sender, RoutedEventArgs e)
        => Vm.PasswordConfirm = PwdConfirmBox.Password;

    private void OnUnlockChanged(object sender, RoutedEventArgs e)
        => Vm.UnlockInput = UnlockBox.Password;

    private void OnEnable(object sender, RoutedEventArgs e)
        => _ = Vm.EnableAsync();

    private void OnDisable(object sender, RoutedEventArgs e)
        => _ = Vm.DisableAsync();

    private async void OnUnlock(object sender, RoutedEventArgs e)
        => await Vm.UnlockAsync();
}
