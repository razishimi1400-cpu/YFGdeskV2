using YfgDesk.Client.Services;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Tests;

/// <summary>
/// Three-tier fallback state-machine tests for <see cref="ConnectionOrchestrator"/>.
///
/// Each test injects mock implementations of the five collaborators
/// (<see cref="LocalCache"/>, <see cref="DiscoveryClient"/>,
/// <see cref="IServerLookupClient"/>, <see cref="ITurnClient"/>,
/// <see cref="IP2pEstablisher"/>, <see cref="IDirectLanProbe"/>) and verifies
/// that the orchestrator walks the tiers in priority order.
/// </summary>
public class ConnectionOrchestratorTests
{
    private static long Ms(int sec) => DateTimeOffset.UtcNow.AddSeconds(sec).ToUnixTimeMilliseconds();

    /// <summary>Build a fresh orchestrator with the given mocks.</summary>
    private static (ConnectionOrchestrator, Mocks) Build()
    {
        var cacheFile = Path.GetTempFileName();
        var cache = new LocalCache(cacheFile, ttlSec: 60);
        var transport = new InMemoryUdpTransport();
        var opts = new ClientOptions { LanQueryTimeoutSec = 1, LanBroadcastIntervalSec = 60 };
        var discovery = new DiscoveryClient(opts, cache, transport, "111111111", "Self");
        var mocks = new Mocks
        {
            Cache = cache,
            CacheFile = cacheFile,
            Discovery = discovery,
            ServerLookup = new MockServerLookup(),
            Turn = new MockTurn(),
            P2p = new MockP2p(),
            DirectLan = new MockDirectLan(),
            Opts = opts,
        };
        var orchestrator = new ConnectionOrchestrator(
            cache, discovery, mocks.ServerLookup, mocks.Turn,
            mocks.P2p, mocks.DirectLan, opts);
        return (orchestrator, mocks);
    }

    [Fact]
    public async Task Tier1_CacheHit_DirectLan_Success()
    {
        var (orch, mocks) = Build();
        try
        {
            mocks.Cache.Upsert(new DiscoveryEntry
            {
                DeviceId = "123456789",
                Ip = "192.168.1.10",
                Port = 49153,
                LastSeenMs = Ms(0),
            });
            ((MockDirectLan)mocks.DirectLan).ShouldSucceed = true;

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.DirectLan, result.Tier);
            Assert.Equal("192.168.1.10", result.Ip);
            Assert.False(((MockServerLookup)mocks.ServerLookup).WasCalled);
            Assert.False(((MockTurn)mocks.Turn).WasCalled);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Tier1_CacheHit_DirectLan_Fail_FallsTo_Tier3()
    {
        var (orch, mocks) = Build();
        try
        {
            mocks.Cache.Upsert(new DiscoveryEntry
            {
                DeviceId = "123456789",
                Ip = "192.168.1.10",
                Port = 49153,
                LastSeenMs = Ms(0),
            });
            ((MockDirectLan)mocks.DirectLan).ShouldSucceed = false; // tier1 direct fails
            // Tier 3: server lookup returns online=true, P2P succeeds.
            ((MockServerLookup)mocks.ServerLookup).Response = new LookupResponse(
                "5.5.5.5", 49153, Online: true, DateTime.UtcNow,
                new[] { "5.5.5.5" }, new[] { "49153" }, "123456789", "Remote");
            ((MockP2p)mocks.P2p).ShouldSucceed = true;

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.P2p, result.Tier);
            Assert.True(((MockServerLookup)mocks.ServerLookup).WasCalled);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Tier1_CacheHit_Already_Populated_Returns_DirectLan()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockDirectLan)mocks.DirectLan).ShouldSucceed = true;
            mocks.Cache.Upsert(new DiscoveryEntry
            {
                DeviceId = "123456789",
                Ip = "192.168.1.50",
                Port = 49153,
                LastSeenMs = Ms(0),
            });

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.DirectLan, result.Tier);
            Assert.Equal("192.168.1.50", result.Ip);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Tier3_ServerLookup_Online_P2p_Success()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockServerLookup)mocks.ServerLookup).Response = new LookupResponse(
                "5.5.5.5", 49153, Online: true, DateTime.UtcNow,
                new[] { "5.5.5.5" }, new[] { "49153" }, "123456789", "Remote");
            ((MockP2p)mocks.P2p).ShouldSucceed = true;

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.P2p, result.Tier);
            Assert.True(((MockServerLookup)mocks.ServerLookup).WasCalled);
            // Tier 4 (TURN) must NOT have been called.
            Assert.False(((MockTurn)mocks.Turn).WasCalled);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Tier3_TargetOffline_FallsTo_Tier4()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockServerLookup)mocks.ServerLookup).Response = new LookupResponse(
                "5.5.5.5", 49153, Online: false, DateTime.UtcNow,
                Array.Empty<string>(), Array.Empty<string>(), "123456789", "Remote");
            ((MockTurn)mocks.Turn).Bundle = new TurnBundle(
                "u", "c", new[] { "turn:turn.yfgdesk.com:3478?transport=udp" }, DateTime.UtcNow.AddHours(1));
            ((MockP2p)mocks.P2p).ShouldSucceed = true;

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.Relay, result.Tier);
            Assert.True(((MockTurn)mocks.Turn).WasCalled);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Tier4_TurnUnavailable_Returns_Failed()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockServerLookup)mocks.ServerLookup).Response = null;
            ((MockTurn)mocks.Turn).Bundle = null;  // TURN not configured.

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.Failed, result.Tier);
            Assert.False(string.IsNullOrEmpty(result.FailureReason));
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task All_Tiers_Fail_Surfaces_Last_Error()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockServerLookup)mocks.ServerLookup).Response = null;
            ((MockTurn)mocks.Turn).Bundle = null;
            ((MockP2p)mocks.P2p).ShouldSucceed = false;

            var result = await orch.ConnectAsync("123456789");
            Assert.Equal(ConnectionTier.Failed, result.Tier);
            Assert.Contains("tier", result.FailureReason ?? "");
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Empty_TargetId_Returns_Failed()
    {
        var (orch, mocks) = Build();
        try
        {
            var result = await orch.ConnectAsync("");
            Assert.Equal(ConnectionTier.Failed, result.Tier);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task TierChanged_Event_Fires_For_Each_Tier()
    {
        var (orch, mocks) = Build();
        try
        {
            ((MockServerLookup)mocks.ServerLookup).Response = null;
            ((MockTurn)mocks.Turn).Bundle = null;

            var tiers = new List<ConnectionTier>();
            orch.TierChanged += (id, tier) => tiers.Add(tier);

            await orch.ConnectAsync("123456789");

            Assert.Contains(ConnectionTier.DirectLan, tiers);
            Assert.Contains(ConnectionTier.LanDiscovered, tiers);
            Assert.Contains(ConnectionTier.P2p, tiers);
            Assert.Contains(ConnectionTier.Relay, tiers);
        }
        finally { try { File.Delete(mocks.CacheFile); } catch { /* best-effort */ } }
    }

    // ---- helpers ----

    private sealed class Mocks
    {
        public LocalCache Cache = null!;
        public string CacheFile = null!;
        public DiscoveryClient Discovery = null!;
        public ClientOptions Opts = null!;
        public IServerLookupClient ServerLookup = null!;
        public ITurnClient Turn = null!;
        public IP2pEstablisher P2p = null!;
        public IDirectLanProbe DirectLan = null!;
    }

    private sealed class MockServerLookup : IServerLookupClient
    {
        public LookupResponse? Response { get; set; }
        public bool WasCalled { get; private set; }
        public Task<LookupResponse?> LookupAsync(string targetId, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(Response);
        }
    }

    private sealed class MockTurn : ITurnClient
    {
        public TurnBundle? Bundle { get; set; }
        public bool WasCalled { get; private set; }
        public Task<TurnBundle?> GetCredentialsAsync(CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(Bundle);
        }
    }

    private sealed class MockP2p : IP2pEstablisher
    {
        public bool ShouldSucceed { get; set; }
        public Task<bool> TryEstablishAsync(string targetId, IceServer[] iceServers, CancellationToken ct = default)
            => Task.FromResult(ShouldSucceed);
    }

    private sealed class MockDirectLan : IDirectLanProbe
    {
        public bool ShouldSucceed { get; set; }
        public Task<bool> TryConnectAsync(string ip, int port, CancellationToken ct = default)
            => Task.FromResult(ShouldSucceed);
    }
}
