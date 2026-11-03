using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test;

public sealed class LaunchTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeAuthority _authority = new(Key);
    private readonly FakeKillSwitch _killSwitch = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeNonces _nonces = new();
    private readonly FakePowers _powers = new();

    private static readonly Approver Operator = new("alice", IsPerson: true, IsOperator: true, HasStepUp: false);

    private static OrderRequest Request(bool production = false, StepKind[]? steps = null) =>
        new("repository:example/app", "example/app", "abc1234", "server-1", production, steps ?? [StepKind.MergePullRequests]);

    private OrderIssuer Issuer(IOrderSigner? signer = null) => new(signer ?? _authority, _killSwitch, _audit, _time, new OrderOptions());

    private Launcher Launcher(IOrderVerifier? verifier = null) =>
        new(verifier ?? _authority, _nonces, _powers, _killSwitch, _audit, _time, new LauncherOptions());

    private async Task<SignedOrder> Signed(OrderRequest? request = null, Approver? approver = null)
    {
        var result = await Issuer().IssueAsync(approver ?? Operator, request ?? Request(), TestContext.Current.CancellationToken);
        return result.Order ?? throw new InvalidOperationException($"not issued: {result.Refusal}");
    }

    [Fact]
    public async Task An_operator_gets_an_order_that_the_launcher_accepts_once()
    {
        var order = await Signed();

        var first = await Launcher().CheckAsync(order, TestContext.Current.CancellationToken);
        var second = await Launcher().CheckAsync(order, TestContext.Current.CancellationToken);

        first.Accepted.Should().BeTrue();
        second.Refusal.Should().Be(LaunchRefusal.Replayed);
    }

    [Theory]
    [InlineData(false, false, false, IssueRefusal.NotAPerson)]
    [InlineData(true, false, true, IssueRefusal.NotAnOperator)]
    public async Task Only_a_person_with_the_operator_role_gets_an_order(bool person, bool isOperator, bool stepUp, IssueRefusal expected)
    {
        var result = await Issuer().IssueAsync(new Approver("svc", person, isOperator, stepUp), Request(), TestContext.Current.CancellationToken);

        result.Order.Should().BeNull();
        result.Refusal.Should().Be(expected);
    }

    [Fact]
    public async Task Production_needs_a_step_up_and_other_environments_do_not()
    {
        var withoutStepUp = await Issuer().IssueAsync(Operator, Request(production: true), TestContext.Current.CancellationToken);
        var withStepUp = await Issuer().IssueAsync(Operator with { HasStepUp = true }, Request(production: true), TestContext.Current.CancellationToken);
        var elsewhere = await Issuer().IssueAsync(Operator, Request(production: false), TestContext.Current.CancellationToken);

        withoutStepUp.Refusal.Should().Be(IssueRefusal.StepUpRequired);
        withStepUp.Issued.Should().BeTrue();
        elsewhere.Issued.Should().BeTrue();
    }

    [Fact]
    public async Task An_order_without_any_ticked_step_is_not_issued()
    {
        var result = await Issuer().IssueAsync(Operator, Request(steps: []), TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.NoStepsTicked);
    }

    [Fact]
    public async Task The_kill_switch_stops_issuing_and_launching()
    {
        var order = await Signed();
        _killSwitch.Engaged = true;

        (await Issuer().IssueAsync(Operator, Request(), TestContext.Current.CancellationToken)).Refusal.Should().Be(IssueRefusal.KillSwitchEngaged);
        (await Launcher().CheckAsync(order, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.KillSwitchEngaged);
    }

    [Fact]
    public async Task Without_an_audit_sink_nothing_is_issued_and_nothing_is_launched()
    {
        var order = await Signed();
        _audit.Available = false;

        (await Issuer().IssueAsync(Operator, Request(), TestContext.Current.CancellationToken)).Refusal.Should().Be(IssueRefusal.AuditUnavailable);
        (await Launcher().CheckAsync(order, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.AuditUnavailable);
    }

    [Fact]
    public async Task An_order_whose_content_changed_after_signing_is_refused()
    {
        var order = await Signed();
        var tampered = order with { Order = order.Order with { Repository = "example/other" } };

        (await Launcher().CheckAsync(tampered, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.BadSignature);
    }

    [Fact]
    public async Task An_order_signed_by_someone_else_is_refused()
    {
        var forged = await Issuer(new FakeAuthority(RandomNumberGenerator.GetBytes(32))).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        (await Launcher().CheckAsync(forged.Order!, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.BadSignature);
    }

    [Fact]
    public async Task An_order_expires()
    {
        var order = await Signed();
        _time.Advance(TimeSpan.FromMinutes(5));

        (await Launcher().CheckAsync(order, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.Expired);
    }

    [Fact]
    public async Task An_order_issued_in_the_future_is_refused()
    {
        var ahead = new FakeTimeProvider(_time.GetUtcNow() + TimeSpan.FromMinutes(2));
        var order = (await new OrderIssuer(_authority, _killSwitch, _audit, ahead, new OrderOptions()).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken)).Order!;

        (await Launcher().CheckAsync(order, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.NotYetValid);
    }

    [Fact]
    public async Task An_issuer_cannot_widen_the_life_of_an_order_beyond_the_launchers_bound()
    {
        var generous = new OrderIssuer(_authority, _killSwitch, _audit, _time, new OrderOptions { Lifetime = TimeSpan.FromHours(8) });
        var order = (await generous.IssueAsync(Operator, Request(), TestContext.Current.CancellationToken)).Order!;

        (await Launcher().CheckAsync(order, TestContext.Current.CancellationToken)).Refusal.Should().Be(LaunchRefusal.LifetimeTooLong);
    }

    [Fact]
    public async Task An_order_outside_the_granted_powers_is_refused_and_keeps_its_nonce()
    {
        var order = await Signed();
        _powers.Granted = false;
        var refused = await Launcher().CheckAsync(order, TestContext.Current.CancellationToken);
        _powers.Granted = true;
        var accepted = await Launcher().CheckAsync(order, TestContext.Current.CancellationToken);

        refused.Refusal.Should().Be(LaunchRefusal.OutsideGrantedPowers);
        accepted.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Every_decision_is_recorded_with_who_approved_it()
    {
        var order = await Signed();
        await Launcher().CheckAsync(order, TestContext.Current.CancellationToken);

        _audit.Entries.Select(e => e.Event).Should().Equal("order.issued", "launch.accepted");
        _audit.Entries.Should().OnlyContain(e => e.Subject == "alice");
    }

    [Fact]
    public async Task The_signed_bytes_do_not_depend_on_the_order_of_the_steps()
    {
        var order = (await Signed(Request(steps: [StepKind.Deploy, StepKind.MergePullRequests]))).Order;

        order.Canonical().Should().Equal((order with { Steps = [.. order.Steps.Reverse()] }).Canonical());
    }

    private sealed class FakeAuthority(byte[] key) : IOrderSigner, IOrderVerifier
    {
        public ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Convert.ToBase64String(HMACSHA256.HashData(key, payload)));

        public ValueTask<bool> VerifyAsync(byte[] payload, string signature, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), Convert.FromBase64String(signature)));
    }

    private sealed class FakeKillSwitch : IKillSwitch
    {
        public bool Engaged { get; set; }
    }

    private sealed class FakeAudit : IAuditSink
    {
        public bool Available { get; set; } = true;

        public List<AuditEntry> Entries { get; } = [];

        public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            if (Available)
            {
                Entries.Add(entry);
            }
            return ValueTask.FromResult(Available);
        }
    }

    private sealed class FakeNonces : INonceLedger
    {
        private readonly HashSet<string> _seen = [];

        public ValueTask<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_seen.Add(nonce));
    }

    private sealed class FakePowers : IGrantedPowers
    {
        public bool Granted { get; set; } = true;

        public bool Allows(LaunchOrder order) => Granted;
    }
}
