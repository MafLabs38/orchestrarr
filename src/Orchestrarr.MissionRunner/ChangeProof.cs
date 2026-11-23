using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.MissionRunner;

/// <summary>
/// The staging world for a problem that has **no pull request** (ADR 0048): a worker agent, free inside the staging machine, makes the change that solves it. The runner clones the default branch on the machine, starts the agent in a
/// container with the tools of a developer (the machine is isolated, and thrown away after the job, so nothing the agent does there needs a rule), then **reads back the files itself**, applies the policy of what may leave (never the
/// pipelines, the files that hold a secret or that instruct an agent), and **runs the steps of the repository again on its own**: what is handed over is what the runner saw, never what the agent said. The agent holds no credential
/// that writes to a repository; the control plane opens the pull request from the files.
/// </summary>
public sealed partial class ChangeProof(IShell shell, MissionRunnerOptions options)
{
    private const string Prelude = "export DOCKER_HOST=unix:///run/user/$(id -u)/docker.sock; set -e; ";

    public bool IsConfigured => options.Repair.IsFreeConfigured;

    public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
    {
        if (!RepositoryShape().IsMatch(mission.Repository) || !options.Repositories.Any(pattern => RepositoryPattern.Matches(pattern, mission.Repository)))
        {
            return Refused(mission, $"This runner does not accept {mission.Repository} (MissionRunner:Repositories).");
        }
        if (options.StagingMachine is not { Length: > 0 } machine)
        {
            return Refused(mission, "The runner is not told which machine to work on (MissionRunner:StagingMachine).");
        }
        if (!IsConfigured)
        {
            return Refused(mission, "This runner has no free agent for a problem that has no pull request (MissionRunner:Repair:FreeDockerfileFile and TokenFile).");
        }
        var settings = options.Repair;
        var evidence = new List<string>();
        var quick = TimeSpan.FromMinutes(5);

        await report($"Fetching {mission.Repository} (its default branch)");
        var clone = await OnMachineAsync(machine, $"rm -rf ~/work ~/agentin && mkdir -p ~/work ~/agentin && cd ~/work && git clone --quiet {string.Format(CultureInfo.InvariantCulture, settings.FreeRepositoryUrl, mission.Repository)} repo && cd repo && echo \"HEAD $(git rev-parse HEAD)\" && echo \"BRANCH $(git rev-parse --abbrev-ref HEAD)\"", quick, cancellationToken);
        var head = HeadLine().Match(clone.Output) is { Success: true } headFound ? headFound.Groups[1].Value : null;
        var branch = BranchLine().Match(clone.Output) is { Success: true } branchFound ? branchFound.Groups[1].Value : null;
        Keep(evidence, "clone", clone);
        if (clone.ExitCode != 0 || head is null || branch is null)
        {
            return Refused(mission, $"The repository could not be fetched on the staging machine: {Last(clone.Output)}");
        }

        await report("Preparing the free agent on the staging machine");
        var dockerfile = await File.ReadAllTextAsync(settings.FreeDockerfileFile!, cancellationToken);
        // The tag follows the content of the Dockerfile: a new Dockerfile is a new image, an old one is never run by mistake
        var tag = $"{settings.FreeImage}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dockerfile)))[..12].ToLowerInvariant()}";
        var image = await OnMachineAsync(machine, $"docker image inspect {tag} >/dev/null 2>&1 || docker build -q -t {tag} -", TimeSpan.FromMinutes(20), cancellationToken, input: dockerfile);
        if (image.ExitCode != 0)
        {
            return Refused(mission, $"The free agent's image could not be built: {Last(image.Output)}");
        }
        var token = (await File.ReadAllTextAsync(settings.TokenFile!, cancellationToken)).Trim();
        if (token.Length == 0)
        {
            return Refused(mission, "The token file is empty.");
        }
        var written = await OnMachineAsync(machine, "cat > ~/agentin/prompt.txt", quick, cancellationToken, input: Prompt(mission, head, branch));
        if (written.ExitCode != 0)
        {
            return Refused(mission, $"The task of the agent could not be written on the machine: {Last(written.Output)}");
        }

        await report("The free agent works on the staging machine");
        var run = await OnMachineAsync(machine, AgentScript(settings, tag), TimeSpan.FromMinutes(settings.FreeTimeoutMinutes), cancellationToken, input: token + "\n");
        // What the agent said is its own words, kept short; the token is never in what comes back
        var said = Last(run.Output.Replace(token, "***", StringComparison.Ordinal));
        evidence.Add($"agent:exit={run.ExitCode}");
        if (run.ExitCode != 0 || run.TimedOut)
        {
            var why = run.TimedOut ? "it ran out of time" : $"it stopped with an error ({said})";
            await report($"The agent did not finish: {why}");
            return Unfinished(mission, $"The free agent did not finish: {why}.", evidence, head);
        }

        // What the agent left in the folder is read back by the runner, now, before anything runs again: the steps install and run the code being proved, and what they leave is not the agent's
        var status = await OnMachineAsync(machine, "cd ~/work/repo && git status --porcelain=v1 --untracked-files=all", quick, cancellationToken);
        var (changed, refusal) = ChangedPaths(status);
        if (status.ExitCode != 0 || refusal is not null)
        {
            evidence.Add("change:refused");
            return Unfinished(mission, $"The change cannot be carried: {refusal ?? Last(status.Output)}.", evidence, head);
        }
        if (changed.Count == 0)
        {
            evidence.Add("change:none");
            await report("The agent changed nothing that can be carried");
            return Unfinished(mission, $"The agent changed nothing. It said: {said}", evidence, head);
        }
        // Each file is read in slices: what a call may bring back is bounded (a lock file is far over it), and a slice that was cut would be a file that was read wrong
        var read = await ReadFilesAsync(machine, changed, quick, cancellationToken);
        var files = read.Files;
        var tree = read.Tree;
        var policy = files.Count == changed.Count ? ChangePolicy.Refusal(files) : null;
        if (read.Failure is not null || tree is null || files.Count != changed.Count || policy is not null)
        {
            evidence.Add("change:refused");
            return Unfinished(mission, $"The change cannot be carried: {policy ?? read.Failure ?? "the files could not be read back"}.", evidence, head);
        }

        // The steps of the repository are run again by the runner, on what it saw: the proof is its own
        await report($"The agent changed {files.Count} file(s); running the steps of the repository");
        var listing = await OnMachineAsync(machine, "cd ~/work/repo && ls -1", quick, cancellationToken);
        var (kind, command) = StagingProof.Recipe(listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal));
        if (command is null)
        {
            return Unfinished(mission, "The runner knows no way to build and test this repository, so the change was not proved.", evidence, head, complex: true);
        }
        // What the agent left running or tagged on the docker of the machine is not part of the proof: the containers are removed before the steps are run again
        _ = await OnMachineAsync(machine, "docker ps -aq | xargs -r docker rm -f >/dev/null 2>&1; true", quick, cancellationToken);
        var build = await OnMachineAsync(machine, $"cd ~/work/repo && {command}", TimeSpan.FromMinutes(options.StepTimeoutMinutes), cancellationToken);
        Keep(evidence, kind, build);
        if (build.ExitCode != 0 || build.TimedOut)
        {
            var outcome = build.TimedOut ? "timed out" : $"exit {build.ExitCode}";
            await report($"The steps of the repository do not pass after the change ({outcome})");
            return Unfinished(mission, $"The change of the agent does NOT pass the steps of the repository ({kind}): {outcome}. Output tail: {Last(build.Output)}", evidence, head);
        }
        evidence.Add($"{MissionEvidence.ArtifactTree}{tree}");
        evidence.Add($"{MissionEvidence.Repair}{files.Count}");
        await report($"The change passes: {files.Count} file(s), the steps of the repository pass");
        // What changed, as the runner reads it in the repository (the agent's account is not evidence): the size of the change of every file, then the lines of the files that are not generated. A cut is said, never silent
        var shown = changed.Where(path => !path.EndsWith("lock.json", StringComparison.Ordinal) && !path.EndsWith(".lock", StringComparison.Ordinal) && !path.EndsWith("lock.yaml", StringComparison.Ordinal)).Take(MaxShownFiles).ToList();
        var stat = await OnMachineAsync(machine, $"cd ~/work/repo && git add -N -- {string.Join(' ', changed)} && git diff --no-color --stat=120 HEAD -- {string.Join(' ', changed)} | head -c {StatCharacters}", quick, cancellationToken);
        var diff = shown.Count == 0
            ? new ShellResult(0, string.Empty, false)
            : await OnMachineAsync(machine, $"cd ~/work/repo && git diff --no-color -U0 HEAD -- {string.Join(' ', shown)} | grep -E '^[-+@]' | head -c {DiffCharacters + 1}", quick, cancellationToken);
        var lines = diff.ExitCode == 0 ? diff.Output.Trim() : string.Empty;
        var cut = lines.Length > DiffCharacters || changed.Count > shown.Count && shown.Count == MaxShownFiles;
        var lockNote = shown.Count < changed.Count ? $" Generated files changed too (their lines are not shown): {string.Join(", ", changed.Except(shown))}." : string.Empty;
        var changeText = $"{(stat.ExitCode == 0 && stat.Output.Trim().Length > 0 ? stat.Output.Trim() : "size not read")}. Lines: {(lines.Length > 0 ? lines[..Math.Min(lines.Length, DiffCharacters)] : "none that are not generated")}{(cut ? " [CUT: the rest of the lines is not shown]" : string.Empty)}";
        return new MissionHandOver(
            $"{mission.Repository}: a free agent made a change ({string.Join(", ", files.Select(file => file.Path))}) and the steps of the repository ({kind}) pass on a staging machine that was restored to its reference and qualified before the job.",
            $"Made against {Short(head)} of {branch}. The agent said: {said}. The steps were run again by the runner on the files it read back: {Last(build.Output)}. What changed, read by the runner in the repository: {changeText}.{lockNote}", evidence, [], Complex: false, ComplexReason: null, head, files);
    }

    // What is told to the agent: the frame of the runner (what the machine is, what must not leave it, how the work is handed over), then the task of the lead, then what the controller said of a try that was sent back
    private static string Prompt(ClaimedMission mission, string head, string branch)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"You are a developer working alone on an isolated machine, on a clone of the repository {mission.Repository} at /work (branch {branch}, commit {head[..8]}). ");
        text.Append("You have every tool of a developer: run any command, install any package, edit any file, and use docker (the socket of the machine is mounted, so `docker run` works). The machine is thrown away after this job and cannot reach anything but the Internet, so try what you need: build, run and break things freely. ");
        text.Append("Your task comes from the lead of the control plane, who read the whole problem. Do what a good developer would do to solve it, in the way you judge best. ");
        text.Append(mission.Instruction is { Length: > 0 } instruction ? $"The task: {instruction} " : "The task: solve the problem that this repository has, as you find it. ");
        text.Append("When you are done, the change must be in the working tree of /work: leave the files modified or added in place. Do not commit and do not push: the control plane carries your files into a pull request. Do not delete or rename files (the change is carried as added and modified files). ");
        text.Append("Never put in the change a CI or pipeline file (.github, .gitlab-ci.yml and the like), a file that holds a secret (.env, keys), or a file that instructs an agent (CLAUDE.md, .claude): they are not carried. ");
        text.Append("The application does not run on this machine's tools in production: if the repository has a Dockerfile, production is built from it, so the change must work on the runtime that it names (the version of node, python and so on, not the newest one you have here). Check it the way production runs it: build the image and start the container, and see that it answers. ");
        text.Append("Make the tests of the repository pass, and add or update tests if the change needs it. The steps of the repository will be run again by the runner on what you leave, and then the pipelines and a controller judge the change. ");
        if (mission.Remarks is { Length: > 0 } remarks)
        {
            text.Append(CultureInfo.InvariantCulture, $"This is a new try: the controller sent your previous one back with this remark: {remarks.ReplaceLineEndings(" ")} ");
        }
        text.Append("Finish with 3 to 6 plain sentences: what you changed and why, what you tested, and what a person should look at.");
        return text.ToString();
    }

    // The agent is root in its container (the container of a rootless docker is the machine's own user, which is what lets it use the socket of the docker of the machine), the permissions of the tool are off because the machine is
    // the boundary, and the token of the subscription reaches it by the standard input of the connection, never in an argument
    private static string AgentScript(RepairSettings settings, string image) =>
        "read -r CLAUDE_CODE_OAUTH_TOKEN && export CLAUDE_CODE_OAUTH_TOKEN && "
        + $"docker run --rm -i --user 0 --memory 7g --cpus 3 -e CLAUDE_CODE_OAUTH_TOKEN -e IS_SANDBOX=1 -v ~/work/repo:/work -v ~/agentin:/in:ro -v /run/user/$(id -u)/docker.sock:/var/run/docker.sock -w /work {image} "
        + $"sh -c 'exec claude -p \"$(cat /in/prompt.txt)\" --model {settings.Model} --dangerously-skip-permissions --max-turns {settings.FreeMaxTurns.ToString(CultureInfo.InvariantCulture)}'";

    // `git status --porcelain`: two letters, a space, the path. Only added and modified files are carried; what a build or an install leaves in the folder is not part of the change
    private static (List<string> Paths, string? Refusal) ChangedPaths(ShellResult status)
    {
        var paths = new List<string>();
        foreach (var line in status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
            {
                continue;
            }
            var code = line[..2];
            var path = line[3..].Trim();
            // What the build leaves is not a change, but only a file that git does not track yet is taken for it: a tracked file under a folder named bin or build is source, and a change to it is carried
            if (code == "??" && (ChangePolicy.IsArtifact(path) || ChangePolicy.IsArtifact(path.TrimEnd('/') + "/x")))
            {
                continue;
            }
            if (code.Contains('D') || code.Contains('R') || path.Contains(" -> ", StringComparison.Ordinal))
            {
                return ([], $"'{path}' is deleted or renamed, and a change is carried as added and modified files");
            }
            if (!RepairPolicy.IsPlainPath(path) || ChangePolicy.IsProtected(path))
            {
                return ([], $"'{path}' is not a file that may leave the machine");
            }
            paths.Add(path);
        }
        return (paths, null);
    }

    private const int SliceCharacters = 120_000;
    private const int DiffCharacters = 8_000;
    private const int StatCharacters = 3_000;
    private const int MaxShownFiles = 40;

    // The base64 text of n bytes: four characters for each three bytes, rounded up
    private static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    private sealed record FilesRead(List<PatchedFile> Files, string? Tree, string? Failure);

    // The files that the agent changed, read back one by one in slices of the base64 text (a multiple of four characters, so that each slice is decoded alone), and the tree that they make on top of the commit
    private async Task<FilesRead> ReadFilesAsync(string machine, IReadOnlyList<string> changed, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var files = new List<PatchedFile>();
        foreach (var path in changed)
        {
            var size = await OnMachineAsync(machine, $"cd ~/work/repo && base64 -w0 \"{path}\" | wc -c", timeout, cancellationToken);
            if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var characters) || characters > Base64Length(ChangePolicy.MaxFileBytes))
            {
                return new FilesRead(files, null, $"{path} could not be measured");
            }
            var bytes = new List<byte>();
            for (long from = 0; from < characters; from += SliceCharacters)
            {
                var slice = await OnMachineAsync(machine, $"cd ~/work/repo && base64 -w0 \"{path}\" | cut -c{from + 1}-{from + SliceCharacters}", timeout, cancellationToken);
                if (slice.ExitCode != 0)
                {
                    return new FilesRead(files, null, $"{path} could not be read back");
                }
                try
                {
                    bytes.AddRange(Convert.FromBase64String(slice.Output.Trim()));
                }
                catch (FormatException)
                {
                    return new FilesRead(files, null, $"{path} could not be read back");
                }
            }
            // A change is carried as text: a file that is not valid text (an image, a font) cannot be carried without altering it
            try
            {
                files.Add(new PatchedFile(path, new UTF8Encoding(false, true).GetString([.. bytes])));
            }
            catch (DecoderFallbackException)
            {
                return new FilesRead(files, null, $"{path} is not a text file, and only text can be carried");
            }
        }
        var tree = await OnMachineAsync(machine, $"cd ~/work/repo && export GIT_INDEX_FILE=$(mktemp -u) && git read-tree HEAD && git update-index --add -- {string.Join(' ', changed)} && echo \"TREE $(git write-tree)\"", timeout, cancellationToken);
        var found = TreeLine().Match(tree.Output);
        return new FilesRead(files, tree.ExitCode == 0 && found.Success ? found.Groups[1].Value : null, tree.ExitCode == 0 ? null : Last(tree.Output));
    }

    private Task<ShellResult> OnMachineAsync(string machine, string script, TimeSpan timeout, CancellationToken cancellationToken, string? input = null) =>
        input is null
            ? shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, Prelude + script], timeout, cancellationToken)
            : shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, Prelude + script], input, timeout, cancellationToken);

    private static MissionHandOver Refused(ClaimedMission mission, string reason) =>
        new($"Nothing was done for {mission.Repository}: {reason}", reason, [], [reason], Complex: true, reason, Commit: null);

    // The agent worked and nothing can be carried: said as it is, with no files, so that the controller judges a dossier that holds no change
    private static MissionHandOver Unfinished(ClaimedMission mission, string claim, List<string> evidence, string head, bool complex = false) =>
        new($"{mission.Repository}: {claim}", claim, evidence, [claim], complex, complex ? claim : null, head);

    private static void Keep(List<string> evidence, string step, ShellResult result) =>
        evidence.Add($"{step}:exit={result.ExitCode}:sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result.Output)))}");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Last(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = string.Join(" | ", lines.TakeLast(5));
        return text.Length > 600 ? text[^600..] : text;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$", RegexOptions.None, 250)]
    private static partial Regex RepositoryShape();

    [GeneratedRegex(@"^HEAD ([0-9a-f]{40})\s*$", RegexOptions.Multiline, 250)]
    private static partial Regex HeadLine();

    [GeneratedRegex(@"^BRANCH ([A-Za-z0-9._/-]{1,100})\s*$", RegexOptions.Multiline, 250)]
    private static partial Regex BranchLine();

    [GeneratedRegex(@"^TREE ([0-9a-f]{40})\s*$", RegexOptions.Multiline, 250)]
    private static partial Regex TreeLine();
}
