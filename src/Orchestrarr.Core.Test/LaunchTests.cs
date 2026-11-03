using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test;

public sealed class LaunchTests
{
    private const string Production = "production-1";
    private static readonly string Sha = new('a', 40);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeAuthority _authority = new();
    private readonly FakeKillSwitch _killSwitch = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeNonces _nonces = new();
    private readonly FakePowers _powers = new();
    private readonly FakeEnvironments _environments = new();

    private static Approver Operator => new("alice", IsPerson: true, IsOperator: true, StepUpAt: null);

    private Approver WithStepUp(TimeSpan age) => Operator with { StepUpAt = _time.GetUtcNow() - age };

    private static OrderRequest Request(string? environment = "server-1", StepKind[]? steps = null) =>
        new("repository:example/app", "example/app", Sha, environment, steps ?? [StepKind.MergePullRequests]);

    private OrderIssuer Issuer(IOrderSigner? signer = null, OrderOptions? options = null) =>
        new(signer ?? _authority, _killSwitch, _audit, _environments, _time, options ?? new OrderOptions());

    private Launcher Launcher(IOrderVerifier? verifier = null, LauncherOptions? options = null) =>
        new(verifier ?? _authority.Verifier, _nonces, new LaunchPolicy(_powers, _environments), _killSwitch, _audit, _time, options ?? new LauncherOptions());

    private async Task<SignedOrder> Signed(OrderRequest? request = null, Approver? approver = null)
    {
        var result = await Issuer().IssueAsync(approver ?? Operator, request ?? Request(), TestContext.Current.CancellationToken);
        return result.Order ?? throw new InvalidOperationException($"not issued: {result.Refusal}");
    }

    private async Task<SignedOrder> Signed(OrderOptions options)
    {
        var issued = await Issuer(options: options).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);
        return issued.Order!;
    }

    // An order that did not come from the issuer: what a compromised store or a buggy issuer could hand the launcher, validly signed.
    private async Task<SignedOrder> SignedDirectly(Func<LaunchOrder, LaunchOrder> change, OrderRequest? request = null)
    {
        var order = change((await Signed(request)).Order);
        return new SignedOrder(order, await _authority.SignAsync(order.Canonical(), TestContext.Current.CancellationToken));
    }

    private ValueTask<LaunchDecision> Check(SignedOrder order, Launcher? launcher = null) =>
        (launcher ?? Launcher()).CheckAsync(order, TestContext.Current.CancellationToken);

    // --- the happy path

    [Fact]
    public async Task An_operator_gets_an_order_that_the_launcher_accepts_once()
    {
        var order = await Signed();

        var first = await Check(order);
        var second = await Check(order);

        first.Accepted.Should().BeTrue();
        second.Refusal.Should().Be(LaunchRefusal.Replayed);
        second.Order.Should().BeNull();
    }

    [Fact]
    public async Task The_launcher_answers_with_the_verified_order_in_whole_seconds_and_sorted_steps()
    {
        var order = await Signed(Request(steps: [StepKind.Deploy, StepKind.MergePullRequests]));
        var sloppy = order with { Order = order.Order with { IssuedAt = order.Order.IssuedAt.AddTicks(7), Steps = [.. order.Order.Steps.Reverse()] } };

        var decision = await Check(sloppy);

        decision.Accepted.Should().BeTrue();
        decision.Order!.IssuedAt.Should().Be(order.Order.IssuedAt);
        decision.Order.Steps.Should().Equal(StepKind.MergePullRequests, StepKind.Deploy);
    }

    [Fact]
    public async Task Two_orders_never_share_an_id()
    {
        (await Signed()).Order.Id.Should().NotBe((await Signed()).Order.Id);
    }

    [Fact]
    public async Task The_steps_are_copied_so_the_caller_cannot_change_them_after_signing()
    {
        var steps = new List<StepKind> { StepKind.MergePullRequests };
        var order = await Signed(Request() with { Steps = steps });
        steps.Add(StepKind.Deploy);

        order.Order.Steps.Should().Equal(StepKind.MergePullRequests);
    }

    // --- who may give an order

    [Theory]
    [InlineData(false, true, IssueRefusal.NotAPerson)]
    [InlineData(true, false, IssueRefusal.NotAnOperator)]
    public async Task Only_a_person_with_the_operator_role_gets_an_order(bool person, bool isOperator, IssueRefusal expected)
    {
        var result = await Issuer().IssueAsync(new Approver("svc", person, isOperator, _time.GetUtcNow()), Request(), TestContext.Current.CancellationToken);

        result.Order.Should().BeNull();
        result.Refusal.Should().Be(expected);
    }

    [Fact]
    public async Task Production_needs_a_fresh_step_up_and_the_server_decides_what_production_is()
    {
        var none = await Issuer().IssueAsync(Operator, Request(Production), TestContext.Current.CancellationToken);
        var stale = await Issuer().IssueAsync(WithStepUp(TimeSpan.FromMinutes(6)), Request(Production), TestContext.Current.CancellationToken);
        var fresh = await Issuer().IssueAsync(WithStepUp(TimeSpan.FromMinutes(1)), Request(Production), TestContext.Current.CancellationToken);
        var elsewhere = await Issuer().IssueAsync(Operator, Request("server-1"), TestContext.Current.CancellationToken);
        var noEnvironment = await Issuer().IssueAsync(Operator, Request(null), TestContext.Current.CancellationToken);

        none.Refusal.Should().Be(IssueRefusal.StepUpRequired);
        stale.Refusal.Should().Be(IssueRefusal.StepUpRequired);
        fresh.Issued.Should().BeTrue();
        elsewhere.Issued.Should().BeTrue();
        noEnvironment.Issued.Should().BeTrue();
    }

    [Fact]
    public async Task An_environment_the_description_does_not_know_counts_as_production()
    {
        _environments.UnknownIsProduction = true;

        var result = await Issuer().IssueAsync(Operator, Request("never-heard-of"), TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.StepUpRequired);
    }

    [Fact]
    public async Task The_launcher_checks_the_step_up_again_for_production()
    {
        var withoutStepUp = await SignedDirectly(o => o with { Environment = Production });
        var withStepUp = await SignedDirectly(o => o with { Environment = Production, StepUpAt = o.IssuedAt - TimeSpan.FromMinutes(1) });

        (await Check(withoutStepUp)).Refusal.Should().Be(LaunchRefusal.StepUpRequired);
        (await Check(withStepUp)).Accepted.Should().BeTrue();
    }

    // --- what an order may say

    [Theory]
    [InlineData("example/app\nevil/repo", "abc")]
    [InlineData("example/app", "abc1234")]
    [InlineData("example", "x")]
    public async Task A_request_with_a_malformed_field_is_refused_before_signing(string repository, string head)
    {
        var request = Request() with { Repository = repository, HeadSha = head == "x" ? Sha : head };
        var counting = new CountingSigner(_authority);

        var result = await Issuer(counting).IssueAsync(Operator, request, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.InvalidRequest);
        counting.Signatures.Should().Be(0);
    }

    [Fact]
    public async Task A_trailing_newline_does_not_pass_for_a_valid_commit()
    {
        var result = await Issuer().IssueAsync(Operator, Request() with { HeadSha = Sha + "\n" }, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.InvalidRequest);
    }

    [Fact]
    public async Task No_step_an_unknown_step_or_a_repeated_step_is_refused()
    {
        foreach (var steps in new[] { Array.Empty<StepKind>(), [(StepKind)99], [StepKind.Deploy, StepKind.Deploy] })
        {
            (await Issuer().IssueAsync(Operator, Request(steps: steps), TestContext.Current.CancellationToken)).Refusal.Should().Be(IssueRefusal.InvalidRequest);
        }
    }

    [Fact]
    public async Task The_launcher_refuses_a_validly_signed_order_whose_fields_are_malformed()
    {
        var shifted = await SignedDirectly(o => o with { WorkItemId = "w\nevil/repo" });
        var noSteps = await SignedDirectly(o => o with { Steps = [] });

        (await Check(shifted)).Refusal.Should().Be(LaunchRefusal.Malformed);
        (await Check(noSteps)).Refusal.Should().Be(LaunchRefusal.Malformed);
    }

    // --- the signed bytes

    [Fact]
    public async Task A_newline_cannot_move_a_field_boundary()
    {
        var order = (await Signed()).Order;
        var one = order with { WorkItemId = "a\nb", Repository = "c" };
        var other = order with { WorkItemId = "a", Repository = "b\nc" };

        one.Canonical().Should().NotEqual(other.Canonical());
    }

    [Fact]
    public async Task Absence_and_the_empty_string_are_different_bytes()
    {
        var order = (await Signed()).Order;

        (order with { Environment = null }).Canonical().Should().NotEqual((order with { Environment = string.Empty }).Canonical());
    }

    [Fact]
    public async Task The_order_of_the_steps_and_repeats_do_not_change_the_bytes()
    {
        var order = (await Signed(Request(steps: [StepKind.Deploy, StepKind.MergePullRequests]))).Order;

        (order with { Steps = [.. order.Steps.Reverse()] }).Canonical().Should().Equal(order.Canonical());
        (order with { Steps = [.. order.Steps, StepKind.Deploy] }).Canonical().Should().Equal(order.Canonical());
    }

    public static TheoryData<string> Tampers => new() { "work", "repository", "head", "environment", "steps", "approver", "stepup", "audience", "expiry", "id" };

    [Theory]
    [MemberData(nameof(Tampers))]
    public async Task An_order_changed_after_signing_in_any_field_is_refused(string field)
    {
        var order = await Signed();
        var tampered = field switch
        {
            "work" => order.Order with { WorkItemId = "repository:example/other" },
            "repository" => order.Order with { Repository = "example/other" },
            "head" => order.Order with { HeadSha = new string('b', 40) },
            "environment" => order.Order with { Environment = Production },
            "steps" => order.Order with { Steps = [StepKind.MergePullRequests, StepKind.Deploy] },
            "approver" => order.Order with { ApproverSubject = "mallory" },
            "stepup" => order.Order with { StepUpAt = order.Order.IssuedAt },
            "audience" => order.Order with { Audience = "other" },
            "expiry" => order.Order with { ExpiresAt = order.Order.ExpiresAt.AddHours(1) },
            _ => order.Order with { Id = Guid.NewGuid() },
        };

        (await Check(order with { Order = tampered })).Refusal.Should().Be(LaunchRefusal.BadSignature);
    }

    [Fact]
    public async Task An_order_signed_by_someone_else_is_refused()
    {
        var forged = await Issuer(new FakeAuthority()).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        (await Check(forged.Order!)).Refusal.Should().Be(LaunchRefusal.BadSignature);
    }

    [Theory]
    [InlineData("!!")]
    [InlineData("")]
    public async Task A_malformed_signature_is_refused_without_an_exception(string signature)
    {
        var order = await Signed();

        (await Check(order with { Signature = signature })).Refusal.Should().Be(LaunchRefusal.BadSignature);
    }

    [Fact]
    public async Task A_verifier_that_cannot_answer_never_lets_an_order_through()
    {
        var order = await Signed();

        (await Check(order, Launcher(new ThrowingVerifier()))).Refusal.Should().Be(LaunchRefusal.VerifierUnavailable);
    }

    // --- time and audience

    [Fact]
    public async Task An_order_expires()
    {
        var order = await Signed();
        _time.Advance(TimeSpan.FromMinutes(5));

        (await Check(order)).Refusal.Should().Be(LaunchRefusal.Expired);
    }

    [Fact]
    public async Task An_issue_time_inside_the_clock_tolerance_is_accepted_and_outside_it_is_not()
    {
        var inside = await IssuedAhead(TimeSpan.FromSeconds(29));
        var outside = await IssuedAhead(TimeSpan.FromSeconds(31));

        (await Check(inside)).Accepted.Should().BeTrue();
        (await Check(outside)).Refusal.Should().Be(LaunchRefusal.NotYetValid);
    }

    private async Task<SignedOrder> IssuedAhead(TimeSpan ahead)
    {
        var clock = new FakeTimeProvider(_time.GetUtcNow() + ahead);
        var issuer = new OrderIssuer(_authority, _killSwitch, _audit, _environments, clock, new OrderOptions());
        return (await issuer.IssueAsync(Operator, Request(), TestContext.Current.CancellationToken)).Order!;
    }

    [Fact]
    public async Task The_launcher_bounds_the_life_of_an_order_whatever_the_issuer_says()
    {
        var atTheBound = await Signed(options: new OrderOptions { Lifetime = TimeSpan.FromMinutes(10) });
        var over = await Signed(options: new OrderOptions { Lifetime = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1) });

        (await Check(atTheBound)).Accepted.Should().BeTrue();
        (await Check(over)).Refusal.Should().Be(LaunchRefusal.LifetimeTooLong);
    }

    [Fact]
    public async Task An_order_for_another_launcher_is_refused()
    {
        var order = await Signed(options: new OrderOptions { Audience = "another-launcher" });

        (await Check(order)).Refusal.Should().Be(LaunchRefusal.WrongAudience);
    }

    [Fact]
    public void The_default_life_of_an_order_fits_inside_the_launchers_bound()
    {
        new OrderOptions().Lifetime.Should().BeLessThanOrEqualTo(new LauncherOptions().MaxLifetime);
    }

    // --- powers and single use

    [Fact]
    public async Task An_order_outside_the_granted_powers_is_refused_and_keeps_its_id_for_later()
    {
        var order = await Signed();
        _powers.Granted = false;
        var refused = await Check(order);
        _powers.Granted = true;
        var accepted = await Check(order);

        refused.Refusal.Should().Be(LaunchRefusal.OutsideGrantedPowers);
        accepted.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task An_order_refused_for_any_reason_before_the_last_check_is_not_recorded_as_used()
    {
        var order = await Signed();
        var expired = await Signed();
        _time.Advance(TimeSpan.FromMinutes(6));
        await Check(order with { Signature = "!!" });
        await Check(expired);
        _powers.Granted = false;
        await Check(await SignedDirectly(o => o));

        _nonces.Seen.Should().BeEmpty();
    }

    // --- fail closed: kill switch, audit, signer

    [Fact]
    public async Task The_kill_switch_stops_issuing()
    {
        _killSwitch.Engaged = true;
        var counting = new CountingSigner(_authority);

        var result = await Issuer(counting).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.KillSwitchEngaged);
        counting.Signatures.Should().Be(0);
    }

    [Fact]
    public async Task The_kill_switch_stops_launching()
    {
        var order = await Signed();
        _killSwitch.Engaged = true;

        (await Check(order)).Refusal.Should().Be(LaunchRefusal.KillSwitchEngaged);
    }

    [Fact]
    public async Task A_kill_switch_engaged_while_the_order_is_signed_means_no_order()
    {
        var result = await Issuer(new CallbackSigner(_authority, () => _killSwitch.Engaged = true))
            .IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        result.Order.Should().BeNull();
        result.Refusal.Should().Be(IssueRefusal.KillSwitchEngaged);
    }

    [Fact]
    public async Task A_kill_switch_engaged_during_the_launch_checks_refuses_the_launch()
    {
        var order = await Signed();
        _powers.OnCheck = () => _killSwitch.Engaged = true;

        (await Check(order)).Refusal.Should().Be(LaunchRefusal.KillSwitchEngaged);
    }

    [Fact]
    public async Task Without_an_audit_sink_nothing_is_signed()
    {
        _audit.Available = false;
        var counting = new CountingSigner(_authority);

        var result = await Issuer(counting).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(IssueRefusal.AuditUnavailable);
        result.Order.Should().BeNull();
        counting.Signatures.Should().Be(0);
    }

    [Fact]
    public async Task Without_an_audit_sink_nothing_is_launched_and_the_id_stays_spent()
    {
        var order = await Signed();
        _audit.Available = false;

        var decision = await Check(order);

        decision.Refusal.Should().Be(LaunchRefusal.AuditUnavailable);
        decision.Order.Should().BeNull();
        _nonces.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task The_signing_is_recorded_before_it_happens()
    {
        var recordedWhenSigning = new List<string>();
        var signer = new CallbackSigner(_authority, () => recordedWhenSigning.AddRange(_audit.Entries.Select(e => e.Event)));

        await Issuer(signer).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        recordedWhenSigning.Should().Equal(AuditEvents.Signing);
    }

    [Fact]
    public async Task A_signer_that_cannot_answer_gives_no_order_and_leaves_a_trace()
    {
        var result = await Issuer(new ThrowingSigner()).IssueAsync(Operator, Request(), TestContext.Current.CancellationToken);

        result.Order.Should().BeNull();
        result.Refusal.Should().Be(IssueRefusal.SignerUnavailable);
        _audit.Entries.Select(e => e.Event).Should().Equal(AuditEvents.Signing, AuditEvents.SigningFailed);
    }

    // --- the trail

    [Fact]
    public async Task An_accepted_order_leaves_what_was_approved_and_by_whom()
    {
        var order = await Signed();
        await Check(order);

        _audit.Entries.Select(e => e.Event).Should().Equal(AuditEvents.Signing, AuditEvents.Issued, AuditEvents.LaunchAccepted);
        _audit.Entries.Should().OnlyContain(e => e.Subject == "alice");
        _audit.Entries[^1].Detail.Should().Contain(Sha).And.Contain("example/app").And.Contain("MergePullRequests");
    }

    [Fact]
    public async Task A_refused_issue_is_recorded_with_its_code()
    {
        await Issuer().IssueAsync(new Approver("svc", IsPerson: false, IsOperator: true, StepUpAt: null), Request(), TestContext.Current.CancellationToken);

        var entry = _audit.Entries.Should().ContainSingle().Which;
        entry.Event.Should().Be(AuditEvents.IssueRefused);
        entry.Detail.Should().Be(nameof(IssueRefusal.NotAPerson));
    }

    [Fact]
    public async Task A_refused_launch_of_a_trusted_order_names_who_approved_it()
    {
        var order = await Signed();
        _powers.Granted = false;
        await Check(order);

        var entry = _audit.Entries[^1];
        entry.Event.Should().Be(AuditEvents.LaunchRefused);
        entry.Subject.Should().Be("alice");
        entry.Detail.Should().Contain(nameof(LaunchRefusal.OutsideGrantedPowers));
    }

    [Fact]
    public async Task What_an_unverified_order_claims_never_reaches_the_trail()
    {
        var order = await Signed();
        var forged = order with { Order = order.Order with { ApproverSubject = "mallory", Repository = "evil/repo\nforged line" } };
        await Check(forged);

        var entry = _audit.Entries[^1];
        entry.Subject.Should().Be("unverified");
        entry.OrderId.Should().BeNull();
        entry.Detail.Should().Be(nameof(LaunchRefusal.BadSignature));
    }

    // --- structure

    [Fact]
    public void The_launcher_has_no_way_to_sign_and_the_issuer_none_to_verify()
    {
        Dependencies<Launcher>().Should().NotContain(typeof(IOrderSigner));
        Dependencies<OrderIssuer>().Should().NotContain(typeof(IOrderVerifier));
    }

    private static IEnumerable<Type> Dependencies<T>() => typeof(T).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);

    // --- fakes

    // Signs with a private key and verifies with the public one, as Vault transit does: the launcher cannot forge what it verifies.
    private sealed class FakeAuthority : IOrderSigner
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public FakeAuthority()
        {
            Verifier = new PublicVerifier(_key.ExportSubjectPublicKeyInfo());
        }

        public IOrderVerifier Verifier { get; }

        public ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256)));
    }

    private sealed class PublicVerifier : IOrderVerifier
    {
        private readonly ECDsa _publicKey = ECDsa.Create();

        public PublicVerifier(byte[] subjectPublicKeyInfo)
        {
            _publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        }

        public ValueTask<bool> VerifyAsync(byte[] payload, string signature, CancellationToken cancellationToken)
        {
            try
            {
                return ValueTask.FromResult(_publicKey.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256));
            }
            catch (FormatException)
            {
                return ValueTask.FromResult(false);
            }
        }
    }

    private sealed class ThrowingVerifier : IOrderVerifier
    {
        public ValueTask<bool> VerifyAsync(byte[] payload, string signature, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the vault is sealed");
    }

    private sealed class ThrowingSigner : IOrderSigner
    {
        public ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the vault is sealed");
    }

    private sealed class CountingSigner(IOrderSigner inner) : IOrderSigner
    {
        public int Signatures { get; private set; }

        public ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken)
        {
            Signatures++;
            return inner.SignAsync(payload, cancellationToken);
        }
    }

    private sealed class CallbackSigner(IOrderSigner inner, Action onSign) : IOrderSigner
    {
        public ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken)
        {
            onSign();
            return inner.SignAsync(payload, cancellationToken);
        }
    }

    private sealed class FakeKillSwitch : IKillSwitch
    {
        public bool Engaged { get; set; }

        public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);
    }

    private sealed class FakeAudit : IAuditSink
    {
        public bool Available { get; set; } = true;

        public List<AuditEntry> Entries { get; } = [];

        public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken) => TryRecordRangeAsync([entry], cancellationToken);

        public ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
        {
            if (Available)
            {
                Entries.AddRange(entries);
            }
            return ValueTask.FromResult(Available);
        }
    }

    private sealed class FakeNonces : INonceLedger
    {
        public HashSet<string> Seen { get; } = [];

        public ValueTask<bool> TryConsumeAsync(string orderId, DateTimeOffset retainUntil, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Seen.Add(orderId));
    }

    private sealed class FakePowers : IGrantedPowers
    {
        public bool Granted { get; set; } = true;

        public Action? OnCheck { get; set; }

        public ValueTask<bool> AllowsAsync(LaunchOrder order, CancellationToken cancellationToken)
        {
            OnCheck?.Invoke();
            return ValueTask.FromResult(Granted);
        }
    }

    private sealed class FakeEnvironments : IEnvironmentClassifier
    {
        public bool UnknownIsProduction { get; set; }

        public ValueTask<bool> IsProductionAsync(string environment, CancellationToken cancellationToken) =>
            ValueTask.FromResult(environment == Production || (UnknownIsProduction && environment != "server-1"));
    }
}
