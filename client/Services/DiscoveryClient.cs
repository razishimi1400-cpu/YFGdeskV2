using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YfgDesk.Shared.Crypto;

namespace YfgDesk.Client.Services;

/// <summary>
/// LAN discovery client. Broadcasts a UDP beacon on port 49152 every 5s,
/// listens for incoming beacons, and serves them through <see cref="LocalCache"/>.
/// Also handles Tier-2 of the orchestrator: query the LAN for a specific
/// <c>deviceId</c> within a 3s timeout.
///
/// Format of the beacon (JSON, UTF-8):
/// <code>
/// { "protocol":"yfgdesk","version":1,"deviceId":"123456789","displayName":"Office-PC","listenPort":49153,"timestamp":&lt;epochMs&gt; }
/// </code>
///
/// mDNS is NOT implemented in v2.0 (per the spec: "if too complex, just UDP
/// broadcast is enough for v2.0"). The <see cref="MdnsServiceType"/> property
/// is reserved for v2.1.
/// </summary>
public sealed class DiscoveryClient : IDisposable
{
    public const int ProtocolVersion = 1;
    public const string ProtocolName = "yfgdesk";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClientOptions _opts;
    private readonly LocalCache _cache;
    private readonly IUdpTransport _transport;
    private readonly Func<long> _clock;
    private readonly string _selfDeviceId;
    private readonly string _selfDisplayName;

    private readonly CancellationTokenSource _cts = new();
    private Task? _broadcastTask;
    private Task? _listenTask;
    private int _started;

    public DiscoveryClient(
        ClientOptions opts,
        LocalCache cache,
        IUdpTransport transport,
        string selfDeviceId,
        string selfDisplayName,
        Func<long>? clock = null)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _selfDeviceId = selfDeviceId ?? throw new ArgumentNullException(nameof(selfDeviceId));
        _selfDisplayName = selfDisplayName ?? throw new ArgumentNullException(nameof(selfDisplayName));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Begin broadcasting + listening. Idempotent. Returns immediately;
    /// background tasks run until <see cref="Dispose"/> is called.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _broadcastTask = BroadcastLoopAsync(_cts.Token);
        _listenTask = ListenLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Tier-2 query: send a targeted broadcast for <paramref name="targetDeviceId"/>
    /// and wait up to <paramref name="timeout"/> for a matching beacon.
    /// Returns the entry if the device responded (and updates the cache).
    /// </summary>
    public async Task<DiscoveryEntry?> QueryLanAsync(string targetDeviceId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Validators.IsValidDeviceId(targetDeviceId)) return null;
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(_opts.LanQueryTimeoutSec);

        // Probe packet: a normal beacon with an additional `query` field. Old
        // listeners ignore it; new listeners respond by immediately re-broadcasting.
        var probe = new BeaconPayload(
            ProtocolName, ProtocolVersion, _selfDeviceId, _selfDisplayName,
            _opts.LanListenPort, _clock(), Query: targetDeviceId);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linkedCts.CancelAfter(timeout);

        await _transport.SendBroadcastAsync(
            new UdpPacket(IPAddress.Broadcast, _opts.LanDiscoveryPort, EncodeBeacon(probe)),
            linkedCts.Token);

        // Wait for any incoming beacon matching the target deviceId.
        // ListenLoopAsync also feeds the cache; here we just observe.
        var deadlineMs = _clock() + (long)timeout.TotalMilliseconds;
        while (!linkedCts.IsCancellationRequested)
        {
            var nowMs = _clock();
            if (nowMs >= deadlineMs) break;

            var entry = _cache.LookupDevice(targetDeviceId);
            if (entry is not null) return entry;

            try { await Task.Delay(100, linkedCts.Token); }
            catch (OperationCanceledException) { break; }
        }

        // Final cache lookup.
        return _cache.LookupDevice(targetDeviceId);
    }

    /// <summary>
    /// Send a one-shot beacon (e.g. on startup, before the broadcast loop kicks in).
    /// </summary>
    public async Task SendBeaconAsync(CancellationToken ct = default)
    {
        var beacon = new BeaconPayload(
            ProtocolName, ProtocolVersion, _selfDeviceId, _selfDisplayName,
            _opts.LanListenPort, _clock(), Query: null);
        await _transport.SendBroadcastAsync(
            new UdpPacket(IPAddress.Broadcast, _opts.LanDiscoveryPort, EncodeBeacon(beacon)),
            ct);
    }

    // ---- background loops ----

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        // Stagger first send to avoid thundering herd on app start.
        try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendBeaconAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // Transient network errors: swallow and keep looping.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_opts.LanBroadcastIntervalSec), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpPacket packet;
            try
            {
                packet = await _transport.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // Transient — keep listening.
                continue;
            }

            if (!TryDecodeBeacon(packet.Payload, out var beacon)) continue;
            if (beacon!.Protocol != ProtocolName) continue;
            if (beacon.Version != ProtocolVersion) continue;
            if (!Validators.IsValidDeviceId(beacon.DeviceId)) continue;
            if (beacon.DeviceId == _selfDeviceId) continue;

            // Update the cache.
            var entry = new DiscoveryEntry
            {
                DeviceId = beacon.DeviceId,
                Ip = packet.Source?.Address.ToString() ?? "0.0.0.0",
                Port = beacon.ListenPort,
                DisplayName = beacon.DisplayName ?? string.Empty,
                LastSeenMs = beacon.Timestamp != 0 ? beacon.Timestamp : _clock(),
                Source = "broadcast",
            };
            _cache.Upsert(entry, entry.LastSeenMs);

            // If the beacon is a query for US, immediately re-broadcast so the
            // other side sees our beacon. (Tier-2 probe response.)
            if (!string.IsNullOrEmpty(beacon.Query) && beacon.Query == _selfDeviceId)
            {
                try { await SendBeaconAsync(ct); }
                catch { /* best-effort */ }
            }
        }
    }

    // ---- codec ----

    internal static byte[] EncodeBeacon(BeaconPayload beacon)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon, JsonOpts));

    internal static bool TryDecodeBeacon(byte[] payload, out BeaconPayload? beacon)
    {
        beacon = null;
        if (payload is null || payload.Length == 0 || payload.Length > 4096) return false;
        try
        {
            var json = Encoding.UTF8.GetString(payload);
            beacon = JsonSerializer.Deserialize<BeaconPayload>(json, JsonOpts);
            return beacon is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { Task.WaitAll(new[] { _broadcastTask ?? Task.CompletedTask, _listenTask ?? Task.CompletedTask }, TimeSpan.FromSeconds(1)); }
        catch { /* best-effort */ }
        _cts.Dispose();
        _transport.Dispose();
    }

    // ---- DTOs ----

    public sealed record BeaconPayload(
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("listenPort")] int ListenPort,
        [property: JsonPropertyName("timestamp")] long Timestamp,
        [property: JsonPropertyName("query")] string? Query);
}

/// <summary>
/// A received UDP packet.
/// </summary>
public readonly record struct UdpPacket(IPAddress BroadcastOrTarget, int Port, byte[] Payload, IPEndPoint? Source = null);

/// <summary>
/// Abstraction over the UDP socket used by <see cref="DiscoveryClient"/>.
/// The production impl is <see cref="SystemUdpTransport"/>; tests inject
/// <see cref="InMemoryUdpTransport"/>.
/// </summary>
public interface IUdpTransport : IDisposable
{
    Task SendBroadcastAsync(UdpPacket packet, CancellationToken ct = default);
    Task<UdpPacket> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Production UDP transport built on <see cref="UdpClient"/>. Listens on
/// <see cref="ClientOptions.LanDiscoveryPort"/> (default 49152) and sends
/// broadcasts to <c>255.255.255.255</c>.
/// </summary>
public sealed class SystemUdpTransport : IUdpTransport
{
    private readonly UdpClient _sendClient;
    private readonly UdpClient _listenClient;
    private readonly int _listenPort;

    public SystemUdpTransport(ClientOptions opts)
    {
        _listenPort = opts.LanDiscoveryPort;

        _sendClient = new UdpClient();
        _sendClient.EnableBroadcast = true;
        // Allow the listen socket to bind even if another yfgdesk client is
        // running on the same host (multi-session dev/test scenario).
        _sendClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _listenClient = new UdpClient();
        _listenClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenClient.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
    }

    public async Task SendBroadcastAsync(UdpPacket packet, CancellationToken ct = default)
    {
        var ep = new IPEndPoint(packet.BroadcastOrTarget, packet.Port);
        await _sendClient.SendAsync(packet.Payload, ep, ct);
    }

    public async Task<UdpPacket> ReceiveAsync(CancellationToken ct = default)
    {
        var result = await _listenClient.ReceiveAsync(ct);
        return new UdpPacket(
            IPAddress.Broadcast,
            _listenPort,
            result.Buffer,
            Source: result.RemoteEndPoint);
    }

    public void Dispose()
    {
        try { _sendClient.Dispose(); } catch { /* best-effort */ }
        try { _listenClient.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// In-memory UDP transport for tests. Two endpoints connected by a queue:
/// <c>SendBroadcastAsync</c> enqueues; <c>ReceiveAsync</c> dequeues.
/// </summary>
public sealed class InMemoryUdpTransport : IUdpTransport
{
    private readonly System.Threading.Channels.Channel<UdpPacket> _inbox =
        System.Threading.Channels.Channel.CreateUnbounded<UdpPacket>();

    public IPAddress SelfAddress { get; set; } = IPAddress.Parse("127.0.0.1");

    public Task SendBroadcastAsync(UdpPacket packet, CancellationToken ct = default)
    {
        _inbox.Writer.TryWrite(packet);
        return Task.CompletedTask;
    }

    public async Task<UdpPacket> ReceiveAsync(CancellationToken ct = default)
    {
        return await _inbox.Reader.ReadAsync(ct);
    }

    /// <summary>
    /// Simulate an incoming beacon from a remote peer.
    /// </summary>
    public void InjectIncoming(UdpPacket packet) => _inbox.Writer.TryWrite(packet);

    public void Dispose() => _inbox.Writer.TryComplete();
}
