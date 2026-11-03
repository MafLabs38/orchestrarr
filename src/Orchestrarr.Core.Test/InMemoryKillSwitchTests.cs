using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Test;

public sealed class InMemoryKillSwitchTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static AuditEntry Engaged() => new(Now, AuditEvents.KillSwitchEngaged, "alice-1", null, null);

    private static AuditEntry Released() => new(Now, AuditEvents.KillSwitchReleased, "alice-1", null, null);

    [Fact]
    public async Task A_change_is_applied_and_traced()
    {
        var trail = new InMemoryAuditLog(10);
        using var stop = new InMemoryKillSwitch(trail);

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.Applied);
        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.Applied);

        stop.Engaged.Should().BeFalse();
        trail.List().Select(entry => entry.Event).Should().Equal(AuditEvents.KillSwitchReleased, AuditEvents.KillSwitchEngaged);
    }

    [Fact]
    public async Task A_release_that_cannot_be_traced_leaves_the_stop_engaged()
    {
        using var stop = new InMemoryKillSwitch(new InMemoryAuditLog(1));
        await stop.ChangeAsync(true, Engaged(), Ct);

        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.NotTraced);

        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task An_engagement_that_cannot_be_traced_still_engages()
    {
        using var stop = new InMemoryKillSwitch(new InMemoryAuditLog(1));
        await stop.ChangeAsync(true, Engaged(), Ct);

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.AppliedUntraced);

        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task With_no_trail_a_change_has_nothing_to_trace_to()
    {
        using var stop = new InMemoryKillSwitch();

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.Applied);
        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.Applied);
    }
}
