using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Test;

public sealed class AuditCapacityPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static AuditEntry[] Entries(string name, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => new AuditEntry(Now, name, "someone", null, null))];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(8, 6)]
    [InlineData(2000, 1500)]
    [InlineData(int.MaxValue, 1_610_612_735)]
    public void Refusals_may_use_three_quarters_of_the_trail_rounded_down(int capacity, int limit)
    {
        AuditCapacityPolicy.RefusalLimit(capacity).Should().Be(limit);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(100, 5)]
    [InlineData(2000, 100)]
    [InlineData(200_000, 10_000)]
    public void The_stop_keeps_a_twentieth_of_the_trail_and_at_least_one_entry(int capacity, int reserve)
    {
        AuditCapacityPolicy.Reserve(capacity).Should().Be(reserve);
    }

    [Theory]
    [InlineData(100, 0, 0, 95, true)]
    [InlineData(100, 0, 0, 96, false)]
    [InlineData(100, 94, 0, 1, true)]
    public void Decisions_stop_where_the_reserve_of_the_stop_begins(int capacity, int held, int refusalsHeld, int adding, bool fits)
    {
        AuditCapacityPolicy.Fits(capacity, held, refusalsHeld, Entries(AuditEvents.ApprovalRecorded, adding)).Should().Be(fits);
    }

    [Theory]
    [InlineData(AuditEvents.KillSwitchEngaged)]
    [InlineData(AuditEvents.KillSwitchReleased)]
    [InlineData(AuditEvents.InstanceCreated)]
    public void The_stop_and_a_new_database_may_use_the_reserve_but_not_more(string name)
    {
        AuditCapacityPolicy.Fits(100, 99, 0, Entries(name, 1)).Should().BeTrue();
        AuditCapacityPolicy.Fits(100, 100, 0, Entries(name, 1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(100, 70, 5, true)]
    [InlineData(100, 74, 1, true)]
    [InlineData(100, 74, 2, false)]
    [InlineData(100, 75, 1, false)]
    public void Refusals_stay_in_their_share(int capacity, int refusalsHeld, int adding, bool fits)
    {
        AuditCapacityPolicy.Fits(capacity, 0, refusalsHeld, Entries(AuditEvents.ApprovalRefused, adding)).Should().Be(fits);
    }

    [Fact]
    public void A_batch_with_one_decision_cannot_use_the_reserve()
    {
        AuditEntry[] batch = [.. Entries(AuditEvents.KillSwitchEngaged, 1), .. Entries(AuditEvents.ApprovalRecorded, 1)];

        AuditCapacityPolicy.Fits(100, 94, 0, batch).Should().BeFalse();
    }

    [Fact]
    public void A_trail_with_no_capacity_takes_nothing_and_an_empty_batch_always_fits()
    {
        AuditCapacityPolicy.Fits(0, 0, 0, Entries(AuditEvents.KillSwitchEngaged, 1)).Should().BeFalse();
        AuditCapacityPolicy.Fits(0, 0, 0, []).Should().BeTrue();
    }
}
