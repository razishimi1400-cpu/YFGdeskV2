using YfgDesk.Client.Services;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.ViewModels;

/// <summary>
/// Viewer-role view-model. Validates the target device ID (9 digits), holds
/// the requested permissions + video preference, and exposes the "request"
/// command that calls into <see cref="ConnectionOrchestrator"/>.
/// </summary>
public sealed class ViewerViewModel : ObservableObject
{
    private string _targetIdInput = string.Empty;
    private string _targetIdError = string.Empty;
    private bool _isConnecting;
    private bool _isConnected;
    private int _requestedPermissions = (int)Permissions.ViewScreen;
    private int _qualityMode = 1; // 0=Small,1=Standard,2=Custom
    private string _statusText = string.Empty;
    private bool _requestEnabled;

    /// <summary>User-typed target device ID (9 digits). May include spaces.</summary>
    public string TargetIdInput
    {
        get => _targetIdInput;
        set
        {
            if (SetProperty(ref _targetIdInput, value))
            {
                TargetIdError = string.Empty;
                var stripped = (value ?? string.Empty).Replace(" ", "").Trim();
                RequestEnabled = Validators.IsValidDeviceId(stripped);
            }
        }
    }

    public string TargetIdError
    {
        get => _targetIdError;
        set => SetProperty(ref _targetIdError, value);
    }

    public bool RequestEnabled
    {
        get => _requestEnabled;
        private set => SetProperty(ref _requestEnabled, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set => SetProperty(ref _isConnecting, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public bool RequestViewScreen
    {
        get => (_requestedPermissions & (int)Permissions.ViewScreen) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.ViewScreen; else v &= ~(int)Permissions.ViewScreen;
            _requestedPermissions = v; RaisePropertyChanged();
        }
    }

    public bool RequestRemoteControl
    {
        get => (_requestedPermissions & (int)Permissions.RemoteControl) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.RemoteControl; else v &= ~(int)Permissions.RemoteControl;
            _requestedPermissions = v; RaisePropertyChanged();
        }
    }

    public bool RequestFileTransfer
    {
        get => (_requestedPermissions & (int)Permissions.FileTransfer) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.FileTransfer; else v &= ~(int)Permissions.FileTransfer;
            _requestedPermissions = v; RaisePropertyChanged();
        }
    }

    public int QualityMode
    {
        get => _qualityMode;
        set => SetProperty(ref _qualityMode, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Strip spaces from the user input and return the clean 9-digit id.</summary>
    public string GetSanitizedTargetId() => (TargetIdInput ?? string.Empty).Replace(" ", "").Trim();

    /// <summary>Return the requested permissions as a bitmask.</summary>
    public int GetRequestedPermissions() => _requestedPermissions;

    public VideoPreference GetVideoPreference(int width, int height, int fps)
        => new(width, height, fps, QualityMode switch
        {
            0 => "Small",
            1 => "Standard",
            2 => "Custom",
            _ => "Standard",
        });
}
