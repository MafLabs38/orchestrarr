using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

/// <summary>What every audit trail does, in memory and in the file, so that the two cannot drift: the same rule, run against both.</summary>
public sealed class AuditLogContractTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();

    public static TheoryData<string> Logs => ["memory", "sqlite"];

    public void Dispose() => _file.Dispose();

    private IAuditLog Make(string kind, int capacity) =>
        kind == "memory" ? new InMemoryAuditLog(capacity) : new SqliteAuditLog(_file.Open(), capacity);

    private static AuditEntry Decision(int n = 0) => new(Noon.AddSeconds(n), AuditEvents.ApprovalRecorded, "alice-1", $"order-{n}", null);

    private static AuditEntry Refusal(int n = 0) => new(Noon.AddSeconds(n), AuditEvents.ApprovalRefused, "alice-1", null, "stepNotGranted");

    private static async Task<int> Accepted(IAuditLog log, IEnumerable<AuditEntry> entries)
    {
        var accepted = 0;
        foreach (var entry in entries)
        {
            accepted += await log.TryRecordAsync(entry, Ct) ? 1 : 0;
        }
        return accepted;
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task Refusals_stop_at_their_share_even_when_decisions_come_first_and_decisions_still_fit(string kind)
    {
        var log = Make(kind, capacity: 8);
        await Accepted(log, [Decision(1), Decision(2)]);

        var refusals = await Accepted(log, Enumerable.Range(10, 8).Select(Refusal));
        var decisions = await Accepted(log, [Decision(30), Decision(31)]);

        refusals.Should().Be(6, "a capacity of 8 gives refusals 6 places, whatever came before");
        decisions.Should().Be(0, "2 decisions and 6 refusals fill the 8 places");
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task A_capacity_of_one_has_no_place_for_a_refusal_and_one_for_a_decision(string kind)
    {
        var log = Make(kind, capacity: 1);

        (await log.TryRecordAsync(Refusal(), Ct)).Should().BeFalse();
        (await log.TryRecordAsync(Decision(), Ct)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task A_capacity_of_five_gives_refusals_three_places(string kind)
    {
        var log = Make(kind, capacity: 5);

        (await Accepted(log, Enumerable.Range(0, 5).Select(Refusal))).Should().Be(3);
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task A_batch_that_crosses_the_share_or_the_capacity_leaves_nothing(string kind)
    {
        var log = Make(kind, capacity: 4);
        await Accepted(log, [Refusal(1), Refusal(2)]);

        (await log.TryRecordRangeAsync([Refusal(3), Refusal(4)], Ct)).Should().BeFalse();
        (await log.TryRecordRangeAsync([Decision(5), Decision(6), Decision(7)], Ct)).Should().BeFalse();

        log.List().Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task An_empty_batch_is_recorded_even_when_the_trail_is_full(string kind)
    {
        var log = Make(kind, capacity: 1);
        await log.TryRecordAsync(Decision(), Ct);

        (await log.TryRecordRangeAsync([], Ct)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task The_newest_comes_first(string kind)
    {
        var log = Make(kind, capacity: 10);
        await Accepted(log, [Decision(1), Decision(2), Decision(3)]);

        log.List().Select(entry => entry.OrderId).Should().Equal("order-3", "order-2", "order-1");
    }

    [Fact]
    public async Task The_share_of_the_refusals_is_counted_from_the_file_after_a_restart()
    {
        var log = new SqliteAuditLog(_file.Open(), capacity: 8);
        await Accepted(log, Enumerable.Range(0, 6).Select(Refusal));

        var restarted = new SqliteAuditLog(_file.Reopen(), capacity: 8);

        (await restarted.TryRecordAsync(Refusal(9), Ct)).Should().BeFalse();
        (await restarted.TryRecordAsync(Decision(9), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Entries_recorded_at_once_by_several_tasks_never_pass_the_capacity()
    {
        var database = _file.Open();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(n => Task.Run(
            async () => await new SqliteAuditLog(database, capacity: 2).TryRecordAsync(Decision(n), Ct),
            Ct)));

        results.Count(accepted => accepted).Should().Be(2);
        new SqliteAuditLog(database, capacity: 2).List().Should().HaveCount(2);
    }
}
