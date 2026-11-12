using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Test.Tasks;

public sealed class TaskModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid BatchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Head = new('a', 40);

    private static TaskSpec Spec(int sequence, TaskKind kind = TaskKind.ValidatePullRequest, int pullRequest = 1) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{sequence:D12}"), sequence, kind, "example/app", pullRequest, Head, Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static TaskBatch Batch(params TaskSpec[] tasks) =>
        new(BatchId, "repository:example/app", "alice", Now, Now.AddHours(1), tasks.Length == 0 ? [Spec(1), Spec(2, TaskKind.MergePullRequest)] : tasks);

    [Fact]
    public void The_digest_is_the_same_for_the_same_batch_whatever_the_order_of_the_list()
    {
        var one = Batch(Spec(1), Spec(2, TaskKind.MergePullRequest));
        var other = Batch(Spec(2, TaskKind.MergePullRequest), Spec(1));

        other.Digest().Should().Be(one.Digest());
        one.Digest().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Anything_a_runner_acts_on_changes_the_digest()
    {
        var reference = Batch().Digest();

        (Batch() with { Id = Guid.NewGuid() }).Digest().Should().NotBe(reference);
        (Batch() with { WorkItemId = "repository:example/other" }).Digest().Should().NotBe(reference);
        (Batch() with { ApproverSubject = "mallory" }).Digest().Should().NotBe(reference);
        (Batch() with { ExpiresAt = Now.AddHours(2) }).Digest().Should().NotBe(reference);
        (Batch() with { DelegationId = Guid.NewGuid() }).Digest().Should().NotBe(reference);
        Batch(Spec(1), Spec(2, TaskKind.ValidatePullRequest)).Digest().Should().NotBe(reference);
        Batch(Spec(1) with { PullRequest = 2 }, Spec(2, TaskKind.MergePullRequest)).Digest().Should().NotBe(reference);
        Batch(Spec(1) with { HeadSha = new string('b', 40) }, Spec(2, TaskKind.MergePullRequest)).Digest().Should().NotBe(reference);
        Batch(Spec(1) with { Repository = "example/other" }, Spec(2, TaskKind.MergePullRequest)).Digest().Should().NotBe(reference);
        Batch(Spec(1)).Digest().Should().NotBe(reference);
    }

    [Fact]
    public void Times_are_taken_in_whole_seconds()
    {
        (Batch() with { CreatedAt = Now.AddMilliseconds(400) }).Digest().Should().Be(Batch().Digest());
    }

    [Fact]
    public void Absence_and_the_empty_text_do_not_pass_for_each_other()
    {
        (Batch() with { DelegationId = null }).Digest().Should().Be(Batch().Digest());
        (Batch() with { WorkItemId = string.Empty }).Digest().Should().NotBe((Batch() with { WorkItemId = "x" }).Digest());
    }

    [Fact]
    public void A_chain_of_lines_continues_the_digest_of_the_batch()
    {
        var start = Batch().Digest();
        var first = RunEvent.Create(start, 1, Now, RunEvents.Claimed, null, "runner-1");
        var second = RunEvent.Create(first.Hash, 2, Now.AddSeconds(1), RunEvents.TaskDryRun, Spec(1).Id, "would validate");

        RunEvent.Continues(start, 0, [first, second]).Should().BeTrue();
        RunEvent.Continues(first.Hash, 1, [second]).Should().BeTrue();
    }

    [Fact]
    public void A_line_changed_removed_or_moved_breaks_the_chain()
    {
        var start = Batch().Digest();
        var first = RunEvent.Create(start, 1, Now, RunEvents.Claimed, null, "runner-1");
        var second = RunEvent.Create(first.Hash, 2, Now, RunEvents.TaskDryRun, null, "would validate");
        var third = RunEvent.Create(second.Hash, 3, Now, RunEvents.RunCompleted, null, null);

        RunEvent.Continues(start, 0, [first, second with { Detail = "would merge" }, third]).Should().BeFalse("a changed line no longer matches its hash");
        RunEvent.Continues(start, 0, [first, third]).Should().BeFalse("a removed line leaves a gap");
        RunEvent.Continues(start, 0, [second, first]).Should().BeFalse();
        RunEvent.Continues("other", 0, [first]).Should().BeFalse("a chain starts at the digest of its batch");
        RunEvent.Continues(start, 0, [first with { Sequence = 5 }]).Should().BeFalse();
    }

    [Fact]
    public void An_empty_list_continues_any_chain()
    {
        RunEvent.Continues("anything", 7, []).Should().BeTrue();
    }
}
