using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// The problem as the control plane read it, for the lead agent that thinks about it (ADR 0046). Everything here was fetched by the control plane except <see cref="Description"/>, which is the text of a row: when
/// <see cref="DescriptionIsParsed"/> is false it is text written by someone else (a pull request's title, a message), and it is given to the lead as untrusted data. **No worker receives this**: a worker has its instruction.
/// </summary>
public sealed record ProblemBrief(
    string Kind,
    string Repository,
    string Description,
    bool DescriptionIsParsed,
    string? UpdateClass = null,
    string? Checks = null,
    string? Severity = null,
    int? Count = null,
    int? PullRequest = null,
    IReadOnlyList<string>? Details = null)
{
    /// <summary>The brief of a row of the table: what the control plane read, and the description with where it came from (parsed by the control plane, or the text of someone else).</summary>
    public static ProblemBrief Of(ProblemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new ProblemBrief(row.Kind.ToString(), row.Source ?? row.Application, row.Description, row.ParsedChange is not null, row.UpdateClass?.ToString(), row.Checks?.ToString(), row.Severity?.ToString(), row.Count, row.PullRequest);
    }
}

/// <summary>One step of the plan of the lead: a world and the instruction of the worker that will work in it.</summary>
public sealed record PlannedStep(World World, string? Instruction);

/// <summary>
/// What the lead agent proposes for a problem (ADR 0046): the worlds the work goes through, in order, the instruction for each, the complexity mark and the reasons. A **proposal**: the conductor checks it
/// (<see cref="LeadPlanChecks"/>) before anything starts, and it can only make the work stop earlier than the powers allow, never go further.
/// </summary>
public sealed record LeadPlan(IReadOnlyList<PlannedStep> Steps, bool Complex, string? ComplexReason, IReadOnlyList<string> Reasons)
{
    public World First => Steps[0].World;

    public World Last => Steps[^1].World;

    public string? InstructionFor(World world) => Steps.FirstOrDefault(step => step.World == world)?.Instruction;

    /// <summary>The plan on one line, for the trail and the screen.</summary>
    public string Summary => string.Join(" → ", Steps.Select(step => step.World.ToString()));
}

/// <summary>The lead's answer, or why there is none. A failure to run or to read the answer is never a plan: the problem does not start.</summary>
public sealed record LeadOutcome(LeadPlan? Plan, string? Failure);

/// <summary>What the lead is asked about: the problem, how far the maintainer approved it, and the first world (a staging if the application has one).</summary>
public sealed record LeadInput(ProblemBrief Brief, World ApprovedUpTo, World First);

/// <summary>The checks the conductor makes on a plan before it uses it: shape, order, ceiling and the plain text of the instructions.</summary>
public static class LeadPlanChecks
{
    public const int MaxInstructionCharacters = 2000;
    public const int MaxReasonCharacters = 600;

    /// <summary>Why the plan is refused; empty when it may be used. <paramref name="highest"/> is the highest world the powers reach now.</summary>
    public static IReadOnlyList<string> Problems(LeadPlan? plan, World first, World highest)
    {
        if (plan is null || plan.Steps.Count == 0)
        {
            return ["the plan has no step"];
        }
        var problems = new List<string>();
        if (plan.Steps[0].World != first)
        {
            problems.Add($"the plan starts in the {plan.Steps[0].World} world, the first world of this problem is {first}");
        }
        for (var index = 1; index < plan.Steps.Count; index++)
        {
            if (plan.Steps[index].World != plan.Steps[index - 1].World + 1)
            {
                problems.Add("the worlds of the plan are not consecutive and in order");
                break;
            }
        }
        if (plan.Last > highest)
        {
            problems.Add($"the plan goes up to the {plan.Last} world, the powers reach the {highest} world");
        }
        foreach (var step in plan.Steps)
        {
            if (step.Instruction is { } instruction && !IsPlain(instruction, MaxInstructionCharacters))
            {
                problems.Add($"the instruction of the {step.World} world is not short plain text");
            }
        }
        if (plan.Reasons.Count == 0 || plan.Reasons.Any(reason => !IsPlain(reason, MaxReasonCharacters)))
        {
            problems.Add("the plan has no reasons, or a reason is not short plain text");
        }
        if (plan.ComplexReason is { } complexReason && !IsPlain(complexReason, MaxReasonCharacters))
        {
            problems.Add("the reason of the complexity is not short plain text");
        }
        return problems;
    }

    // Plain text: bounded, no control character (a line break included), none of the markers that fence data in a prompt
    private static bool IsPlain(string text, int max) =>
        text.Length > 0 && text.Length <= max && !text.Any(char.IsControl) && !text.Contains("<<<", StringComparison.Ordinal) && !text.Contains(">>>", StringComparison.Ordinal);
}
