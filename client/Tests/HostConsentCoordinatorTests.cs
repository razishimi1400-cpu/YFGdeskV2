using YfgDesk.Client.Services;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Tests;

/// <summary>
/// State-machine tests for <see cref="HostConsentCoordinator"/>.
/// Verifies: Begin/Accept/Reject/Activate/End/Expire/EmergencyStop transitions.
/// </summary>
public class HostConsentCoordinatorTests
{
    private static long Ms(int sec) => DateTimeOffset.UtcNow.AddSeconds(sec).ToUnixTimeMilliseconds();

    [Fact]
    public void Idle_To_AwaitingConsent_On_Begin()
    {
        var c = new HostConsentCoordinator();
        Assert.Equal(HostConsentCoordinator.State.Idle, c.CurrentState);

        c.Begin("s_abc", "123456789", "Viewer-1",
            (int)Permissions.ViewScreen | (int)Permissions.RemoteControl,
            new VideoPreference(1920, 1080, 30, "Standard"),
            nowMs: Ms(0));

        Assert.Equal(HostConsentCoordinator.State.AwaitingConsent, c.CurrentState);
        Assert.Equal("s_abc", c.ActiveSessionId);
        Assert.Equal("Viewer-1", c.ViewerDisplayName);
    }

    [Fact]
    public void Begin_Throws_If_Already_Awaiting()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        Assert.Throws<InvalidOperationException>(() =>
            c.Begin("s_other", "987654321", "Viewer-2", 1, null, Ms(0)));
    }

    [Fact]
    public void Accept_Only_Subset_Of_Requested()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1",
            (int)Permissions.ViewScreen | (int)Permissions.RemoteControl,
            null, Ms(0));

        // Granting a bit NOT in requested → must fail.
        Assert.False(c.Accept((int)Permissions.FileTransfer, Ms(1)));
        Assert.Equal(HostConsentCoordinator.State.AwaitingConsent, c.CurrentState);

        // Granting zero → must fail.
        Assert.False(c.Accept(0, Ms(2)));
        Assert.Equal(HostConsentCoordinator.State.AwaitingConsent, c.CurrentState);

        // Granting a strict subset → ok.
        Assert.True(c.Accept((int)Permissions.ViewScreen, Ms(3)));
        Assert.Equal(HostConsentCoordinator.State.Accepted, c.CurrentState);
        Assert.Equal((int)Permissions.ViewScreen, c.GrantedPermissions);
    }

    [Fact]
    public void Reject_Transitions_To_Terminal()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        Assert.True(c.Reject());
        Assert.Equal(HostConsentCoordinator.State.Rejected, c.CurrentState);
        Assert.Equal(0, c.GrantedPermissions);

        // Reject from non-awaiting state → false.
        Assert.False(c.Reject());
    }

    [Fact]
    public void Expire_After_Deadline_Transitions_To_TimedOut()
    {
        var c = new HostConsentCoordinator { ConsentWindowMs = 5 * 60 * 1000 };
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, nowMs: Ms(0));

        // Within window → no transition.
        Assert.False(c.Expire(nowMs: Ms(60)));
        Assert.Equal(HostConsentCoordinator.State.AwaitingConsent, c.CurrentState);

        // After window → TimedOut.
        Assert.True(c.Expire(nowMs: Ms(360)));
        Assert.Equal(HostConsentCoordinator.State.TimedOut, c.CurrentState);
    }

    [Fact]
    public void Accept_After_Deadline_Fails_And_Transitions_To_TimedOut()
    {
        var c = new HostConsentCoordinator { ConsentWindowMs = 5 * 60 * 1000 };
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, nowMs: Ms(0));

        Assert.False(c.Accept(1, Ms(400)));
        Assert.Equal(HostConsentCoordinator.State.TimedOut, c.CurrentState);
    }

    [Fact]
    public void Activate_From_Accepted_To_Active()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        Assert.True(c.Accept(1, Ms(1)));
        Assert.True(c.Activate(Ms(2)));
        Assert.Equal(HostConsentCoordinator.State.Active, c.CurrentState);
        Assert.True(c.HasPermission(Permissions.ViewScreen));
    }

    [Fact]
    public void Activate_From_AwaitingConsent_Fails()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        Assert.False(c.Activate(Ms(1)));
    }

    [Fact]
    public void End_From_Any_NonTerminal_Transitions_To_Ended()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        Assert.True(c.Accept(1, Ms(1)));
        Assert.True(c.Activate(Ms(2)));
        Assert.True(c.End());
        Assert.Equal(HostConsentCoordinator.State.Ended, c.CurrentState);
        Assert.Equal(0, c.GrantedPermissions);
        Assert.False(c.HasPermission(Permissions.ViewScreen));
    }

    [Fact]
    public void EmergencyStop_From_AwaitingConsent_Transitions_To_Ended()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1",
            (int)Permissions.ViewScreen | (int)Permissions.RemoteControl, null, Ms(0));
        c.EmergencyStop();
        Assert.Equal(HostConsentCoordinator.State.Ended, c.CurrentState);
        Assert.Equal(0, c.GrantedPermissions);
    }

    [Fact]
    public void EmergencyStop_From_Active_Clears_Permissions()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        c.Accept(1, Ms(1));
        c.Activate(Ms(2));
        Assert.True(c.HasPermission(Permissions.ViewScreen));

        c.EmergencyStop();
        Assert.Equal(HostConsentCoordinator.State.Ended, c.CurrentState);
        Assert.False(c.HasPermission(Permissions.ViewScreen));
    }

    [Fact]
    public void Reset_Restores_Idle()
    {
        var c = new HostConsentCoordinator();
        c.Begin("s_abc", "123456789", "Viewer-1", 1, null, Ms(0));
        c.Reject();
        c.Reset();
        Assert.Equal(HostConsentCoordinator.State.Idle, c.CurrentState);
        Assert.Null(c.ActiveSessionId);
    }

    [Fact]
    public void Snapshot_Reflects_State()
    {
        var c = new HostConsentCoordinator { ConsentWindowMs = 10_000 };
        c.Begin("s_abc", "123456789", "Viewer-1",
            (int)Permissions.ViewScreen, null, nowMs: Ms(0));
        var snap = c.GetSnapshot();
        Assert.Equal(HostConsentCoordinator.State.AwaitingConsent, snap.CurrentState);
        Assert.Equal("123456789", snap.ViewerId);
        Assert.Equal("Viewer-1", snap.ViewerDisplayName);
        Assert.Equal((int)Permissions.ViewScreen, snap.RequestedPermissions);
        Assert.Equal(0, snap.GrantedPermissions);
        Assert.Equal("s_abc", snap.SessionId);
    }
}
