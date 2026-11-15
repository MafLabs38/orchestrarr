using Orchestrarr.Core.Missions;
using Microsoft.Extensions.Time.Testing;

namespace Orchestrarr.Core.Test.Missions;

public sealed class MandateTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly MandateSigner _signer = MandateSigner.Create();
    private readonly RunnerState _state = new();
    private readonly TestAuthority _authority;
    private readonly List<IDisposable> _verifiers = [];

    public MandateTests() => _authority = new TestAuthority(_time);

    public void Dispose()
    {
        _signer.Dispose();
        _authority.Dispose();
        foreach (var verifier in _verifiers)
        {
            verifier.Dispose();
        }
    }

    private Mandate Mandate(World world = World.Git, string runner = "runner-git", params GatedAct[] acts) => new(
        "mandate-1", "mission-1", "problem-1", world, runner, acts.Length == 0 ? [GatedAct.OpenPullRequest] : acts,
        new MissionFrame(["example/shop"], [".github/**", "**/*.enc.*", "**/.env*"], "orchestrarr/fix-1"), new MissionBudget(20, 3, 50),
        _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(30), MandateSource.PlanValidation, "nonce-1", "approval-1", "shop", "deprecated-dependency");

    // Signed by the control plane, with a certificate of the authority that the mandate falls inside
    private SignedMandate Signed(Mandate mandate, MandateSigner? signer = null)
    {
        var approval = _authority.Approve(new ApprovalRequest(CertificateKind.Problem, mandate.ProblemId, mandate.ApplicationId, mandate.Frame.Repositories, World.Release, new MissionBudget(60, 5, 100), TimeSpan.FromHours(1), mandate.Frame.ForbiddenPaths)).Approval!;
        return (signer ?? _signer).Sign(mandate with { ApprovalId = approval.Certificate.Id }, approval);
    }

    private MandateVerifier Verifier(World world = World.Git, string runner = "runner-git")
    {
        var verifier = new MandateVerifier(_signer.PublicKey(), runner, world, _state, _time, _authority.Verifier);
        _verifiers.Add(verifier);
        return verifier;
    }

    [Fact]
    public void A_mandate_signed_by_the_control_plane_is_accepted_by_the_runner_it_is_for_in_its_world()
    {
        var signed = Signed(Mandate());

        Verifier().Accept(signed).Should().Be(MandateRefusal.None);
    }

    [Fact]
    public void A_mandate_is_single_use_a_second_start_is_a_replay()
    {
        var verifier = Verifier();
        var signed = Signed(Mandate());

        verifier.Accept(signed);

        verifier.Accept(signed).Should().Be(MandateRefusal.Replayed);
    }

    [Fact]
    public void Any_change_to_a_signed_mandate_breaks_the_signature()
    {
        var signed = Signed(Mandate());
        var widened = signed with { Mandate = signed.Mandate with { Acts = [GatedAct.OpenPullRequest, GatedAct.MergePullRequest] } };
        var moved = signed with { Mandate = signed.Mandate with { World = World.Release } };
        var longer = signed with { Mandate = signed.Mandate with { ExpiresAt = signed.Mandate.ExpiresAt.AddDays(1) } };
        var frame = signed with { Mandate = signed.Mandate with { Frame = signed.Mandate.Frame with { ForbiddenPaths = [] } } };

        foreach (var forged in new[] { widened, moved, longer, frame })
        {
            Verifier().Accept(forged).Should().Be(MandateRefusal.BadSignature);
        }
    }

    [Fact]
    public void A_runner_that_holds_another_key_cannot_forge_a_mandate_that_the_others_accept()
    {
        using var thief = MandateSigner.Create();
        var forged = Signed(Mandate(World.Release, "runner-release", GatedAct.Deploy), thief);

        Verifier(World.Release, "runner-release").Accept(forged).Should().Be(MandateRefusal.BadSignature);
    }

    [Fact]
    public void A_mandate_for_another_runner_or_another_world_is_refused()
    {
        var signed = Signed(Mandate());

        Verifier(runner: "another-runner").Accept(signed).Should().Be(MandateRefusal.WrongRunner);
        Verifier(World.Staging).Accept(signed).Should().Be(MandateRefusal.WrongWorld);
    }

    [Fact]
    public void A_mandate_is_refused_before_its_time_and_after_its_expiry()
    {
        var signed = Signed(Mandate());
        var verifier = Verifier();

        _time.Advance(TimeSpan.FromMinutes(31));
        verifier.Accept(signed).Should().Be(MandateRefusal.Expired);

        var early = Signed(Mandate() with { IssuedAt = _time.GetUtcNow().AddMinutes(5), ExpiresAt = _time.GetUtcNow().AddMinutes(40) });
        verifier.Accept(early).Should().Be(MandateRefusal.NotYetValid);
    }

    [Fact]
    public void A_revoked_mandate_and_a_global_stop_stop_every_gated_act()
    {
        var signed = Signed(Mandate());
        var verifier = Verifier();
        verifier.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.None);

        _state.Revoked.Add("mandate-1");
        verifier.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.Revoked);

        _state.Revoked.Clear();
        _state.KillSwitchEngaged = true;
        verifier.MayAct(signed, GatedAct.OpenPullRequest).Should().Be(MandateRefusal.KillSwitchEngaged);
    }

    [Fact]
    public void An_act_that_the_mandate_does_not_list_is_refused_whatever_the_agent_asks()
    {
        var signed = Signed(Mandate(acts: GatedAct.OpenPullRequest));

        Verifier().MayAct(signed, GatedAct.MergePullRequest).Should().Be(MandateRefusal.ActNotAllowed);
        Verifier().MayAct(signed, GatedAct.Deploy).Should().Be(MandateRefusal.ActNotAllowed);
    }

    [Fact]
    public void The_order_of_the_acts_and_of_the_paths_does_not_change_the_signature_but_their_content_does()
    {
        var one = Mandate(World.Git, "runner-git", GatedAct.MergePullRequest, GatedAct.OpenPullRequest);
        var other = one with { Acts = [GatedAct.OpenPullRequest, GatedAct.MergePullRequest] };
        var different = one with { Frame = one.Frame with { Repositories = ["example/other"] } };

        one.CanonicalBytes().Should().Equal(other.CanonicalBytes());
        one.CanonicalBytes().Should().NotEqual(different.CanonicalBytes());
    }

    [Fact]
    public void The_private_key_can_be_kept_and_loaded_again_and_signs_what_the_original_signs()
    {
        var restored = MandateSigner.FromPrivateKey(_signer.PrivateKey());
        using (restored)
        {
            var signed = Signed(Mandate(), restored);

            Verifier().Accept(signed).Should().Be(MandateRefusal.None);
        }
    }

    [Fact]
    public void The_public_half_is_not_a_private_key_and_cannot_be_loaded_to_sign()
    {
        var load = () => MandateSigner.FromPrivateKey(_signer.PublicKey());

        load.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}
