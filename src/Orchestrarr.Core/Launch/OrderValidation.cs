using System.Text.RegularExpressions;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Launch;

/// <summary>
/// The shape of every field of an order, checked by the issuer before it signs and by the launcher after it verifies. Fields come from
/// findings and requests, which are untrusted: nothing outside these patterns is signed, run or written to the audit trail. The
/// patterns end with <c>\z</c>, because <c>$</c> also accepts a trailing newline.
/// </summary>
internal static partial class OrderValidation
{
    private const int MaxSteps = 16;

    public static bool IsValid(
        string workItemId,
        string repository,
        string headSha,
        string? environment,
        IReadOnlyList<StepKind> steps,
        string approverSubject,
        string audience) =>
        HasValidFields(workItemId, repository, headSha, environment, steps, approverSubject) && Identity().IsMatch(audience ?? string.Empty);

    // The fields that an order and an approval share
    private static bool HasValidFields(string workItemId, string repository, string headSha, string? environment, IReadOnlyList<StepKind> steps, string approverSubject) =>
        WorkItem().IsMatch(workItemId ?? string.Empty)
        && Repository().IsMatch(repository ?? string.Empty)
        && Sha().IsMatch(headSha ?? string.Empty)
        && (environment is null || Environment().IsMatch(environment))
        && Identity().IsMatch(approverSubject ?? string.Empty)
        && HasValidSteps(steps);

    /// <summary>The shape of an approval (no environment, no audience): the same fields, the same patterns as an order's.</summary>
    public static bool IsValidApproval(string workItemId, string repository, string headSha, IReadOnlyList<StepKind> steps, string approverSubject) =>
        HasValidFields(workItemId, repository, headSha, environment: null, steps, approverSubject);

    public static bool IsValidWorkItem(string? workItemId) => WorkItem().IsMatch(workItemId ?? string.Empty);

    /// <summary>The subject of an approver, as the identity provider gives it.</summary>
    public static bool IsValidSubject(string? subject) => Identity().IsMatch(subject ?? string.Empty);

    private static bool HasValidSteps(IReadOnlyList<StepKind>? steps) =>
        steps is { Count: > 0 and <= MaxSteps }
        && steps.All(Enum.IsDefined)
        && steps.Distinct().Count() == steps.Count;

    [GeneratedRegex(@"^[A-Za-z0-9_.:/-]{1,200}\z", RegexOptions.None, 100)]
    private static partial Regex WorkItem();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.None, 100)]
    private static partial Regex Repository();

    // The full commit: a short one is ambiguous, and the order binds one commit.
    [GeneratedRegex(@"^([0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.None, 100)]
    private static partial Regex Sha();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,100}\z", RegexOptions.None, 100)]
    private static partial Regex Environment();

    [GeneratedRegex(@"^[A-Za-z0-9_.@:+-]{1,200}\z", RegexOptions.None, 100)]
    private static partial Regex Identity();
}
