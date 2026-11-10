using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

public sealed class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_fifth_attempt_is_counted_and_the_sixth_is_refused_whether_the_name_exists_or_not()
    {
        var throttle = new LoginThrottle(_time);

        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            throttle.TryReserve("alice").Should().BeTrue();
        }

        throttle.TryReserve("alice").Should().BeFalse();
        throttle.TryReserve("ALICE").Should().BeFalse("a name is compared without its case");
        throttle.TryReserve("bob").Should().BeTrue();
    }

    [Fact]
    public void A_lockout_ends_when_its_attempts_age_out_and_a_success_forgives_the_failures()
    {
        var throttle = new LoginThrottle(_time);
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            throttle.TryReserve("alice");
        }

        throttle.LockedFor("alice").Should().BeCloseTo(LoginThrottle.Window, TimeSpan.FromSeconds(1));
        _time.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        throttle.TryReserve("alice").Should().BeTrue();

        throttle.Succeeded("alice");
        throttle.LockedFor("alice").Should().BeNull();
    }

    [Fact]
    public void Made_up_names_never_lift_the_lockout_of_an_account()
    {
        var throttle = new LoginThrottle(_time);
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            throttle.TryReserve("alice", isAccount: true);
        }

        for (var guess = 0; guess < LoginThrottle.MaxTracked + 50; guess++)
        {
            throttle.TryReserve($"guess-{guess}");
        }

        throttle.TryReserve("alice", isAccount: true).Should().BeFalse("the count of an account is never forgotten to make room");
    }
}
