using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteKillSwitchTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public async Task A_new_database_starts_released_nothing_was_ever_stopped()
    {
        var stop = new SqliteKillSwitch(_file.Open());

        stop.Engaged.Should().BeFalse();
        (await stop.IsEngagedAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public void An_engaged_stop_is_still_engaged_after_a_restart()
    {
        new SqliteKillSwitch(_file.Open()).Set(true);

        new SqliteKillSwitch(_file.Open()).Engaged.Should().BeTrue();
    }

    [Fact]
    public void A_released_stop_is_still_released_after_a_restart()
    {
        var stop = new SqliteKillSwitch(_file.Open());
        stop.Set(true);
        stop.Set(false);

        new SqliteKillSwitch(_file.Open()).Engaged.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_means_engaged_a_state_that_cannot_be_read_counts_as_engaged()
    {
        var stop = new SqliteKillSwitch(_file.Open());
        _file.Damage();

        stop.Engaged.Should().BeTrue();
        (await stop.IsEngagedAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public void Engaging_always_works_even_when_it_cannot_be_saved()
    {
        var stop = new SqliteKillSwitch(_file.Open());
        _file.Damage();

        var engage = () => stop.Set(true);

        engage.Should().NotThrow();
        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public void Releasing_a_stop_that_cannot_be_saved_leaves_it_engaged_and_says_so()
    {
        var stop = new SqliteKillSwitch(_file.Open());
        stop.Set(true);
        _file.Damage();

        var release = () => stop.Set(false);

        release.Should().Throw<KillSwitchUnavailableException>();
        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public void An_engagement_that_could_not_be_saved_holds_until_a_release_is_saved()
    {
        var stop = new SqliteKillSwitch(_file.Open());
        _file.Damage();
        stop.Set(true);
        _file.Repair();
        _file.Open();

        // The database is readable again, and says released: the stop still holds, since nothing saved the release
        stop.Engaged.Should().BeTrue();
        stop.Set(false);
        stop.Engaged.Should().BeFalse();
    }
}
