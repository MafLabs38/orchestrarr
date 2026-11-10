using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Settings;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Test.Settings;

/// <summary>The rule that keeps the options safe (ADR 0023): lowering security needs the leave of the host, is traced first, and an unreadable store means the safe value.</summary>
public sealed class OptionsServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryOptionStore _store = new();
    private readonly InMemoryAuditLog _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OptionsService Service(bool leave = true, bool configRequires = true, IOptionStore? store = null, IAuditSink? audit = null) =>
        new(store ?? _store, audit ?? _audit, _time, new OptionsSettings(leave), new TaskOptions { RequireSecondFactor = configRequires });

    [Fact]
    public void The_code_is_required_until_someone_with_the_leave_of_the_host_turns_it_off()
    {
        Service().Required.Should().BeTrue();
        Service().List().Should().ContainSingle().Which.Should().Match<OptionState>(state => state.Value && state.CanTurnOff && state.LockedReason == null);
    }

    [Fact]
    public async Task Turning_it_off_with_the_leave_of_the_host_is_traced_first_and_takes_effect_at_once()
    {
        var service = Service();

        (await service.SetAsync(OptionIds.RequireSecondFactor, false, "root", Ct)).Should().BeNull();

        service.Required.Should().BeFalse();
        _audit.List().Should().ContainSingle(entry => entry.Event == OptionsService.Changed && entry.Subject == "root" && entry.Detail!.Contains("off", StringComparison.Ordinal));
        (await service.SetAsync(OptionIds.RequireSecondFactor, true, "root", Ct)).Should().BeNull();
        service.Required.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_without_the_leave_refuses_to_turn_it_off_whoever_asks_and_changes_nothing()
    {
        var service = Service(leave: false);

        (await service.SetAsync(OptionIds.RequireSecondFactor, false, "root", Ct)).Should().Be(OptionRefusal.LockedByHost);

        service.Required.Should().BeTrue();
        _store.Get(OptionIds.RequireSecondFactor).Should().BeNull();
        _audit.List().Should().BeEmpty();
        service.List().Single().Should().Match<OptionState>(state => !state.CanTurnOff && state.LockedReason == "host");
    }

    [Fact]
    public void A_value_written_by_other_means_changes_nothing_while_the_host_has_no_leave()
    {
        _store.TrySet(OptionIds.RequireSecondFactor, false, Start, "someone with the file");

        Service(leave: false).Required.Should().BeTrue("the stored value is not even read");
        Service(leave: true).Required.Should().BeFalse("the same file with the leave of the host is what the screen would have written");
    }

    [Fact]
    public void An_unreadable_store_reads_as_the_safe_value()
    {
        var store = Substitute.For<IOptionStore>();
        store.Get(Arg.Any<string>()).Returns(_ => throw new StorageUnavailableException());

        Service(store: store).Required.Should().BeTrue();
    }

    [Fact]
    public async Task A_change_that_cannot_be_traced_or_saved_is_not_made()
    {
        var audit = Substitute.For<IAuditSink>();
        audit.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(false);
        (await Service(audit: audit).SetAsync(OptionIds.RequireSecondFactor, false, "root", Ct)).Should().Be(OptionRefusal.Unavailable);
        _store.Get(OptionIds.RequireSecondFactor).Should().BeNull("nothing is written when the trail cannot take it");

        var store = Substitute.For<IOptionStore>();
        store.TrySet(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>()).Returns(false);
        (await Service(store: store).SetAsync(OptionIds.RequireSecondFactor, false, "root", Ct)).Should().Be(OptionRefusal.Unavailable);
    }

    [Fact]
    public async Task What_the_configuration_fixes_the_screen_cannot_change_in_either_direction()
    {
        var service = Service(configRequires: false);

        service.Required.Should().BeFalse();
        (await service.SetAsync(OptionIds.RequireSecondFactor, true, "root", Ct)).Should().Be(OptionRefusal.SetByConfiguration);
        (await service.SetAsync(OptionIds.RequireSecondFactor, false, "root", Ct)).Should().Be(OptionRefusal.SetByConfiguration);
        service.List().Single().Should().Match<OptionState>(state => !state.CanTurnOff && state.LockedReason == "configuration");
    }

    [Fact]
    public async Task An_unknown_option_is_refused()
    {
        (await Service().SetAsync("nothing.here", false, "root", Ct)).Should().Be(OptionRefusal.UnknownOption);
    }
}
