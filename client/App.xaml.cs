using System.IO;
using System.Net.Http;
using System.Windows;
using YfgDesk.Client.Services;
using YfgDesk.Client.ViewModels;

namespace YfgDesk.Client;

/// <summary>
/// WPF application entry point. Builds the DI graph manually (no container —
/// the client is a single-process app). Host:
/// <list type="bullet">
///   <item><see cref="ClientOptions"/></item>
///   <item><see cref="SecureStorage"/></item>
///   <item><see cref="DeviceIdentity"/></item>
///   <item><see cref="LocalCache"/></item>
///   <item><see cref="DiscoveryClient"/></item>
///   <item><see cref="SignalingClient"/></item>
///   <item><see cref="ConnectionOrchestrator"/></item>
///   <item><see cref="HostConsentCoordinator"/></item>
///   <item><see cref="UnattendedStore"/></item>
///   <item><see cref="MainViewModel"/></item>
/// </list>
/// </summary>
public partial class App : Application
{
    public static ClientOptions Options { get; private set; } = new();
    public static ISecureStorage SecureStorage { get; private set; } = Services.SecureStorage.Default;
    public static DeviceIdentity? Identity { get; private set; }
    public static LocalCache? Cache { get; private set; }
    public static DiscoveryClient? Discovery { get; private set; }
    public static SignalingClient? Signaling { get; private set; }
    public static ConnectionOrchestrator? Orchestrator { get; private set; }
    public static HostConsentCoordinator? Consent { get; private set; }
    public static UnattendedStore? Unattended { get; private set; }
    public static MainViewModel? MainVm { get; private set; }

    /// <summary>RA-fa (RTL) is the default UI culture; EN-ltr is the toggle.</summary>
    public static string CurrentCulture { get; set; } = "fa";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await InitializeAsync();
    }

    private static async Task InitializeAsync()
    {
        Cache = new LocalCache(LocalCache.DefaultCachePath(), Options.LanCacheTtlSec);
        Consent = new HostConsentCoordinator();
        Unattended = new UnattendedStore(SecureStorage);

        // Try to load an existing identity.
        Identity = await DeviceIdentity.LoadAsync(SecureStorage);

        // If no identity yet (first run), enroll against the embedded server
        // list with automatic failover. The server that succeeds becomes the
        // active server; subsequent signaling will try it first, with
        // failover if it later goes down.
        if (Identity is null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                var (identity, serverUrl) = await DeviceIdentity.EnrollWithFailoverAsync(
                    Options.GetServerCandidates(),
                    SecureStorage,
                    http,
                    Options.DisplayName);
                Identity = identity;
                // Pin the active server to the one that worked.
                Options.ServerBaseUrl = serverUrl;
            }
            catch (Exception ex)
            {
                // Enrollment failed on all servers — continue with a placeholder
                // deviceId; the user will see "—" until network is available.
                _ = ex;
            }
        }

        var deviceId = Identity?.DeviceId ?? "—";
        var displayName = Identity?.DisplayName ?? Options.DisplayName;

        // Discovery needs a deviceId to broadcast — if not enrolled yet, use a placeholder.
        var discoveryTransport = new SystemUdpTransport(Options);
        Discovery = new DiscoveryClient(Options, Cache, discoveryTransport, deviceId, displayName);
        Discovery.Start();

        if (Identity is not null)
        {
            Signaling = new SignalingClient(Options, () => Identity.BearerToken);
            Signaling.Start();
        }

        var hostVm = new HostViewModel(Consent);
        var viewerVm = new ViewerViewModel();
        var unattendedVm = new UnattendedViewModel(Unattended);
        var statusVm = new StatusViewModel();
        MainVm = new MainViewModel(Options, hostVm, viewerVm, unattendedVm, statusVm);
        MainVm.SetDeviceId(deviceId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Signaling?.Dispose(); } catch { /* best-effort */ }
        try { Discovery?.Dispose(); } catch { /* best-effort */ }
        try { Identity?.Dispose(); } catch { /* best-effort */ }
        base.OnExit(e);
    }
}
