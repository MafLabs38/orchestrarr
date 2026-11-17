using System.Collections.Concurrent;

namespace Orchestrarr.Core.Missions;

public sealed class InMemoryMissionStore : IMissionStore
{
    private readonly ConcurrentDictionary<string, Mission> _missions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public Mission? Get(string id) => _missions.GetValueOrDefault(id);

    public void Save(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        if (_missions.TryAdd(mission.Id, mission))
        {
            _order.Enqueue(mission.Id);
            return;
        }
        _missions[mission.Id] = mission;
    }

    public IReadOnlyList<Mission> Recent(int limit) => [.. _order.Reverse().Take(Math.Max(limit, 0)).Select(id => _missions[id])];

    public IReadOnlyList<Mission> ForProblem(string problemId) => [.. _order.Select(id => _missions[id]).Where(mission => mission.ProblemId == problemId)];
}

/// <summary>The unit of work of the in-memory stores: the writes of one unit are not interleaved with another's (nothing here can fail half way).</summary>
public sealed class InMemoryMissionUnit : IMissionUnit
{
    private readonly object _gate = new();

    public void Atomically(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            work();
        }
    }
}

public sealed class InMemoryMandateLedger : IMandateLedger
{
    private readonly ConcurrentDictionary<string, SignedMandate> _mandates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _revoked = new(StringComparer.Ordinal);

    public void Record(SignedMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        _mandates.TryAdd(mandate.Mandate.Id, mandate);
    }

    public SignedMandate? Get(string id) => _mandates.GetValueOrDefault(id);

    public IReadOnlyDictionary<string, SignedMandate> GetMany(IReadOnlyCollection<string> ids) => ids.Distinct(StringComparer.Ordinal).Where(_mandates.ContainsKey).ToDictionary(id => id, id => _mandates[id], StringComparer.Ordinal);

    public IReadOnlySet<string> RevokedAmong(IReadOnlyCollection<string> ids) => ids.Where(_revoked.ContainsKey).ToHashSet(StringComparer.Ordinal);

    public int CountForProblem(string problemId) => _mandates.Values.Count(mandate => mandate.Mandate.ProblemId == problemId);

    public int CountForRun(string problemId, string approvalId) => _mandates.Values.Count(mandate => mandate.Mandate.ProblemId == problemId && mandate.Mandate.ApprovalId == approvalId);

    public int CountForApproval(string approvalId) => _mandates.Values.Count(mandate => mandate.Mandate.ApprovalId == approvalId);

    public void Revoke(string id)
    {
        if (_mandates.ContainsKey(id))
        {
            _revoked[id] = true;
        }
    }

    public bool IsRevoked(string id) => _revoked.ContainsKey(id);
}

public sealed class InMemoryProblemStore : IProblemStore
{
    private readonly ConcurrentDictionary<string, ProblemRun> _runs = new(StringComparer.Ordinal);

    public ProblemRun? Get(string problemId) => _runs.GetValueOrDefault(problemId);

    public IReadOnlyDictionary<string, ProblemRun> GetMany(IReadOnlyCollection<string> problemIds) =>
        problemIds.Distinct(StringComparer.Ordinal).Where(_runs.ContainsKey).ToDictionary(id => id, id => _runs[id], StringComparer.Ordinal);

    public void Save(ProblemRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.ProblemId] = run;
    }

    public IReadOnlyList<ProblemRun> InProgress() => [.. _runs.Values.Where(run => run.State == ProblemState.InProgress)];
}

public sealed class InMemoryDossierStore : IDossierStore
{
    private readonly ConcurrentDictionary<string, Dossier> _dossiers = new(StringComparer.Ordinal);

    public void Save(Dossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        _dossiers[dossier.MissionId] = dossier;
    }

    public Dossier? Get(string missionId) => _dossiers.GetValueOrDefault(missionId);

    public IReadOnlyDictionary<string, Dossier> GetMany(IReadOnlyCollection<string> missionIds) =>
        missionIds.Distinct(StringComparer.Ordinal).Where(_dossiers.ContainsKey).ToDictionary(id => id, id => _dossiers[id], StringComparer.Ordinal);
}
