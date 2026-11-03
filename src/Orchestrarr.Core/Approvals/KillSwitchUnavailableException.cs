namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The global stop could not be released for certain: its state cannot be written, so it stays engaged. Engaging never raises it, because the
/// stop must work when nothing else does (SPEC D-43).
/// </summary>
public sealed class KillSwitchUnavailableException : Exception
{
    public KillSwitchUnavailableException()
        : base("The global stop cannot be released: its state cannot be saved, so it stays engaged.")
    {
    }

    public KillSwitchUnavailableException(string message)
        : base(message)
    {
    }

    public KillSwitchUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
