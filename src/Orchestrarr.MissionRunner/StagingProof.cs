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
public sealed partial class StagingProof(IShell shell, MissionRunnerOptions options)
{
    private const string Prelude = "export DOCKER_HOST=unix:///run/user/$(id -u)/docker.sock; set -e; ";

    public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
    {
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
        if (options.StagingMachine is not { Length: > 0 } machine)
        {
            return Refused(mission, "The runner is not told which machine to work on (MissionRunner:StagingMachine).");
        }
        var timeout = TimeSpan.FromMinutes(options.StepTimeoutMinutes);
        var evidence = new List<string>();
        try
        {
            await report($"Putting {machine} back to its reference snapshot");
            if (options.LabTool is null)
            {
                return Refused(mission, "The runner has no tool to restore the staging machine (MissionRunner:LabTool): it does not work on a machine it cannot restore.");
            }
            var reset = await shell.RunAsync(options.LabTool, ["reset", machine], TimeSpan.FromMinutes(5), cancellationToken);
            Keep(evidence, "reset", reset);
            if (reset.ExitCode != 0)
            {
                return Refused(mission, $"The staging machine could not be restored: {Tail(reset.Output)}");
            }

            await report($"Fetching {mission.Repository} pull request #{number}");
            var checkout = await OnMachineAsync(machine,
                $"rm -rf ~/work && mkdir -p ~/work && cd ~/work && git clone --quiet https://github.com/{mission.Repository}.git repo && cd repo && git fetch --quiet origin pull/{number}/head:pr && git checkout --quiet pr && echo \"HEAD $(git rev-parse HEAD)\" && git diff --stat origin/HEAD...HEAD | tail -8",
                timeout, cancellationToken);
            Keep(evidence, "checkout", checkout);
            var head = HeadLine().Match(checkout.Output) is { Success: true } found ? found.Groups[1].Value : null;
            if (checkout.ExitCode != 0 || head is null)
            {
                return Refused(mission, $"The pull request could not be fetched on the staging machine: {Tail(checkout.Output)}");
            }
            if (mission.HeadSha is { Length: > 0 } expected && !head.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return Refused(mission, $"The pull request moved since it was approved (approved {Short(expected)}, found {Short(head)}): nothing was built.");
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
            var outcome = build.TimedOut ? "timed out" : $"exit {build.ExitCode}";
            await report(passed ? "Build and tests passed" : $"Build or tests failed ({outcome})");
            var claim = passed
                ? $"Pull request #{number} of {mission.Repository} at {Short(head)} builds and its tests pass on a staging machine restored to its reference ({kind})."
                : $"Pull request #{number} of {mission.Repository} at {Short(head)} does NOT pass on the staging machine ({kind}): {outcome}.";
            return new MissionHandOver(claim, $"Fetched at {head}; changes: {FirstLines(checkout.Output, 4)}. Output tail: {Tail(build.Output)}", evidence,
                passed ? [] : [Tail(build.Output)], Complex: false, ComplexReason: null, head);
        }
        finally
        {
            // The machine is given back clean whatever happened, first; what is said about it comes after and cannot skip it (a stop or a lost connection must not leave the work behind)
            var restored = await RestoreAsync(machine);
            await ReportQuietlyAsync(report, restored);
        }
    }

    private async Task<string> RestoreAsync(string machine)
    {
        if (options.LabTool is null)
        {
            return $"{machine} was not restored: the runner has no tool to do it";
        }
        try
        {
            var back = await shell.RunAsync(options.LabTool, ["reset", machine], TimeSpan.FromMinutes(5), CancellationToken.None);
            return back.ExitCode == 0 ? $"{machine} restored" : $"Restoring {machine} FAILED: {Tail(back.Output)}";
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // What is said replaces nothing: the dossier or the stop that the work ended with is still the answer
            return $"Restoring {machine} FAILED: {failure.Message}";
        }
    }

    private static async Task ReportQuietlyAsync(Func<string, Task> report, string line)
    {
        try
        {
            await report(line);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // The machine is restored; a line that cannot be sent is lost, not an error of the work
        }
    }

    internal static (string Kind, string? Command) Recipe(IReadOnlySet<string> files)
    {
        if (files.Contains("package.json"))
        {
            var install = files.Contains("package-lock.json") ? "npm ci" : "npm install";
            return ("node", $"docker run --rm --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --memory 2g --cpus 2 -v \"$PWD\":/app -w /app node:22-alpine sh -c \"{install} --no-audit --no-fund && npm test\"");
        }
        if (files.Contains("requirements.txt"))
        {
            return ("python", "docker run --rm --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --memory 2g --cpus 2 -v \"$PWD\":/app -w /app python:3.12-slim sh -c \"pip install -q -r requirements.txt pytest && python -m pytest -q\"");
        }
        var compose = new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }.FirstOrDefault(files.Contains);
        return compose is null ? ("none", null) : ("compose", $"docker compose -f {compose} config -q");
    }

    private Task<ShellResult> OnMachineAsync(string machine, string script, TimeSpan timeout, CancellationToken cancellationToken) =>
        shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, Prelude + script], timeout, cancellationToken);

    private static MissionHandOver Refused(ClaimedMission mission, string reason) =>
        new($"Nothing was proven for {mission.Repository}: {reason}", reason, [], [reason], Complex: true, reason, Commit: null);

    private static void Keep(List<string> evidence, string step, ShellResult result) =>
        evidence.Add($"{step}:exit={result.ExitCode}:sha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output)))}");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(6));
    }

    private static string FirstLines(string output, int count) =>
        string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Take(count));

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RepositoryShape();

    [GeneratedRegex(@"^HEAD ([0-9a-f]{40})\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadLine();
}
