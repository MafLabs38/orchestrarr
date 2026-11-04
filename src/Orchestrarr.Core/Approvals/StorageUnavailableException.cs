namespace Orchestrarr.Core.Approvals;

/// <summary>
/// A store that is kept somewhere could not be used (a locked or full file, a database that cannot be read). The product fails closed: the
/// service turns it into a refusal, never into a silent success, and never into an empty list.
/// </summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException()
        : base("The storage cannot be used.")
    {
    }

    public StorageUnavailableException(string message)
        : base(message)
    {
    }

    public StorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
