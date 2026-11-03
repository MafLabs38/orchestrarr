using System.Globalization;
using System.Text;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Launch;

/// <summary>
/// What the operator ticked, bound to one commit and one environment (SPEC D-41). It is data, never a command or a prompt: the
/// launcher builds the instruction of the agent from it and from templates.
/// </summary>
public sealed record LaunchOrder(
    Guid Id,
    string WorkItemId,
    string Repository,
    string HeadSha,
    string? Environment,
    IReadOnlyList<StepKind> Steps,
    string Approver,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce)
{
    /// <summary>The exact bytes that are signed and verified: fixed field order, invariant formatting, one field per line.</summary>
    public byte[] Canonical()
    {
        var steps = string.Join(',', Steps.Order().Select(step => step.ToString()));
        string[] fields =
        [
            "orchestrarr-order-v1",
            Id.ToString("D"),
            WorkItemId,
            Repository,
            HeadSha,
            Environment ?? string.Empty,
            steps,
            Approver,
            IssuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            Nonce,
        ];
        return Encoding.UTF8.GetBytes(string.Join('\n', fields));
    }
}

public sealed record SignedOrder(LaunchOrder Order, string Signature);
