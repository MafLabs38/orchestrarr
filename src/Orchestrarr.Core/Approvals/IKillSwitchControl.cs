using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The global stop, with the way to set it (SPEC D-43). Engaged, nothing is approved and nothing launches.</summary>
public interface IKillSwitchControl : IKillSwitch
{
    bool Engaged { get; }

    void Set(bool engaged);
}
