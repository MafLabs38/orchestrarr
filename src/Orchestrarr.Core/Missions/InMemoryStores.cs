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

public sealed class InMemoryMandateLedger : IMandateLedger
{
    private readonly ConcurrentDictionary<string, SignedMandate> _mandates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _revoked = new(StringComparer.Ordinal);

    public void Record(SignedMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        _mandates[mandate.Mandate.Id] = mandate;
    }

    public SignedMandate? Get(string id) => _mandates.GetValueOrDefault(id);

    public int CountForProblem(string problemId) => _mandates.Values.Count(mandate => mandate.Mandate.ProblemId == problemId);

    public void Revoke(string id) => _revoked[id] = true;

    public bool IsRevoked(string id) => _revoked.ContainsKey(id);
}

public sealed class InMemoryProblemStore : IProblemStore
{
    private readonly ConcurrentDictionary<string, ProblemRun> _runs = new(StringComparer.Ordinal);

    public ProblemRun? Get(string problemId) => _runs.GetValueOrDefault(problemId);

    public void Save(ProblemRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.ProblemId] = run;
    }
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
}
