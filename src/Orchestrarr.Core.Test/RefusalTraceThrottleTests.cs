using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Core.Test;

public sealed class RefusalTraceThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_refusal_of_the_same_person_is_traced_once_per_window_and_again_exactly_at_its_end()
    {
        var time = new FakeTimeProvider(Now);
        var throttle = new RefusalTraceThrottle(time, TimeSpan.FromMinutes(10));

        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeFalse("9:59 is still inside the window");
        time.Advance(TimeSpan.FromSeconds(1));
        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeTrue("10:00 is outside it");
    }

    [Fact]
    public void Another_person_or_another_reason_is_its_own_claim()
    {
        var throttle = new RefusalTraceThrottle(new FakeTimeProvider(Now));

        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeTrue();
        throttle.TryClaim("reader-2", ApprovalRefusal.NotAnOperator).Should().BeTrue();
        throttle.TryClaim("reader-1", ApprovalRefusal.KillSwitchEngaged).Should().BeTrue();
    }

    [Fact]
    public void A_claim_whose_trace_could_not_be_written_is_given_back()
    {
        var throttle = new RefusalTraceThrottle(new FakeTimeProvider(Now));
        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeTrue();

        throttle.Release("reader-1", ApprovalRefusal.NotAnOperator);

        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator).Should().BeTrue();
    }

    [Fact]
    public void What_it_remembers_is_bounded_and_a_refusal_it_cannot_remember_is_traced_all_the_same()
    {
        var throttle = new RefusalTraceThrottle(new FakeTimeProvider(Now), memory: 3);

        for (var n = 0; n < 3; n++)
        {
            throttle.TryClaim($"reader-{n}", ApprovalRefusal.NotAnOperator).Should().BeTrue();
        }

        throttle.TryClaim("reader-9", ApprovalRefusal.NotAnOperator).Should().BeTrue("the memory is full of recent claims: traced, not dropped");
        throttle.TryClaim("reader-9", ApprovalRefusal.NotAnOperator).Should().BeTrue("and not remembered either");
        throttle.TryClaim("reader-0", ApprovalRefusal.NotAnOperator).Should().BeFalse("the ones remembered still count");
    }

    [Fact]
    public void Old_claims_are_forgotten_to_make_room()
    {
        var time = new FakeTimeProvider(Now);
        var throttle = new RefusalTraceThrottle(time, TimeSpan.FromMinutes(10), memory: 2);
        throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator);
        throttle.TryClaim("reader-2", ApprovalRefusal.NotAnOperator);
        time.Advance(TimeSpan.FromMinutes(11));

        throttle.TryClaim("reader-3", ApprovalRefusal.NotAnOperator).Should().BeTrue();
        throttle.TryClaim("reader-3", ApprovalRefusal.NotAnOperator).Should().BeFalse("the old ones made room, so this one is remembered");
    }

    [Fact]
    public async Task Identical_refusals_at_once_are_traced_once()
    {
        var throttle = new RefusalTraceThrottle(new FakeTimeProvider(Now));

        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => throttle.TryClaim("reader-1", ApprovalRefusal.NotAnOperator), TestContext.Current.CancellationToken)));

        claims.Count(claimed => claimed).Should().Be(1);
    }
}
