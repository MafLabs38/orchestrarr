namespace Orchestrarr.Core.Approvals;

/// <summary>Whether approving needs the code of the authenticator right now. Read at every approval, never cached.</summary>
public interface ISecondFactorPolicy
{
    bool Required { get; }
}
