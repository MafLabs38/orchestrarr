using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ApprovalCertificateTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly TestAuthority _authority;

    public ApprovalCertificateTests() => _authority = new TestAuthority(_time);

    public void Dispose() => _authority.Dispose();

    private static ApprovalRequest Request(World max = World.Git) => new(CertificateKind.Problem, "problem-1", "shop", ["example/shop"], max, new MissionBudget(20, 3, 50), TimeSpan.FromHours(1));

    private static Mandate MandateFor(SignedApproval approval, World world = World.Git, Func<Mandate, Mandate>? change = null)
    {
        var mandate = new Mandate("m-1", "mission-1", "problem-1", world, "runner-git", [GatedAct.OpenPullRequest], new MissionFrame(["example/shop"], [".github/**", "**/*.enc.*", "**/.env*"], "Git:shop"), new MissionBudget(10, 2, 20),
            approval.Certificate.IssuedAt, approval.Certificate.IssuedAt.AddMinutes(30), MandateSource.PlanValidation, "nonce-1", approval.Certificate.Id, "shop", "deprecated-dependency");
        return change?.Invoke(mandate) ?? mandate;
    }

    [Fact]
    public async Task The_authority_signs_a_certificate_only_for_a_valid_second_factor_code_and_a_code_is_good_once_for_an_operator()
    {
        var good = _authority.Approve(Request());
        good.Result.Should().Be(SecondFactorResult.Verified);
        good.Approval!.Certificate.Approver.Should().StartWith("alice-");

        _authority.Approve(Request(), code: "000000").Approval.Should().BeNull();
        _authority.Approve(Request(), code: "abc").Result.Should().Be(SecondFactorResult.Malformed);

        var once = await _authority.Authority.ProposeAndApproveAsync(Request(), "bob", _authority.CodeFor(), TestContext.Current.CancellationToken);
        var again = await _authority.Authority.ProposeAndApproveAsync(Request(), "bob", _authority.CodeFor(), TestContext.Current.CancellationToken);
        once.Approval.Should().NotBeNull();
        again.Approval.Should().BeNull("the same code is not good twice");
        again.Result.Should().Be(SecondFactorResult.Reused);
    }

    [Fact]
    public void A_request_that_asks_for_nothing_is_refused_before_the_code_is_spent()
    {
        _authority.Approve(Request() with { Repositories = [] }).Approval.Should().BeNull();
        _authority.Approve(Request() with { Budget = new MissionBudget(0, 1, 1) }).Approval.Should().BeNull();
        _authority.Approve(Request() with { Lifetime = TimeSpan.Zero }).Approval.Should().BeNull();
    }

    [Fact]
    public void A_mandate_that_falls_inside_the_certificate_is_accepted_and_every_way_of_going_beyond_it_is_named()
    {
        var approval = _authority.Approve(Request(World.Git)).Approval!;

        _authority.Verifier.Check(approval, MandateFor(approval)).Should().Be(ApprovalRefusal.None);
        _authority.Verifier.Check(approval, MandateFor(approval, World.Release)).Should().Be(ApprovalRefusal.WorldAboveApproval);
        _authority.Verifier.Check(approval, MandateFor(approval, change: m => m with { Frame = m.Frame with { Repositories = ["example/other"] } })).Should().Be(ApprovalRefusal.RepositoryNotApproved);
        _authority.Verifier.Check(approval, MandateFor(approval, change: m => m with { Budget = new MissionBudget(99, 2, 20) })).Should().Be(ApprovalRefusal.BudgetAboveApproval);
        _authority.Verifier.Check(approval, MandateFor(approval, change: m => m with { ExpiresAt = approval.Certificate.ExpiresAt.AddMinutes(1) })).Should().Be(ApprovalRefusal.OutlivesApproval);
        _authority.Verifier.Check(approval, MandateFor(approval, change: m => m with { ProblemId = "problem-2" })).Should().Be(ApprovalRefusal.WrongProblem);
        _authority.Verifier.Check(approval, MandateFor(approval, change: m => m with { ApprovalId = "another" })).Should().Be(ApprovalRefusal.NotTheApprovalOfThisMandate);
        _authority.Verifier.Check(null, MandateFor(approval)).Should().Be(ApprovalRefusal.Missing);
    }

    [Fact]
    public async Task A_certificate_that_was_changed_or_made_by_another_key_or_that_expired_is_refused()
    {
        var approval = _authority.Approve(Request(World.Git)).Approval!;
        var widened = approval with { Certificate = approval.Certificate with { MaxWorld = World.Release } };
        using var thief = new ApprovalAuthority(new SecondFactorGuard(new FixedSecrets(), new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions()), _time, () => Guid.NewGuid().ToString("N"));
        var forged = (await thief.ProposeAndApproveAsync(Request(World.Release), "mallory", Totp.Code(FixedSecrets.Secret, Totp.StepAt(_time.GetUtcNow(), new TotpOptions()), new TotpOptions()), TestContext.Current.CancellationToken)).Approval!;

        _authority.Verifier.Check(widened, MandateFor(widened, World.Release)).Should().Be(ApprovalRefusal.BadSignature);
        _authority.Verifier.Check(forged, MandateFor(forged, World.Release)).Should().Be(ApprovalRefusal.BadSignature, "a control plane that made its own authority cannot make an approval the runners accept");
        _time.Advance(TimeSpan.FromHours(2));
        _authority.Verifier.Check(approval, MandateFor(approval)).Should().Be(ApprovalRefusal.Expired);
    }

    [Fact]
    public void A_runner_refuses_a_mandate_with_no_certificate_or_one_that_does_not_cover_it_even_when_the_control_plane_signed_it()
    {
        using var controlPlane = MandateSigner.Create();
        var approval = _authority.Approve(Request(World.Git)).Approval!;
        var state = new RunnerState();
        using var runner = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);

        runner.Accept(controlPlane.Sign(MandateFor(approval), approval)).Should().Be(MandateRefusal.None);

        var noCertificate = controlPlane.Sign(MandateFor(approval) with { Nonce = "n2" }, approval: null);
        runner.Accept(noCertificate).Should().Be(MandateRefusal.NoValidApproval);
        var beyond = MandateFor(approval) with { Nonce = "n3", Frame = new MissionFrame(["example/other"], [], "Git:other") };
        runner.Accept(controlPlane.Sign(beyond, approval)).Should().Be(MandateRefusal.NoValidApproval, "a compromised control plane signs it, the runner still refuses");
        var higher = MandateFor(approval, World.Release) with { Nonce = "n4", RunnerId = "runner-git" };
        using var releaseRunner = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Release, state, _time, _authority.Verifier);
        releaseRunner.Accept(controlPlane.Sign(higher, approval)).Should().Be(MandateRefusal.NoValidApproval);
    }

    [Fact]
    public async Task The_code_confirms_what_the_authority_holds_and_nothing_else_so_whoever_relays_it_cannot_choose_what_is_signed()
    {
        var proposal = _authority.Authority.Propose(Request(World.Git), "carol")!;

        proposal.Summary.Should().Contain("problem-1").And.Contain("example/shop").And.Contain("highest world: Git").And.Contain("mandates: at most 8");
        _authority.Authority.Describe(proposal.Id)!.Digest.Should().Be(proposal.Digest);
        // Another approver cannot confirm it, and the attempt uses it up
        (await _authority.Authority.ApproveAsync(proposal.Id, "mallory", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval.Should().BeNull();
        _authority.Authority.Describe(proposal.Id).Should().BeNull("a proposal is used up by the first attempt");

        var held = _authority.Authority.Propose(Request(World.Git), "carol")!;
        var signed = (await _authority.Authority.ApproveAsync(held.Id, "carol", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval!;

        signed.Certificate.MaxWorld.Should().Be(World.Git);
        signed.Certificate.Repositories.Should().Equal("example/shop");
        (await _authority.Authority.ApproveAsync(held.Id, "carol", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval.Should().BeNull("a proposal is confirmed once");
        (await _authority.Authority.ApproveAsync("unknown", "carol", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval.Should().BeNull();
    }

    [Fact]
    public async Task A_proposal_that_waited_too_long_is_gone_and_a_wrong_code_uses_it_up()
    {
        var late = _authority.Authority.Propose(Request(), "dave")!;
        _time.Advance(TimeSpan.FromMinutes(11));

        _authority.Authority.Describe(late.Id).Should().BeNull();
        (await _authority.Authority.ApproveAsync(late.Id, "dave", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval.Should().BeNull();

        var guessed = _authority.Authority.Propose(Request(), "dave")!;
        (await _authority.Authority.ApproveAsync(guessed.Id, "dave", "000000", TestContext.Current.CancellationToken)).Approval.Should().BeNull();
        (await _authority.Authority.ApproveAsync(guessed.Id, "dave", _authority.CodeFor(), TestContext.Current.CancellationToken)).Approval.Should().BeNull("the proposal is used up: the maintainer asks again");
    }

    [Fact]
    public void The_authority_puts_its_own_bounds_on_the_lifetime_and_the_number_of_mandates_whoever_asks()
    {
        _authority.Approve(Request() with { Lifetime = TimeSpan.FromDays(8) }).Approval.Should().BeNull("a problem is approved for a week at most");
        _authority.Approve(Request() with { Kind = CertificateKind.Rule, Subject = "deprecated-dependency", Lifetime = TimeSpan.FromDays(29) }).Approval.Should().NotBeNull("a rule lives longer than a problem");
        _authority.Approve(Request() with { Kind = CertificateKind.Rule, Subject = "deprecated-dependency", Lifetime = TimeSpan.FromDays(31) }).Approval.Should().BeNull();
        _authority.Approve(Request(World.Release) with { Kind = CertificateKind.Rule, Subject = "deprecated-dependency" }).Approval.Should().BeNull("a standing rule reaches the Git world at most: the release world needs a certificate for the problem itself");
        _authority.Approve(Request(World.Git) with { Kind = CertificateKind.Rule, Subject = "deprecated-dependency" }).Approval.Should().NotBeNull();
        _authority.Approve(Request() with { MaxMandates = 0 }).Approval.Should().BeNull();
        _authority.Approve(Request() with { MaxMandates = 51 }).Approval.Should().BeNull();
    }

    [Fact]
    public void A_runner_counts_the_mandates_under_a_certificate_and_refuses_the_one_beyond_what_the_certificate_says()
    {
        using var controlPlane = MandateSigner.Create();
        var approval = _authority.Approve(Request(World.Git) with { MaxMandates = 2 }).Approval!;
        var state = new RunnerState();
        using var runner = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);

        runner.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n1" }, approval)).Should().Be(MandateRefusal.None);
        runner.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n2" }, approval)).Should().Be(MandateRefusal.None);
        runner.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n3" }, approval)).Should().Be(MandateRefusal.ApprovalUsedUp, "a signer cannot issue more under one approval than the maintainer allowed");
        runner.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n1" }, approval)).Should().Be(MandateRefusal.Replayed);
    }

    [Fact]
    public void A_withdrawn_certificate_is_refused_by_the_runner_before_it_expires_and_the_authority_publishes_the_list()
    {
        using var controlPlane = MandateSigner.Create();
        var approval = _authority.Approve(Request(World.Git)).Approval!;
        var state = new RunnerState();
        using var runner = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);
        var signed = controlPlane.Sign(MandateFor(approval), approval);
        runner.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.None);

        _authority.Authority.Revoke(approval.Certificate.Id);
        _authority.Authority.Revoked().Should().Equal(approval.Certificate.Id);
        foreach (var id in _authority.Authority.Revoked())
        {
            state.RevokedApprovals.Add(id);
        }

        runner.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.ApprovalRevoked);
        runner.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "other" }, approval)).Should().Be(MandateRefusal.ApprovalRevoked);
    }

    [Fact]
    public void The_summary_the_maintainer_reads_has_one_line_per_field_and_a_requester_cannot_make_it_say_something_else()
    {
        var proposal = _authority.Authority.Propose(Request(World.Release) with { Repositories = ["example/shop", "example/api"] }, "erin")!;

        proposal.Summary.Split('\n').Should().Contain(["kind: Problem", "subject: \"problem-1\"", "application: \"shop\"", "repository: \"example/api\"", "repository: \"example/shop\"", "highest world: Release"]);
        // A value that holds a line break, a control character or nothing is refused, so it cannot start a line of its own
        _authority.Authority.Propose(Request() with { ApplicationId = "shop\nhighest world: Staging" }, "erin").Should().BeNull();
        _authority.Authority.Propose(Request() with { Subject = "problem-1\u0007" }, "erin").Should().BeNull();
        _authority.Authority.Propose(Request() with { Repositories = [string.Empty] }, "erin").Should().BeNull();
        _authority.Authority.Propose(Request() with { ForbiddenPaths = ["a\r\nb"] }, "erin").Should().BeNull();
    }

    [Fact]
    public void Two_requests_that_read_alike_do_not_share_a_digest_because_it_is_over_the_canonical_encoding()
    {
        var one = _authority.Authority.Propose(Request() with { Repositories = ["a, b"] }, "erin")!;
        var two = _authority.Authority.Propose(Request() with { Repositories = ["a", "b"] }, "erin")!;
        var same = _authority.Authority.Propose(Request() with { Repositories = ["a", "b"] }, "erin")!;

        one.Digest.Should().NotBe(two.Digest);
        two.Digest.Should().Be(same.Digest, "the same request has the same digest");
        two.Summary.Should().NotBe(one.Summary);
    }

    [Fact]
    public void The_count_of_a_certificate_is_the_authoritys_one_for_every_runner_so_two_runners_cannot_double_it()
    {
        using var controlPlane = MandateSigner.Create();
        var approval = _authority.Approve(Request(World.Git) with { MaxMandates = 2 }).Approval!;
        var state = new RunnerState(_authority.Authority);
        using var one = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);
        using var other = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, new RunnerState(_authority.Authority), _time, _authority.Verifier);

        one.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n1" }, approval)).Should().Be(MandateRefusal.None);
        other.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n2" }, approval)).Should().Be(MandateRefusal.None);
        one.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n3" }, approval)).Should().Be(MandateRefusal.ApprovalUsedUp);
        other.Accept(controlPlane.Sign(MandateFor(approval) with { Nonce = "n4" }, approval)).Should().Be(MandateRefusal.ApprovalUsedUp, "the other runner sees the same count");
    }

    [Fact]
    public void A_runner_that_cannot_ask_the_authority_refuses_and_a_withdrawal_reaches_every_runner()
    {
        using var controlPlane = MandateSigner.Create();
        var approval = _authority.Approve(Request(World.Git)).Approval!;
        var state = new RunnerState(_authority.Authority);
        using var runner = new MandateVerifier(controlPlane.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);
        var signed = controlPlane.Sign(MandateFor(approval), approval);

        state.AuthorityDown = true;
        runner.Accept(signed).Should().Be(MandateRefusal.AuthorityUnreachable);
        runner.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.AuthorityUnreachable, "unknown counts as withdrawn");
        state.AuthorityDown = false;
        runner.Accept(signed).Should().Be(MandateRefusal.None);
        _authority.Authority.Revoke(approval.Certificate.Id);
        runner.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.ApprovalRevoked);
    }

    [Fact]
    public void What_the_authority_keeps_goes_through_a_ledger_that_a_restart_can_keep()
    {
        var ledger = new InMemoryAuthorityLedger();
        using var first = new ApprovalAuthority(new SecondFactorGuard(new FixedSecrets(), new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions()), _time, () => Guid.NewGuid().ToString("N"), ledger: ledger);
        first.Revoke("c1");
        first.Use("c2", "n1", 1).Should().Be(MandateUse.Used);

        using var restarted = new ApprovalAuthority(new SecondFactorGuard(new FixedSecrets(), new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions()), _time, () => Guid.NewGuid().ToString("N"), ledger: ledger);

        restarted.IsRevoked("c1").Should().BeTrue("a withdrawn certificate stays withdrawn when the authority restarts");
        restarted.Use("c2", "n2", 1).Should().Be(MandateUse.ApprovalUsedUp);
        restarted.Use("c9", "n1", 5).Should().Be(MandateUse.Replayed, "a nonce is used once");
    }

    [Fact]
    public void Every_certificate_carries_the_floor_of_forbidden_paths_even_when_the_request_lists_none()
    {
        var proposal = _authority.Authority.Propose(Request(World.Release) with { ForbiddenPaths = null }, "erin")!;

        proposal.Summary.Split('\n').Should().Contain(["forbidden path: \".github/**\"", "forbidden path: \"**/*.enc.*\"", "forbidden path: \"**/.env*\""], "the maintainer reads them, and an absence cannot hide");
        var approval = _authority.Approve(Request(World.Release) with { ForbiddenPaths = ["infra/**"] }).Approval!;
        approval.Certificate.ForbiddenPaths.Should().BeEquivalentTo(".github/**", "**/*.enc.*", "**/.env*", "infra/**");
    }

    [Fact]
    public void A_kind_or_a_world_that_does_not_exist_is_never_certified_and_never_accepted()
    {
        _authority.Authority.Propose(Request() with { Kind = (CertificateKind)2 }, "erin").Should().BeNull();
        _authority.Authority.Propose(Request() with { MaxWorld = (World)7 }, "erin").Should().BeNull();
        var good = _authority.Approve(Request(World.Git)).Approval!;
        var wildcard = good with { Certificate = good.Certificate with { Kind = (CertificateKind)2 } };

        // Signed or not, a certificate of a kind that does not exist binds no problem and no class: it is refused before anything else
        _authority.Verifier.Check(wildcard, MandateFor(good)).Should().Be(ApprovalRefusal.UnknownKind);
    }

    private sealed class FixedSecrets : ISecondFactorSecrets
    {
        public static readonly byte[] Secret = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(Secret);
    }

    [Fact]
    public void A_mandate_that_lists_an_act_of_another_world_that_names_another_application_or_class_or_drops_a_forbidden_path_is_refused_whoever_signed_it()
    {
        var approval = _authority.Approve(Request(World.Release) with { ForbiddenPaths = [".github/**", "secrets/**"] }).Approval!;
        var good = MandateFor(approval, World.Git, m => m with { Frame = m.Frame with { ForbiddenPaths = [".github/**", "**/*.enc.*", "**/.env*", "secrets/**", "extra/**"] } });

        _authority.Verifier.Check(approval, good).Should().Be(ApprovalRefusal.None);
        _authority.Verifier.Check(approval, good with { Acts = [GatedAct.MergePullRequest, GatedAct.Deploy] }).Should().Be(ApprovalRefusal.ActsOfAnotherWorld, "a Git-world mandate cannot list a deployment");
        _authority.Verifier.Check(approval, good with { ApplicationId = "other-app" }).Should().Be(ApprovalRefusal.WrongApplication);
        _authority.Verifier.Check(approval, good with { Frame = good.Frame with { ForbiddenPaths = [] } }).Should().Be(ApprovalRefusal.ForbiddenPathsDropped);
        _authority.Verifier.Check(approval, good with { Frame = good.Frame with { ForbiddenPaths = [".github/**", "**/*.enc.*", "**/.env*"] } }).Should().Be(ApprovalRefusal.ForbiddenPathsDropped, "secrets/** was asked for and dropped");
    }

    [Fact]
    public void A_rule_certificate_covers_only_its_class_and_its_application()
    {
        var rule = _authority.Approve(Request(World.Git) with { Kind = CertificateKind.Rule, Subject = "deprecated-dependency" }).Approval!;
        var mandate = MandateFor(rule);

        _authority.Verifier.Check(rule, mandate).Should().Be(ApprovalRefusal.None, "any problem of the class in the approved repositories is inside the rule");
        _authority.Verifier.Check(rule, mandate with { ProblemClass = "secret-exposed" }).Should().Be(ApprovalRefusal.WrongClass);
        _authority.Verifier.Check(rule, mandate with { ApplicationId = "other-app" }).Should().Be(ApprovalRefusal.WrongApplication);
    }
}
