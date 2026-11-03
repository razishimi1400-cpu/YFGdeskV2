using System.Collections.ObjectModel;
using YfgDesk.Client.Services;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.ViewModels;

/// <summary>
/// Top-level view-model for the WPF MainWindow. Holds the active Host /
/// Viewer view-models plus global state (connection tier, online state,
/// last error, sticky-footer heartbeat).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _deviceId = "—";
    private string _displayName = string.Empty;
    private bool _isOnline;
    private bool _isConnecting;
    private string _lastError = string.Empty;
    private ConnectionTier _activeTier = ConnectionTier.Failed;
    private SignalingClient.SignalingState _signalingState = SignalingClient.SignalingState.Disconnected;
    private int _selectedTab;   // 0 = Host, 1 = Connect

    public MainViewModel(ClientOptions opts, HostViewModel host, ViewerViewModel viewer, UnattendedViewModel unattended, StatusViewModel status)
    {
        Options = opts;
        Host = host;
        Viewer = viewer;
        Unattended = unattended;
        Status = status;
    }

    public ClientOptions Options { get; }

    public HostViewModel Host { get; }
    public ViewerViewModel Viewer { get; }
    public UnattendedViewModel Unattended { get; }
    public StatusViewModel Status { get; }

    /// <summary>Current device ID (9-digit string, copyable). Empty before enrollment.</summary>
    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetProperty(ref _isOnline, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set => SetProperty(ref _isConnecting, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public ConnectionTier ActiveTier
    {
        get => _activeTier;
        set => SetProperty(ref _activeTier, value);
    }

    public SignalingClient.SignalingState SignalingState
    {
        get => _signalingState;
        set
        {
            if (SetProperty(ref _signalingState, value))
            {
                IsOnline = value == SignalingClient.SignalingState.Online;
            }
        }
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public string ProtocolVersionLabel => $"YFGDESK v2.0 · protocol {ProtocolVersion.Current}";

    /// <summary>
    /// Set the device ID after a successful enrollment. Also pushes it to
    /// <see cref="HostViewModel"/> and <see cref="ViewerViewModel"/>.
    /// </summary>
    public void SetDeviceId(string deviceId)
    {
        DeviceId = deviceId;
        Host.DeviceId = deviceId;
    }
}
