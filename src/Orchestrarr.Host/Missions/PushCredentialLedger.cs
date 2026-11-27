using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// The push credentials that the control plane made and that are still valid on the forge (ADR 0057), **in memory only** (never written, never logged), so that it can revoke them on the forge itself: the earlier ones of a mission once
/// the runner says that it placed the new one (not before: a runner that did not get the new one keeps a credential that works), all of them when the agent is done, when the control plane refuses to renew, and at the latest at the next
/// pass of the driver when the stop is engaged or the mission no longer runs. Deleting the file on the machine is not enough: an agent that copied it could still push until it expires. A restart of the control plane forgets them: they
/// then expire within the hour.
/// </summary>
internal sealed class PushCredentialLedger(IPushCredentials credentials, IMissionStore missions, IStopSignal stop, ProblemConductor conductor, ILogger<PushCredentialLedger> logger)
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<string>> _live = new(StringComparer.Ordinal);

    /// <summary>A new credential of the mission, kept to be revoked in its turn; the earlier ones stay valid until the runner says that it placed this one.</summary>
    public void Issued(string missionId, string token)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(missionId, out var held))
            {
                held.Add(token);
            }
            else
            {
                _live[missionId] = [token];
            }
        }
    }

    /// <summary>The runner placed the newest credential of the mission: the earlier ones are revoked.</summary>
    public async Task<int> RevokeEarlierAsync(string missionId, CancellationToken cancellationToken)
    {
        List<string> earlier;
        lock (_gate)
        {
            if (!_live.TryGetValue(missionId, out var held) || held.Count < 2)
            {
                return 0;
            }
            earlier = held[..^1];
            _live[missionId] = [held[^1]];
        }
        return await RevokeAllAsync(missionId, earlier, "replaced by a new one that the runner placed", cancellationToken);
    }

    /// <summary>Revokes every credential of the mission that is still valid. Returns how many were revoked.</summary>
    public async Task<int> RevokeAsync(string missionId, string why, CancellationToken cancellationToken)
    {
        List<string> held;
        lock (_gate)
        {
            if (!_live.Remove(missionId, out var found))
            {
                return 0;
            }
            held = found;
        }
        return await RevokeAllAsync(missionId, held, why, cancellationToken);
    }

    /// <summary>The pass of the driver: what was made for a mission that no longer runs, or while the stop is engaged, is revoked.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _live.Keys];
        }
        foreach (var id in ids)
        {
            if (stop.Engaged)
            {
                _ = await RevokeAsync(id, "the global stop is engaged", cancellationToken);
            }
            else if (missions.Get(id) is not { State: MissionState.Running or MissionState.Mandated })
            {
                _ = await RevokeAsync(id, "the mission no longer runs", cancellationToken);
            }
        }
    }

    private async Task<int> RevokeAllAsync(string missionId, List<string> tokens, string why, CancellationToken cancellationToken)
    {
        var revoked = 0;
        foreach (var token in tokens)
        {
            if (await credentials.RevokeAsync(token, cancellationToken))
            {
                revoked++;
            }
        }
        if (tokens.Count == 0)
        {
            return 0;
        }
        var said = revoked == tokens.Count ? $"{revoked} push credential(s) revoked on the forge: {why}" : $"{tokens.Count - revoked} of {tokens.Count} push credential(s) could not be revoked on the forge ({why}): they expire within the hour";
        if (revoked < tokens.Count)
        {
            logger.LogWarning("Push credentials of mission {Mission} could not all be revoked: {Why}", missionId, why);
        }
        _ = await conductor.RecordActPerformedAsync(missionId, GatedAct.PushBranch, said, cancellationToken);
        return revoked;
    }
}
