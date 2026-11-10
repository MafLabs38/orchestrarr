namespace Orchestrarr.Core.Accounts;

/// <summary>
/// The permissions, a closed list: one authorization policy exists for each, and a role is a set of them (ADR 0018). A new permission is
/// granted to the right roles by the commit that adds it.
/// </summary>
public static class Permissions
{
    /// <summary>Reading what the product shows: the decision cards, the pull requests, the runs, the status.</summary>
    public const string FleetRead = "fleet.read";

    /// <summary>Reading the audit trail: who approved what, and every refusal.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>Approving a decision card. The executor still asks for the second factor: this is the right to ask, not the proof.</summary>
    public const string ApprovalsCreate = "approvals.create";

    /// <summary>Engaging and releasing the global stop.</summary>
    public const string StopControl = "stop.control";

    /// <summary>Creating, changing and removing accounts, and resetting their passwords.</summary>
    public const string AccountsManage = "accounts.manage";

    /// <summary>Changing the options of the product, including those that lower its security where the host allows it.</summary>
    public const string OptionsManage = "options.manage";

    public static IReadOnlyList<string> All { get; } = [FleetRead, AuditRead, ApprovalsCreate, StopControl, AccountsManage, OptionsManage];
}

/// <summary>A named set of permissions. The built-in roles are in code, not stored.</summary>
public sealed record Role(string Name, string Description, IReadOnlySet<string> Permissions);

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Reader = "Reader";

    public static IReadOnlyList<Role> All { get; } =
    [
        new(Admin, "Everything, accounts included.", Permissions.All.ToHashSet(StringComparer.Ordinal)),
        new(Operator, "Reads everything, approves decision cards and engages or releases the global stop.", Set(Permissions.FleetRead, Permissions.AuditRead, Permissions.ApprovalsCreate, Permissions.StopControl)),
        new(Reader, "Reads everything and changes nothing.", Set(Permissions.FleetRead, Permissions.AuditRead)),
    ];

    public static Role? Find(string name) => All.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal));

    private static HashSet<string> Set(params string[] permissions) => new(permissions, StringComparer.Ordinal);
}
