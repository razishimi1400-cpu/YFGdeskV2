using System.Windows.Input;
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

    /// <summary>
    /// Bound to the big AnyDesk-style input box. May contain spaces or
    /// Persian digits — both are stripped before validation.
    /// </summary>
    public string TargetId
    {
        get => _targetIdInput;
        set
        {
            if (SetProperty(ref _targetIdInput, value))
            {
                TargetIdError = string.Empty;
                var stripped = SanitizeId(value);
                RequestEnabled = Validators.IsValidDeviceId(stripped);
            }
        }
    }

    /// <summary>Kept for backward compatibility — same as <see cref="TargetId"/>.</summary>
    public string TargetIdInput
    {
        get => _targetIdInput;
        set => TargetId = value;
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

    /// <summary>
    /// Connect command — bound from the big "اتصال" button. The actual
    /// orchestration lives in <see cref="App.Orchestrator"/>; the command
    /// just kicks it off if the target ID is valid.
    /// </summary>
    public ICommand? ConnectCommand { get; set; }

    /// <summary>Strip spaces + Persian digits → Latin digits.</summary>
    private static string SanitizeId(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // Map Persian/Arabic digits to Latin
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c >= '\u06F0' && c <= '\u06F9') // Persian digits
                sb.Append((char)('0' + (c - '\u06F0')));
            else if (c >= '\u0660' && c <= '\u0669') // Arabic-Indic digits
                sb.Append((char)('0' + (c - '\u0660')));
            else if (c == ' ' || c == '\u00A0') // skip spaces
                continue;
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Strip spaces from the user input and return the clean 9-digit id.</summary>
    public string GetSanitizedTargetId() => SanitizeId(TargetIdInput);

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
