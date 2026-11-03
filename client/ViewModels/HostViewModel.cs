using System.Collections.ObjectModel;
using YfgDesk.Client.Services;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.ViewModels;

/// <summary>
/// Host-role view-model. Surfaces:
/// - the host's 9-digit device ID (copyable),
/// - the "share screen" button (becomes hostReady),
/// - the accept/reject dialog state for an incoming request,
/// - the kill switch (EmergencyStop).
/// </summary>
public sealed class HostViewModel : ObservableObject
{
    private readonly HostConsentCoordinator _consent;
    private string _deviceId = "—";
    private bool _isSharing;
    private string _viewerDisplayName = string.Empty;
    private string _viewerDeviceId = string.Empty;
    private int _requestedPermissions;
    private int _grantedPermissions;
    private string? _activeSessionId;
    private bool _incomingRequestVisible;
    private bool _isActive;
    private long _consentDeadlineMs;

    public HostViewModel(HostConsentCoordinator consent)
    {
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
    }

    /// <summary>9-digit device ID. Empty before enrollment.</summary>
    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    public bool IsSharing
    {
        get => _isSharing;
        set => SetProperty(ref _isSharing, value);
    }

    public bool IncomingRequestVisible
    {
        get => _incomingRequestVisible;
        set => SetProperty(ref _incomingRequestVisible, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string ViewerDisplayName
    {
        get => _viewerDisplayName;
        set => SetProperty(ref _viewerDisplayName, value);
    }

    public string ViewerDeviceId
    {
        get => _viewerDeviceId;
        set => SetProperty(ref _viewerDeviceId, value);
    }

    public int RequestedPermissions
    {
        get => _requestedPermissions;
        set => SetProperty(ref _requestedPermissions, value);
    }

    public int GrantedPermissions
    {
        get => _grantedPermissions;
        set => SetProperty(ref _grantedPermissions, value);
    }

    public long ConsentDeadlineMs
    {
        get => _consentDeadlineMs;
        set => SetProperty(ref _consentDeadlineMs, value);
    }

    public bool RequestedViewScreen
    {
        get => (_requestedPermissions & (int)Permissions.ViewScreen) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.ViewScreen; else v &= ~(int)Permissions.ViewScreen;
            RequestedPermissions = v;
        }
    }

    public bool RequestedRemoteControl
    {
        get => (_requestedPermissions & (int)Permissions.RemoteControl) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.RemoteControl; else v &= ~(int)Permissions.RemoteControl;
            RequestedPermissions = v;
        }
    }

    public bool RequestedFileTransfer
    {
        get => (_requestedPermissions & (int)Permissions.FileTransfer) != 0;
        set
        {
            var v = _requestedPermissions;
            if (value) v |= (int)Permissions.FileTransfer; else v &= ~(int)Permissions.FileTransfer;
            RequestedPermissions = v;
        }
    }

    public bool GrantViewScreen
    {
        get => (_grantedPermissions & (int)Permissions.ViewScreen) != 0;
        set => GrantedPermissions = ApplyGrant(Permissions.ViewScreen, value);
    }

    public bool GrantRemoteControl
    {
        get => (_grantedPermissions & (int)Permissions.RemoteControl) != 0;
        set => GrantedPermissions = ApplyGrant(Permissions.RemoteControl, value);
    }

    public bool GrantFileTransfer
    {
        get => (_grantedPermissions & (int)Permissions.FileTransfer) != 0;
        set => GrantedPermissions = ApplyGrant(Permissions.FileTransfer, value);
    }

    private int ApplyGrant(Permissions perm, bool on)
    {
        var v = _grantedPermissions;
        if (on)
        {
            if ((_requestedPermissions & (int)perm) == 0) return v;
            v |= (int)perm;
        }
        else
        {
            v &= ~(int)perm;
        }
        return v;
    }

    public void OnIncomingRequest(string sessionId, string viewerId, string viewerDisplayName, int requestedPermissions, VideoPreference videoPreference, long nowMs)
    {
        _consent.Begin(sessionId, viewerId, viewerDisplayName, requestedPermissions, videoPreference, nowMs);
        var snap = _consent.GetSnapshot();
        ViewerDisplayName = snap.ViewerDisplayName ?? string.Empty;
        ViewerDeviceId = snap.ViewerId ?? string.Empty;
        RequestedPermissions = snap.RequestedPermissions;
        ConsentDeadlineMs = snap.AwaitingDeadlineMs;
        GrantedPermissions = 0;
        IncomingRequestVisible = true;
    }

    public bool Accept(long nowMs)
    {
        if (!_consent.Accept(_grantedPermissions, nowMs)) return false;
        IncomingRequestVisible = false;
        return true;
    }

    public bool Reject()
    {
        var ok = _consent.Reject();
        IncomingRequestVisible = false;
        return ok;
    }

    public bool Tick(long nowMs)
    {
        if (_consent.Expire(nowMs))
        {
            IncomingRequestVisible = false;
            return true;
        }
        return false;
    }

    public void EmergencyStop()
    {
        _consent.EmergencyStop();
        IsSharing = false;
        IncomingRequestVisible = false;
        IsActive = false;
    }

    public void Activate(long nowMs)
    {
        if (_consent.Activate(nowMs)) IsActive = true;
    }

    public string? ActiveSessionId
    {
        get => _activeSessionId;
        set => SetProperty(ref _activeSessionId, value);
    }
}
