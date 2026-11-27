using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Missions;

/// <summary>What a mission shows while it runs: the lines the runner reported, in the order they came. Kept in memory (the dossier is the record), bounded in lines per mission and in missions kept.</summary>
public sealed class MissionProgress(TimeProvider time)
{
    private const int MaxLines = 300;
    private const int MaxMissions = 200;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _order = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Line, DateTimeOffset At)> _last = new(StringComparer.Ordinal);

    /// <summary>The last thing a mission reported and when: what a person reads to tell a mission that works from one that has gone quiet.</summary>
    public (string? Line, DateTimeOffset? At) LastNews(string missionId) => _last.TryGetValue(missionId, out var last) ? (last.Line, last.At) : (null, null);

    public void Add(string missionId, string line)
    {
        if (!_lines.TryGetValue(missionId, out var list))
        {
            var added = new List<string>();
            list = _lines.GetOrAdd(missionId, added);
            if (ReferenceEquals(list, added))
            {
                _order.Enqueue(missionId);
                // The oldest missions are forgotten first: what a screen shows of an old mission is its dossier, not these lines
                while (_lines.Count > MaxMissions && _order.TryDequeue(out var oldest))
                {
                    _lines.TryRemove(oldest, out _);
                    _last.TryRemove(oldest, out _);
                }
            }
        }
        var shown = line.Length > 500 ? line[..500] : line;
        _last[missionId] = (shown, time.GetUtcNow());
        lock (list)
        {
            list.Add($"{time.GetUtcNow():HH:mm:ss} {shown}");
            if (list.Count > MaxLines)
            {
                list.RemoveAt(0);
            }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string? Machine)> _offered = new(StringComparer.Ordinal);

    /// <summary>How long a mission that was offered to a lane is not offered to another while it has not started: a claim is answered in a moment, a lane that was offered a mission starts it at once.</summary>
    public static readonly TimeSpan OfferHolds = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether the mission was offered to another lane a moment ago and has not started: with several lanes asking at once, a mission goes to one of them. The lane of a machine that asks again for the mission it was offered gets it again
    /// (a runner that restarted before it could start it); the lanes of the Git world have no machine to tell them apart, so none of them does.
    /// </summary>
    public bool RecentlyOffered(string missionId, string? machine) =>
        _offered.TryGetValue(missionId, out var offer) && time.GetUtcNow() - offer.At < OfferHolds && (machine is null || !string.Equals(machine, offer.Machine, StringComparison.OrdinalIgnoreCase));

    private readonly Lock _claimGate = new();

    /// <summary>The lanes of a runner claim at the same time: choosing a mission, taking its machine and noting that it was offered are one step, with no other claim in between (one control-plane process, ADR 0053).</summary>
    public T Claiming<T>(Func<T> claim)
    {
        lock (_claimGate)
        {
            return claim();
        }
    }

    public void Offered(string missionId, string? machine)
    {
        var now = time.GetUtcNow();
        _offered[missionId] = (now, machine);
        // Old entries are forgotten with the lines of old missions: only a mission that is still waiting is of interest
        if (_offered.Count > MaxMissions)
        {
            foreach (var old in _offered.Where(pair => now - pair.Value.At > OfferHolds).Select(pair => pair.Key).ToList())
            {
                _offered.TryRemove(old, out _);
            }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _remarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reworked = new(StringComparer.Ordinal);

    /// <summary>What the controller asked to be done again when it sent a mission back (kept with the lines: the dossier is the record, this is only what the next pass of the runner is told).</summary>
    public void Remark(string missionId, string remarks) => _remarks[missionId] = remarks.Length > 1000 ? remarks[..1000] : remarks;

    public string? RemarksOf(string missionId) => _remarks.GetValueOrDefault(missionId);

    /// <summary>Whether the rework for this send-back was already handed out (it does not hand it out).</summary>
    public bool WasOffered(string missionId, int sendBacks)
    {
        lock (_reworked)
        {
            return _reworked.GetValueOrDefault(missionId) >= sendBacks;
        }
    }

    /// <summary>Whether a mission that was sent back for the <paramref name="sendBacks"/>th time has already been offered for another pass: it is offered once for each send-back.</summary>
    public bool TakeRework(string missionId, int sendBacks)
    {
        lock (_reworked)
        {
            if (_reworked.GetValueOrDefault(missionId) >= sendBacks)
            {
                return false;
            }
            _reworked[missionId] = sendBacks;
            return true;
        }
    }

    public IReadOnlyList<string> Lines(string missionId)
    {
        if (!_lines.TryGetValue(missionId, out var list))
        {
            return [];
        }
        lock (list)
        {
            return [.. list];
        }
    }
}

/// <summary>
/// The routes of a runner that serves missions (ADR 0031, 0033), under the token of the runner like the others: it asks for a mission of its own, says that it started, reports what it does and hands the dossier over.
/// A mission is a runner's only if its mandate names that runner, whatever the runner asks.
/// </summary>
internal static partial class MissionRunnerEndpoints
{

    public static void MapMissionRunnerRoutes(this RouteGroupBuilder group, Func<HttpContext, string> runnerName)
    {
        group.MapPost("/missions/claim", (ClaimRequest? request, string? world, string? machine, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, ProblemConductor conductor, IDossierStore dossiers, MissionProgress progress, RepairRegistry? repairs, TimeProvider time, RunnerRegistry registry) =>
        {
            var name = runnerName(context);
            if (request?.Hello is { } hello)
            {
                // A runner of missions is seen like the others: what it accepts decides what the table offers
                registry.Seen(name, hello);
            }
            // The lanes of a runner ask at the same time: choosing a mission, taking its machine and noting that it was offered are one step, so that a mission goes to one lane
            return progress.Claiming(() => Claim(world, machine, name, context, missions, mandates, problems, conductor, dossiers, progress, repairs, time));
        });

        group.MapPost("/missions/{id}/started", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, ProblemConductor conductor, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates))
            {
                return Results.NotFound();
            }
            var result = await conductor.StartedAsync(id, ct);
            live.Publish(LiveTopics.Missions, id);
            return result.Ok ? Results.Ok() : Results.Conflict(new { error = result.Refusal });
        });

        group.MapPost("/missions/{id}/progress", (string id, MissionProgressRequest request, HttpContext context, IMissionStore missions, IMandateLedger mandates, MissionProgress progress, ILiveNotifier live) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Line) || missions.Get(id) is not { State: MissionState.Mandated or MissionState.Running })
            {
                return Results.NotFound();
            }
            progress.Add(id, request.Line);
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok();
        });

        group.MapPost("/missions/{id}/handover", async (string id, MissionHandOver request, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, ProblemConductor conductor, RepairRegistry? repairs, IBranchSource branches, IDefaultBranchSource defaults, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Claim) || string.IsNullOrWhiteSpace(request.WhatChangedAndWhy))
            {
                return Results.NotFound();
            }
            // The branch of a free agent that says it has finished (ADR 0048): the control plane reads it on the forge, at the commit named, and keeps what it read. What the runner or the agent says of it is a claim
            if (request.Branch is { Length: > 0 } branch)
            {
                var staged = missions.Get(id);
                var entry = staged is null ? null : problems.Get(staged.ProblemId)?.Entry;
                if (staged is not { World: World.Staging } || repairs is null || entry is not { PullRequest: null, Repositories.Count: 1 })
                {
                    return Results.Json(new { error = "only the staging world of a problem with no pull request hands over a branch" }, statusCode: StatusCodes.Status409Conflict);
                }
                if (!AgentBranch.Belongs(branch, staged.ProblemId) || request.Commit is not { } pushed || !CommitShape().IsMatch(pushed))
                {
                    return Results.Json(new { error = "the branch or the commit it finished at is not well formed" }, statusCode: StatusCodes.Status409Conflict);
                }
                var repository = entry.Repositories[0];
                // What the control plane cannot accept is **not lost**: the dossier is kept with the reason as an open point, no branch is kept, and the controller (and the maintainer, on the Missions screen) read it and send the work back
                string? refused = null;
                if (await branches.HeadOfAsync(repository, branch, ct) is not { } onForge || !string.Equals(onForge, pushed, StringComparison.Ordinal))
                {
                    refused = "the branch is not at the commit it was said to finish at, or cannot be read on the forge";
                }
                else if (await defaults.ReadAsync(repository, ct) is not { } current || await branches.CompareAsync(repository, current.Branch, pushed, ct) is not { } change)
                {
                    refused = "the change cannot be read on the forge";
                }
                else if (await RepairBase.WorkflowsAsync(branches, repository, change, pushed, ct) is var (before, after)
                    && ChangePolicy.Refusal(change, staged.Frame.ForbiddenPaths, before, after, after.Count > 0 ? await branches.FilesAtAsync(repository, pushed, ct) : null) is { } policy)
                {
                    refused = policy;
                }
                // A change to a pipeline file is justified by the agent, then checked by the controller against the lines and the problem (ADR 0057): one that does not say why is not kept
                else if (PipelineChanges.Unjustified(change.Files.Select(file => file.Path), request.PipelineJustification) is { } unjustified)
                {
                    refused = unjustified;
                }
                else
                {
                    // What is shown to the controller is what the forge says, not what the agent or the runner wrote about it
                    repairs.KeepBranch(staged.ProblemId, change.MergeBase, new PendingBranch(branch, pushed, [.. change.Files.Select(file => file.Path)], ChangePolicy.Reading(change)));
                }
                if (refused is not null)
                {
                    repairs.Forget(staged.ProblemId);
                    var note = $"The control plane did not accept the branch {branch}: {refused}.";
                    request = request with { WhatChangedAndWhy = $"{note} {request.WhatChangedAndWhy}", OpenPoints = [.. request.OpenPoints ?? [], note], Commit = null };
                }
            }
            // The files of a repair are the staging world's, and are checked before anything keeps them: the policy is the control plane's own, whatever the runner did
            else if (request.Files is { Count: > 0 } files)
            {
                var staged = missions.Get(id);
                var entry = staged is null ? null : problems.Get(staged.ProblemId)?.Entry;
                if (staged is not { World: World.Staging } || repairs is null || entry is null)
                {
                    return Results.Json(new { error = "only the staging world hands over files" }, statusCode: StatusCodes.Status409Conflict);
                }
                // A pull request that did not pass is repaired against the head that the maintainer saw; a problem that has none hands over a branch, not files
                var baseHead = entry.HeadSha;
                if (entry.PullRequest is null || baseHead is not { Length: > 0 } || !CommitShape().IsMatch(baseHead))
                {
                    return Results.Json(new { error = "files are handed over only to repair a pull request, at the commit that the maintainer saw" }, statusCode: StatusCodes.Status409Conflict);
                }
                var refusal = RepairPolicy.Refusal(files, staged.Frame.ForbiddenPaths, await RepairBase.ReadAsync(branches, entry.Repositories[0], baseHead, files, ct));
                if (refusal is not null)
                {
                    return Results.Json(new { error = refusal }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }
                repairs.Keep(staged.ProblemId, baseHead, files);
            }
            else if (repairs is not null && missions.Get(id) is { World: World.Staging } staged)
            {
                // A staging pass that repaired nothing is the last word on the problem: a repair that an earlier pass held is not the one that this pass proved
                repairs.Forget(staged.ProblemId);
            }
            var dossier = new Dossier(id, Cut(request.Claim), Cut(request.WhatChangedAndWhy), Bounded(request.EvidenceDigests), Bounded(request.OpenPoints), request.Complex, request.ComplexReason is null ? null : Cut(request.ComplexReason),
                request.Commit is { } commit && CommitShape().IsMatch(commit) ? commit : null, string.IsNullOrWhiteSpace(request.PipelineJustification) ? null : Cut(request.PipelineJustification));
            var result = await conductor.HandOverAsync(id, dossier, ct);
            live.Publish(LiveTopics.Missions, id);
            return result.Ok ? Results.Ok() : Results.Conflict(new { error = result.Refusal });
        });
    }

    /// <summary>
    /// The mission to offer first among those that could be: the more urgent problem first (a release that failed goes ahead of the normal ones, ADR 0050), then the later worlds (what is on its way to production is finished
    /// before something new is started), then the identifier. A mission whose problem is unknown goes last. A running mission is never taken from its runner for it.
    /// </summary>
    internal static Mission? FirstNext(this IEnumerable<Mission> candidates, IProblemStore problems)
    {
        var list = candidates.ToList();
        var runs = problems.GetMany([.. list.Select(candidate => candidate.ProblemId)]);
        return list.OrderByDescending(candidate => runs.TryGetValue(candidate.ProblemId, out var run) ? run.Priority : FlowPriority.Lowest).ThenByDescending(candidate => candidate.World).ThenBy(candidate => candidate.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    // Choosing a mission, taking the lease of its machine and noting that it was offered: one step, under the gate of the claims
    private static IResult Claim(string? world, string? machine, string name, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, ProblemConductor conductor, IDossierStore dossiers, MissionProgress progress, RepairRegistry? repairs, TimeProvider time)
    {
        // A mission is offered when its mandate has just been issued, or when the controller sent it back after a dossier (once for each send-back): the same mandate goes on with the remarks
        var mission = missions.Recent(200).Where(candidate => ((candidate.State == MissionState.Mandated && !progress.RecentlyOffered(candidate.Id, machine)) || (IsSentBack(candidate, dossiers) && !progress.WasOffered(candidate.Id, candidate.SendBacks))) && MandateOf(candidate, mandates) is { } signed && signed.Mandate.RunnerId == name && signed.Mandate.ExpiresAt > time.GetUtcNow() && (world is null || string.Equals(candidate.World.ToString(), world, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => IsItsTurn(candidate, problems))
            .FirstNext(problems);
        if (mission is null || MandateOf(mission, mandates) is not { } mandate || problems.Get(mission.ProblemId)?.Entry is not { Repositories.Count: 1 } entry)
        {
            return Results.Ok(new MissionClaimResponse(null));
        }
        // A mission of a world that works on a lab machine is claimed with a lease on its machine (ADR 0047): one holder, and only an Idle machine. No machine free, the mission waits
        var leaseTaken = false;
        if (mission.World is World.Staging or World.Release && context.RequestServices.GetService<MachinePool>() is { } pool)
        {
            var (leased, taken) = MachineEndpoints.TryLeaseFor(pool, context.RequestServices.GetRequiredService<MissionsOptions>(), machine, name, mission);
            if (!leased)
            {
                return Results.Ok(new MissionClaimResponse(null));
            }
            leaseTaken = taken;
        }
        var rework = mission.State == MissionState.Running;
        if (rework && !progress.TakeRework(mission.Id, mission.SendBacks))
        {
            // Nothing is offered after all: a lease that was just taken is given back (one that the mission already held is not touched: its runner is working on it)
            if (leaseTaken && machine is not null)
            {
                context.RequestServices.GetRequiredService<MachinePool>().Release(machine, mission.Id, "the claim offered nothing");
            }
            return Results.Ok(new MissionClaimResponse(null));
        }
        if (!rework)
        {
            progress.Offered(mission.Id, machine);
        }
        var current = repairs?.Effective(mission.ProblemId, entry);
        return Results.Ok(new MissionClaimResponse(new ClaimedMission(mission.Id, mission.ProblemId, mission.ApplicationId, mission.World.ToString(), mission.Objective, entry.Repositories[0], current?.Number ?? entry.PullRequest, current?.Head ?? entry.HeadSha,
            mandate.Mandate.ExpiresAt, [.. mandate.Mandate.Acts.Select(act => act.ToString())], conductor.AcceptedCommit(mission.Id) ?? entry.HeadSha, rework, rework ? progress.RemarksOf(mission.Id) : null, repairs?.FixOf(mission.ProblemId) is not null ? entry.PullRequest : null, mission.Instruction, mission.World is World.Staging or World.Release ? machine : null)));
    }

    private static bool IsSentBack(Mission candidate, IDossierStore dossiers) => candidate.State == MissionState.Running && candidate.SendBacks > 0 && dossiers.Get(candidate.Id) is not null;

    // One problem at a time on one repository (FR-MIS-6): a mission is offered only when its problem is the oldest of those in progress on its repository, so that what was proved in staging is what is merged and deployed. The priority does not enter here: it changes while a problem runs, and the turn of a repository must not change under a change that is half way to production (ADR 0050)
    internal static bool IsItsTurn(Mission candidate, IProblemStore problems)
    {
        if (problems.Get(candidate.ProblemId) is not { Entry.Repositories.Count: 1 } run)
        {
            return false;
        }
        var repository = run.Entry.Repositories[0];
        var oldest = problems.InProgress().Where(other => other.Entry.Repositories.Count == 1 && string.Equals(other.Entry.Repositories[0], repository, StringComparison.Ordinal))
            .OrderBy(other => other.StartedAt).ThenBy(other => other.ProblemId, StringComparer.Ordinal).FirstOrDefault();
        return oldest is null || string.Equals(oldest.ProblemId, candidate.ProblemId, StringComparison.Ordinal);
    }

    // What a runner may put in a dossier is bounded here as it is in the prompt of the controller: a runner that is not trusted cannot fill the store
    private const int MaxListed = 20;
    private const int MaxTextLength = 2000;

    private static string Cut(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.None, 250)]
    private static partial System.Text.RegularExpressions.Regex CommitShape();

    private static List<string> Bounded(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Take(MaxListed).Select(value => value.Length > MaxTextLength ? value[..MaxTextLength] : value)];

    private static SignedMandate? MandateOf(Mission mission, IMandateLedger mandates) => mission.MandateId is { } id ? mandates.Get(id) : null;

    internal static bool Owns(string id, string runner, IMissionStore missions, IMandateLedger mandates) =>
        missions.Get(id) is { } mission && MandateOf(mission, mandates) is { } signed && signed.Mandate.RunnerId == runner;
}
