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
        var mandate = new Mandate("m-1", "mission-1", "problem-1", world, "runner-git", [GatedAct.OpenPullRequest], new MissionFrame(["example/shop"], [".github/**"], "Git:shop"), new MissionBudget(10, 2, 20),
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

        var once = await _authority.Authority.ApproveAsync(Request(), "bob", _authority.CodeFor(), TestContext.Current.CancellationToken);
        var again = await _authority.Authority.ApproveAsync(Request(), "bob", _authority.CodeFor(), TestContext.Current.CancellationToken);
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
        var forged = (await thief.ApproveAsync(Request(World.Release), "mallory", Totp.Code(FixedSecrets.Secret, Totp.StepAt(_time.GetUtcNow(), new TotpOptions()), new TotpOptions()), TestContext.Current.CancellationToken)).Approval!;

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
        var state = new OpenState();
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

    private sealed class OpenState : IMandateState
    {
        private readonly HashSet<string> _used = [];

        public bool KillSwitchEngaged => false;

        public bool TryUse(string nonce) => _used.Add(nonce);

        public bool IsRevoked(string mandateId) => false;
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
        var good = MandateFor(approval, World.Git, m => m with { Frame = m.Frame with { ForbiddenPaths = [".github/**", "secrets/**", "extra/**"] } });

        _authority.Verifier.Check(approval, good).Should().Be(ApprovalRefusal.None);
        _authority.Verifier.Check(approval, good with { Acts = [GatedAct.MergePullRequest, GatedAct.Deploy] }).Should().Be(ApprovalRefusal.ActsOfAnotherWorld, "a Git-world mandate cannot list a deployment");
        _authority.Verifier.Check(approval, good with { ApplicationId = "other-app" }).Should().Be(ApprovalRefusal.WrongApplication);
        _authority.Verifier.Check(approval, good with { Frame = good.Frame with { ForbiddenPaths = [] } }).Should().Be(ApprovalRefusal.ForbiddenPathsDropped);
        _authority.Verifier.Check(approval, good with { Frame = good.Frame with { ForbiddenPaths = [".github/**"] } }).Should().Be(ApprovalRefusal.ForbiddenPathsDropped);
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
