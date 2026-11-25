using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.MissionRunner;

/// <summary>
/// The work of the staging world for a pull request: the staging machine is put back to its reference, the pull request is fetched there at the commit that the mandate names,
/// built and tested in a container, and what was seen is handed over as a dossier. The machine is put back again when the work ends (ADR 0041): the next job never works in what this one left.
/// </summary>
public sealed partial class StagingProof(IShell shell, MissionRunnerOptions options, RepairAgent? repair = null, ChangeProof? change = null) : IWorldWork
{
    public string World => "Staging";

    // The messages of git are read by the code (a conflict): every command of the staging proof runs in the C locale, whatever the machine speaks
    private const string Prelude = "export LC_ALL=C DOCKER_HOST=unix:///run/user/$(id -u)/docker.sock; set -e; ";

    public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
    {
        // A problem that has no pull request is solved by a free agent, not proved (ADR 0048)
        if (mission.PullRequest is null && change is not null)
        {
            return await change.RunAsync(mission, report, cancellationToken);
        }
        if (mission.PullRequest is not { } number || !RepositoryShape().IsMatch(mission.Repository))
        {
            return Refused(mission, "This runner proves pull requests only for now: the mission names no pull request or an unreadable repository.");
        }
        if (!options.Repositories.Any(pattern => RepositoryPattern.Matches(pattern, mission.Repository)))
        {
            return Refused(mission, $"This runner does not accept {mission.Repository} (MissionRunner:Repositories): it is the one check it makes of its own.");
        }
        if (mission.HeadSha is not { Length: > 0 })
        {
            return Refused(mission, "The mission names no commit for the pull request: nothing is built without knowing which commit was approved.");
        }
        if ((mission.Machine ?? options.StagingMachine) is not { Length: > 0 } machine)
        {
            return Refused(mission, "The runner is not told which machine to work on (MissionRunner:StagingMachine).");
        }
        var timeout = TimeSpan.FromMinutes(options.StepTimeoutMinutes);
        var evidence = new List<string>();
        await report($"Fetching {mission.Repository} pull request #{number}");
        var checkout = await OnMachineAsync(machine,
            $"rm -rf ~/work && mkdir -p ~/work && cd ~/work && git clone --quiet https://github.com/{mission.Repository}.git repo && cd repo && git fetch --quiet origin pull/{number}/head:pr && git checkout --quiet pr && echo \"HEAD $(git rev-parse HEAD)\" && {MergeResult}",
            timeout, cancellationToken);
        Keep(evidence, "checkout", checkout);
        var head = HeadLine().Match(checkout.Output) is { Success: true } found ? found.Groups[1].Value : null;
        var tree = TreeLine().Match(checkout.Output) is { Success: true } treeFound ? treeFound.Groups[1].Value : null;
        if (checkout.ExitCode != 0 || head is null || tree is null)
        {
            var conflict = checkout.Output.Contains("CONFLICT (", StringComparison.Ordinal);
            return Refused(mission, $"The pull request could not be fetched and merged on the staging machine ({(conflict ? "a conflict with the base branch" : "the checkout failed")}): {Tail(checkout.Output)}", conflict ? MissionEvidence.RefusedConflict : null);
        }
        // What is built and tested is the result of the merge, and its tree names the artifact: the release must be that tree (FR-WLD-14)
        evidence.Add($"{MissionEvidence.ArtifactTree}{tree}");
        if (mission.HeadSha is { Length: > 0 } expected && !head.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(mission, $"The pull request moved since it was approved (approved {Short(expected)}, found {Short(head)}): nothing was built.", MissionEvidence.RefusedMoved);
        }

        var recipe = await OnMachineAsync(machine, "cd ~/work/repo && ls -1", timeout, cancellationToken);
        var files = recipe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var (kind, command) = Recipe(files);
        if (command is null)
        {
            return new MissionHandOver($"Pull request #{number} was fetched at {Short(head)} but this runner knows no way to build and test it.",
                $"Fetched {mission.Repository}#{number} at {head}. Nothing recognised to build (no package.json, requirements.txt or compose file).",
                evidence, ["No build recipe for this repository."], Complex: true, "No build recipe for this repository.", head);
        }

        await report($"Building and testing ({kind}) in a container");
        var build = await OnMachineAsync(machine, $"cd ~/work/repo && {command}", timeout, cancellationToken);
        Keep(evidence, kind, build);
        var passed = build.ExitCode == 0 && !build.TimedOut;
        // A change that does not pass is repaired by the worker agent when there is one (a build that timed out is not: nothing says what to repair)
        if (!passed && !build.TimedOut && repair is { IsConfigured: true })
        {
            var repaired = await repair.TryAsync(machine, mission, command, build.Output, report, cancellationToken);
            evidence.AddRange(repaired.Evidence);
            if (repaired.Repaired && repaired.Tree is { } fixedTree)
            {
                // What was proved is now the merge result with the repair: its tree is the artifact that the release must be
                evidence.RemoveAll(entry => entry.StartsWith(MissionEvidence.ArtifactTree, StringComparison.Ordinal));
                evidence.Add($"{MissionEvidence.ArtifactTree}{fixedTree}");
                await report($"Repaired: {repaired.Files.Count} file(s) changed, and the steps pass");
                return new MissionHandOver(
                    $"Pull request #{number} of {mission.Repository} at {Short(head)} did not pass on the staging machine ({kind}); a worker agent repaired the source ({string.Join(", ", repaired.Files.Select(file => file.Path))}) and with that repair it builds and its tests pass.",
                    $"Fetched at {head}; as it is, it failed: {Tail(build.Output)}. A worker agent changed {repaired.Files.Count} file(s) ({string.Join(", ", repaired.Files.Select(file => file.Path))}); the agent cannot run commands, so the runner ran the steps again itself on the repaired source, and they passed: {Tail(repaired.LastOutput)}. What the agent said: {repaired.Summary}", evidence, [], Complex: false, ComplexReason: null, head, repaired.Files);
            }
            evidence.Add("repair:none");
            await report($"The agent could not repair it: {repaired.Summary}");
        }
        var outcome = build.TimedOut ? "timed out" : $"exit {build.ExitCode}";
        await report(passed ? "Build and tests passed" : $"Build or tests failed ({outcome})");
        var claim = passed
            ? $"Pull request #{number} of {mission.Repository} at {Short(head)} builds and its tests pass on a staging machine that was restored to its reference and qualified before the job ({kind})."
            : $"Pull request #{number} of {mission.Repository} at {Short(head)} does NOT pass on the staging machine ({kind}): {outcome}.";
        return new MissionHandOver(claim, $"Fetched at {head}; changes: {FirstLines(checkout.Output, 4)}. Output tail: {Tail(build.Output)}", evidence,
            passed ? [] : [Tail(build.Output)], Complex: false, ComplexReason: null, head);
    }

    internal static (string Kind, string? Command) Recipe(IReadOnlySet<string> files)
    {
        if (files.Contains("package.json"))
        {
            var install = files.Contains("package-lock.json") ? "npm ci" : "npm install";
            return ("node", $"docker run --rm --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --memory 2g --cpus 2 -v \"$PWD\":/app -v \"$PWD/.git\":/app/.git:ro -w /app node:22-alpine sh -c \"{install} --no-audit --no-fund && npm test\"");
        }
        if (files.Contains("requirements.txt"))
        {
            return ("python", "docker run --rm --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --memory 2g --cpus 2 -v \"$PWD\":/app -v \"$PWD/.git\":/app/.git:ro -w /app python:3.12-slim sh -c \"pip install -q -r requirements.txt pytest && python -m pytest -q\"");
        }
        var compose = new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }.FirstOrDefault(files.Contains);
        return compose is null ? ("none", null) : ("compose", $"docker compose -f {compose} config -q");
    }

    private Task<ShellResult> OnMachineAsync(string machine, string script, TimeSpan timeout, CancellationToken cancellationToken) =>
        shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, Prelude + script], timeout, cancellationToken);

    private static MissionHandOver Refused(ClaimedMission mission, string reason, string? marker = null) =>
        new($"Nothing was proven for {mission.Repository}: {reason}", reason, marker is null ? [] : [marker], [reason], Complex: true, reason, Commit: null);

    private static void Keep(List<string> evidence, string step, ShellResult result) =>
        evidence.Add($"{step}:exit={result.ExitCode}:sha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output)))}");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(6));
    }

    private static string FirstLines(string output, int count) =>
        string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(2).Take(count));

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RepositoryShape();

    [GeneratedRegex(@"^TREE ([0-9a-f]{40})\s*$", RegexOptions.Multiline)]
    private static partial Regex TreeLine();

    // The merge is made here, on the base branch as it is now, and not read from the forge: the test merge that the forge keeps is computed lazily and can be of a base that has moved since. A conflict stops the script.
    private const string MergeResult = "git checkout --quiet -B merged origin/HEAD && git -c user.name=orchestrarr -c user.email=orchestrarr@localhost merge --quiet --no-edit --no-ff pr && echo \"TREE $(git rev-parse 'HEAD^{tree}')\" && git diff --stat origin/HEAD HEAD | tail -8";

    [GeneratedRegex(@"^HEAD ([0-9a-f]{40})\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadLine();
}
