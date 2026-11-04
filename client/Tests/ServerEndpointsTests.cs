using YfgDesk.Client.Services;

namespace YfgDesk.Client.Tests;

public class ServerEndpointsTests
{
    [Fact]
    public void GetAll_ReturnsThreeProductionServers()
    {
        var endpoints = ServerEndpoints.GetAll();

        Assert.Equal(3, endpoints.Count);
        Assert.Contains("http://188.121.147.94:7070", endpoints);
        Assert.Contains("http://37.114.216.224:7070", endpoints);
        Assert.Contains("http://94.182.31.149:7070", endpoints);
    }

    [Fact]
    public void Decode_RoundTripsEncode()
    {
        var original = "http://test.example.com:9999";
        var encoded = ServerEndpoints.Encode(original);
        var decoded = ServerEndpoints.Decode(encoded);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void GetAll_DoesNotContainPlainIps()
    {
        // The list itself should expose the decoded form (that's by design —
        // it's the runtime caller who needs the URL). But the encoded form
        // stored in the binary should NOT contain plain IP strings.
        // This test guards against accidentally pasting plaintext URLs
        // into the EncodedEndpoints array.
        var field = typeof(ServerEndpoints)
            .GetField("EncodedEndpoints",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var raw = (string[])field!.GetValue(null)!;

        foreach (var encoded in raw)
        {
            Assert.DoesNotContain("188.121.147.94", encoded);
            Assert.DoesNotContain("37.114.216.224", encoded);
            Assert.DoesNotContain("94.182.31.149", encoded);
            Assert.DoesNotContain("7070", encoded);
        }
    }

    [Fact]
    public void GetAllShuffled_ReturnsAllAndOnlyKnownEndpoints()
    {
        var shuffled = ServerEndpoints.GetAllShuffled(seed: "test-seed-1");

        Assert.Equal(3, shuffled.Count);
        var expected = new HashSet<string>(ServerEndpoints.GetAll());
        foreach (var url in shuffled)
        {
            Assert.Contains(url, expected);
        }
    }

    [Fact]
    public void GetAllShuffled_IsDeterministicForSameSeed()
    {
        var first = ServerEndpoints.GetAllShuffled(seed: "abc");
        var second = ServerEndpoints.GetAllShuffled(seed: "abc");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetAllShuffled_DifferentSeedsMightDiffer()
    {
        // With only 3 elements there are 6 permutations; pick two seeds
        // that we know produce different orders. This isn't strictly
        // guaranteed for any pair of seeds, so just assert that both
        // return valid permutations of the same set.
        var a = ServerEndpoints.GetAllShuffled(seed: "seed-A");
        var b = ServerEndpoints.GetAllShuffled(seed: "seed-B");

        Assert.Equal(3, a.Count);
        Assert.Equal(3, b.Count);
        Assert.Equal(
            new HashSet<string>(ServerEndpoints.GetAll()),
            new HashSet<string>(a));
        Assert.Equal(
            new HashSet<string>(ServerEndpoints.GetAll()),
            new HashSet<string>(b));
    }
}

public class ClientOptionsFailoverTests
{
    [Fact]
    public void GetActiveServerBaseUrl_DefaultsToFirstEmbeddedServer()
    {
        var opts = new ClientOptions();
        var active = opts.GetActiveServerBaseUrl();

        Assert.Equal("http://188.121.147.94:7070", active);
    }

    [Fact]
    public void AdvanceToNextServer_RotatesThroughList()
    {
        var opts = new ClientOptions();
        var all = opts.GetServerCandidates();
        Assert.Equal(3, all.Count);

        Assert.Equal(all[0], opts.GetActiveServerBaseUrl());
        Assert.True(opts.AdvanceToNextServer());
        Assert.Equal(all[1], opts.GetActiveServerBaseUrl());
        Assert.True(opts.AdvanceToNextServer());
        Assert.Equal(all[2], opts.GetActiveServerBaseUrl());
        // Wraps around to start; returns false to indicate wrap.
        Assert.False(opts.AdvanceToNextServer());
        Assert.Equal(all[0], opts.GetActiveServerBaseUrl());
    }

    [Fact]
    public void ResetToPrimaryServer_GoesBackToFirstServer()
    {
        var opts = new ClientOptions();
        opts.AdvanceToNextServer();
        opts.AdvanceToNextServer();
        Assert.NotEqual(opts.GetServerCandidates()[0], opts.GetActiveServerBaseUrl());

        opts.ResetToPrimaryServer();
        Assert.Equal(opts.GetServerCandidates()[0], opts.GetActiveServerBaseUrl());
    }

    [Fact]
    public void ExplicitServerBaseUrl_OverridesEmbeddedList()
    {
        var opts = new ClientOptions { ServerBaseUrl = "http://my.own.server:1234" };
        var candidates = opts.GetServerCandidates();

        Assert.Single(candidates);
        Assert.Equal("http://my.own.server:1234", candidates[0]);
        Assert.Equal("http://my.own.server:1234", opts.GetActiveServerBaseUrl());
    }

    [Fact]
    public void ResolveWebSocketUrl_DerivesFromActiveServer()
    {
        var opts = new ClientOptions();
        var ws = opts.ResolveWebSocketUrl();

        Assert.Equal("ws://188.121.147.94:7070/ws", ws);
    }

    [Fact]
    public void ResolveWebSocketUrl_UsesHttpsWhenServerIsHttps()
    {
        var opts = new ClientOptions { ServerBaseUrl = "https://secure.example.com" };
        var ws = opts.ResolveWebSocketUrl();

        Assert.Equal("wss://secure.example.com:443/ws", ws);
    }
}
