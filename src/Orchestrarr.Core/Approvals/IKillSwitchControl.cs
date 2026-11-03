using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The global stop, with the way to set it (SPEC D-43). Engaged, nothing is approved and nothing launches.</summary>
public interface IKillSwitchControl : IKillSwitch
{
    bool Engaged { get; }

    /// <summary>
    /// Engages or releases the stop. Engaging always takes effect. Releasing a stop whose state cannot be saved raises
    /// <see cref="KillSwitchUnavailableException"/> and leaves it engaged: unknown means engaged.
    /// </summary>
    void Set(bool engaged);
}
