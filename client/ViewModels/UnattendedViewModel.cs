using YfgDesk.Client.Services;

namespace YfgDesk.Client.ViewModels;

/// <summary>Opt-in unattended-access view-model.</summary>
public sealed class UnattendedViewModel : ObservableObject
{
    private readonly UnattendedStore _store;
    private bool _enabled;
    private string _password = string.Empty;
    private string _passwordConfirm = string.Empty;
    private string _statusMessage = string.Empty;
    private string _unlockInput = string.Empty;
    private int _failureCount;

    public UnattendedViewModel(UnattendedStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ = RefreshEnabledAsync();
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string PasswordConfirm
    {
        get => _passwordConfirm;
        set => SetProperty(ref _passwordConfirm, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string UnlockInput
    {
        get => _unlockInput;
        set => SetProperty(ref _unlockInput, value);
    }

    public int FailureCount
    {
        get => _failureCount;
        set => SetProperty(ref _failureCount, value);
    }

    public async Task RefreshEnabledAsync(CancellationToken ct = default)
    {
        try { Enabled = await _store.IsEnabledAsync(ct); }
        catch (PlatformNotSupportedException)
        {
            Enabled = false;
            StatusMessage = "Unattended access requires Windows.";
        }
    }

    public async Task EnableAsync(CancellationToken ct = default)
    {
        if (Password.Length < 8) { StatusMessage = "رمز باید حداقل ۸ کاراکتر باشد."; return; }
        if (Password != PasswordConfirm) { StatusMessage = "رمز و تأیید آن یکسان نیست."; return; }
        try
        {
            await _store.EnableAsync(Password, ct);
            await RefreshEnabledAsync(ct);
            StatusMessage = "حالت بدون نظارت فعال شد.";
        }
        catch (PlatformNotSupportedException)
        {
            StatusMessage = "این ویژگی روی این سیستم پشتیبانی نمی‌شود.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطا: {ex.Message}";
        }
        finally { Password = string.Empty; PasswordConfirm = string.Empty; }
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        try { await _store.DisableAsync(ct); Enabled = false; StatusMessage = "غیرفعال شد."; }
        catch (PlatformNotSupportedException) { StatusMessage = "این ویژگی روی این سیستم پشتیبانی نمی‌شود."; }
    }

    public async Task<bool> UnlockAsync(CancellationToken ct = default)
    {
        try
        {
            var ok = await _store.VerifyAsync(UnlockInput, ct);
            if (ok) { FailureCount = 0; StatusMessage = "احراز هویت موفق."; }
            else
            {
                FailureCount = _store.FailureCount;
                StatusMessage = "رمز اشتباه.";
            }
            return ok;
        }
        catch (PlatformNotSupportedException) { StatusMessage = "این ویژگی روی این سیستم پشتیبانی نمی‌شود."; return false; }
        finally { UnlockInput = string.Empty; }
    }
}

/// <summary>Status bar view-model: connection state + tier + last error.</summary>
public sealed class StatusViewModel : ObservableObject
{
    private string _connectionState = "offline";
    private ConnectionTier _tier = ConnectionTier.Failed;
    private string _lastError = string.Empty;
    private string _heartbeatState = "—";

    public string ConnectionState
    {
        get => _connectionState;
        set => SetProperty(ref _connectionState, value);
    }

    public ConnectionTier Tier
    {
        get => _tier;
        set => SetProperty(ref _tier, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public string HeartbeatState
    {
        get => _heartbeatState;
        set => SetProperty(ref _heartbeatState, value);
    }
}
