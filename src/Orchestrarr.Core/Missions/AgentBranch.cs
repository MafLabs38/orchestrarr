using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// The name of the branch that a free agent pushes (ADR 0048): one shape for the runner that makes it, the control plane that audits it and the forge provider that reads it and opens a pull request from it. A branch belongs to
/// one problem: `agent/&lt;problem&gt;-&lt;six characters&gt;`, so that a handover can never get another problem's branch audited.
/// </summary>
public static partial class AgentBranch
{
    public const string Folder = "agent/";

    [GeneratedRegex(@"^agent/[a-z0-9][a-z0-9-]{0,79}$", RegexOptions.None, 250)]
    private static partial Regex Shape();

    [GeneratedRegex(@"[^a-z0-9-]+", RegexOptions.None, 250)]
    private static partial Regex NotPlain();

    public static bool IsName(string? name) => name is not null && Shape().IsMatch(name);

    /// <summary>`agent/` and the problem, made plain (lower case, letters, digits and dashes, at most 60 characters), and a dash: what every branch of that problem starts with.</summary>
    public static string PrefixOf(string problemId)
    {
        ArgumentNullException.ThrowIfNull(problemId);
        var plain = NotPlain().Replace(problemId.ToLowerInvariant(), "-").Trim('-');
        plain = plain.Length > 60 ? plain[..60].TrimEnd('-') : plain;
        return $"{Folder}{(plain.Length == 0 ? "p" : plain)}-";
    }

    /// <summary>A name that is new at each try: a branch that exists is another try's.</summary>
    public static string Create(string problemId, string suffix) => $"{PrefixOf(problemId)}{suffix}";

    /// <summary>Whether the branch is well formed and is one of this problem's.</summary>
    public static bool Belongs(string? name, string problemId) => IsName(name) && name!.StartsWith(PrefixOf(problemId), StringComparison.Ordinal);
}
