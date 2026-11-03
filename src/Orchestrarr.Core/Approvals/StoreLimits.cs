namespace Orchestrarr.Core.Approvals;

/// <summary>
/// How much the bounded stores hold by default. A store in memory is sized for memory, which a restart empties; one kept in a file is never
/// emptied (nothing yet removes an approval, and the trail is append-only), so it is sized for years, not for a session.
/// </summary>
public static class StoreLimits
{
    public const int MemoryQueue = 500;
    public const int MemoryAudit = 2_000;
    public const int FileQueue = 10_000;
    public const int FileAudit = 200_000;
}
