using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class StagingAndPowerTests
{
    [Theory]
    [InlineData(StagingDeclaration.Undeclared, StagingRequirement.BlankMachine, StagingState.BaselineProven, StagingVerdict.RefusedUndeclared)]
    [InlineData(StagingDeclaration.DeclaredNoStaging, StagingRequirement.FullStack, StagingState.NotDefined, StagingVerdict.UsableWithNoNet)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, StagingVerdict.Usable)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.BaselineProven, StagingVerdict.Usable)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.Provisioned, StagingVerdict.RefusedNotProven)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.Defined, StagingVerdict.RefusedNotProven)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.Stale, StagingVerdict.RefusedBrokenOrStale)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.Broken, StagingVerdict.RefusedBrokenOrStale)]
    public void The_staging_gate_is_closed_for_what_nobody_declared_and_for_a_staging_that_is_not_proven(StagingDeclaration declaration, StagingRequirement requirement, StagingState state, StagingVerdict expected)
    {
        StagingGate.Decide(declaration, requirement, state).Should().Be(expected);
    }

    [Fact]
    public void A_standing_rule_covers_only_its_class_its_repositories_its_worlds_and_its_time()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var rule = new StandingRule("r1", "deprecated-dependency", World.Git, ["example/shop"], now.AddDays(30), Revoked: false);

        rule.Covers("deprecated-dependency", "example/shop", World.Git, now).Should().BeTrue();
        rule.Covers("deprecated-dependency", "example/shop", World.Staging, now).Should().BeTrue("a power that reaches the Git world reaches staging before it");
        rule.Covers("deprecated-dependency", "example/shop", World.Release, now).Should().BeFalse("the maximum world is the Git world");
        rule.Covers("another-class", "example/shop", World.Git, now).Should().BeFalse();
        rule.Covers("deprecated-dependency", "example/other", World.Git, now).Should().BeFalse();
        rule.Covers("deprecated-dependency", "example/shop", World.Git, now.AddDays(31)).Should().BeFalse("it expires");
        (rule with { Revoked = true }).Covers("deprecated-dependency", "example/shop", World.Git, now).Should().BeFalse();
    }

    [Fact]
    public void The_power_in_effect_is_the_lowest_of_the_plan_the_grant_and_the_rule_a_rule_never_widens_a_grant_and_no_grant_is_no_power()
    {
        EffectivePower.Of(World.Release, World.Git, null).Should().Be(World.Git);
        EffectivePower.Of(World.Git, World.Release, null).Should().Be(World.Git);
        EffectivePower.Of(World.Release, World.Release, World.Staging, byRule: true).Should().Be(World.Staging);
        EffectivePower.Of(World.Release, World.Git, World.Release, byRule: true).Should().Be(World.Git, "a rule never widens a grant");
        EffectivePower.Of(World.Release, null, null).Should().BeNull("the grant is off until the maintainer enables it (D-10)");
        EffectivePower.Of(World.Release, World.Release, null, byRule: true).Should().BeNull("a rule that is needed and missing is no power");
        EffectivePower.Reaches(World.Release, World.Release, World.Git, null).Should().BeFalse("the grant stops at the Git world");
        EffectivePower.Reaches(World.Git, World.Release, World.Git, null).Should().BeTrue();
        EffectivePower.Reaches(World.Staging, World.Release, null, null).Should().BeFalse("no grant, no power");
    }

    [Fact]
    public void The_controller_cannot_grant_a_world_the_plan_does_not_allow_or_a_decision_without_reasons_or_over_a_failed_check()
    {
        var grant = new ControllerDecision(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);

        ControllerLimits.MayGrant(grant, World.Git, World.Git, []).Should().BeTrue();
        ControllerLimits.MayGrant(grant, World.Release, World.Git, []).Should().BeFalse();
        ControllerLimits.MayGrant(grant, World.Git, null, []).Should().BeFalse();
        ControllerLimits.MayGrant(grant, World.Git, World.Git, [PreconditionFailure.CiNotGreenOnExactCommit]).Should().BeFalse("a check of the code is never waived");
        ControllerLimits.MayGrant(grant with { Reasons = [] }, World.Git, World.Git, []).Should().BeFalse();
        ControllerLimits.MayGrant(new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], null), World.Git, World.Git, []).Should().BeFalse();
    }

    [Fact]
    public void A_send_back_needs_remarks_for_the_worker()
    {
        new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], null).IsWellFormed.Should().BeFalse();
        new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], "run the integration tests too").IsWellFormed.Should().BeTrue();
        new ControllerDecision(ControllerDecisionKind.Escalate, ["no fixed version exists"], null).IsWellFormed.Should().BeTrue();
    }
}
