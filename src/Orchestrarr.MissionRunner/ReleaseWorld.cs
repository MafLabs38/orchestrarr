using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>
/// The work of the release world: the act gateway says whether the commit that the controller accepted may be deployed now, then the runner deploys it **the way the environment is deployed** (here a container built on the deployment
/// machine from the repository at that commit), and verifies the application as a tester would. The previous version is read before anything is replaced and said in the dossier, and nothing is rolled back by this runner: a release that
/// does not verify is a failed push, and the loop goes on with the same problem (ADR 0040).
/// </summary>
public sealed partial class ReleaseWorld(IMissionControlPlane controlPlane, IShell shell, MissionRunnerOptions options) : IWorldWork
{
    private const string Prelude = "export DOCKER_HOST=unix:///run/user/$(id -u)/docker.sock; set -e; ";

    public string World => "Release";

    public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
    {
        if ((mission.Machine ?? options.DeployMachine) is not { Length: > 0 } machine || !RepositoryShape().IsMatch(mission.Repository))
        {
            return Refused(mission, "The runner has no deployment machine, or the mission names an unreadable repository.");
        }
        if (!options.Repositories.Any(pattern => Orchestrarr.Core.Tasks.RepositoryPattern.Matches(pattern, mission.Repository)))
        {
            return Refused(mission, $"This runner does not accept {mission.Repository} (MissionRunner:Repositories).");
        }
        var answer = await controlPlane.DeployAsync(mission.MissionId, cancellationToken);
        if (!answer.Allowed || answer.Commit is not { } commit || !Sha().IsMatch(commit))
        {
            var why = answer.Failures.Count > 0 ? string.Join(", ", answer.Failures) : answer.Refusal ?? "refused";
            return new MissionHandOver($"The deployment of {mission.Repository} was not allowed: {why}.", $"The act gateway did not allow the deployment: {why}.", ["deploy:refused"], [why], Complex: false, ComplexReason: null, Commit: null);
        }

        var app = AppName(mission.Repository);
        var port = PortOf(mission.Repository);
        await report($"Deploying {Short(commit)} to {machine} as {app} on port {port}");
        var timeout = TimeSpan.FromMinutes(options.StepTimeoutMinutes);
        var evidence = new List<string>();
        var deploy = await OnMachineAsync(machine, DeployScript(mission.Repository, commit, app, port), timeout, cancellationToken);
        Keep(evidence, "deploy", deploy);
        var previous = PreviousLine().Match(deploy.Output) is { Success: true } found ? found.Groups[1].Value.Trim() : "none";
        evidence.Add($"previous:{previous}");
        var address = $"{options.DeployHost ?? machine}:{port}";
        if (deploy.ExitCode != 0 || deploy.TimedOut)
        {
            await report("The deployment did not complete");
            return Failed(commit, evidence, $"The deployment of {Short(commit)} to {machine} did not complete (exit {deploy.ExitCode}): {Tail(deploy.Output)}", previous);
        }

        await report("Verifying the application as a tester would");
        var verify = await OnMachineAsync(machine, VerifyScript(app, port), TimeSpan.FromMinutes(3), cancellationToken);
        Keep(evidence, "verify", verify);
        if (verify.ExitCode != 0 || verify.TimedOut)
        {
            await report("The verification failed");
            return Failed(commit, evidence, $"{app} was deployed at {Short(commit)} on {address} but its verification failed: {Tail(verify.Output)}", previous);
        }
        await report($"{app} answers on {address}");
        var replaces = mission.Replaces is { } original ? $" (it replaces #{original}, which did not pass as it was: it carries the same change with a repair of the source)" : string.Empty;
        var what = mission.PullRequest is { } number ? $"pull request #{number} of {mission.Repository}{replaces}, merged as {Short(commit)} after its proof in staging," : $"{mission.Repository} at {Short(commit)}";
        return new MissionHandOver($"The change ({what.TrimEnd(',')}) is deployed on {machine} as {app} ({address}) and verified: {Tail(verify.Output)}.",
            $"Deployed {commit}, the commit that the controller accepted for {mission.Repository}, on {machine} as the container orch-{app} (image orch-{app}:{commit[..8]}), from the repository at that commit. Previous version: {previous}. "
            + $"The application was then checked as a tester would: {Tail(verify.Output)}. The deployment was allowed by the act gateway on that commit; nothing else was changed or rolled back.", evidence, [], Complex: false, ComplexReason: null, commit);
    }

    // What is run on the deployment machine: every value in it is validated here (repository, commit, port); the application is built from the Dockerfile of the repository. A port that another application already holds stops the deployment.
    // A compose file is not brought up: it can mount the host, join its network or add devices, and nothing here judges it (ADR 0043).
    private static string DeployScript(string repository, string commit, string app, int port)
    {
        var published = port.ToString(CultureInfo.InvariantCulture);
        return $"mkdir -p ~/deploy && cd ~/deploy && {{ [ -d {app} ] || git clone --quiet https://github.com/{repository}.git {app}; }} && cd {app} && git fetch --quiet origin && git checkout --quiet -f {commit} && "
            + $"echo \"PREVIOUS $(docker inspect -f '{{{{.Config.Image}}}}' orch-{app} 2>/dev/null || echo none)\" && "
            + $"other=$(docker ps --filter publish={published} --format '{{{{.Names}}}}' | grep -vx orch-{app} || true); if [ -n \"$other\" ]; then echo \"port {published} is used by $other\" >&2; exit 4; fi && "
            + "if [ -f Dockerfile ]; then "
            + $"inner=$(grep -m1 -i '^EXPOSE' Dockerfile | awk '{{print $2}}' | cut -d/ -f1); inner=${{inner:-80}}; "
            + $"docker build --quiet -t orch-{app}:{commit[..8]} . >/dev/null && (docker rm -f orch-{app} >/dev/null 2>&1 || true) && "
            + $"docker run -d --name orch-{app} --restart unless-stopped -p {published}:$inner orch-{app}:{commit[..8]} >/dev/null; "
            + "else echo 'nothing to deploy: no Dockerfile (a compose file is not deployed here)' >&2; exit 3; fi";
    }

    // The tester's check: the application answers its health route, tried for a minute because a service takes time to start
    private static string VerifyScript(string app, int port) =>
        "ok=0; for i in $(seq 1 30); do "
        + $"if docker inspect -f '{{{{.State.Running}}}}' orch-{app} 2>/dev/null | grep -q true; then code=$(curl -s -o /dev/null -w '%{{http_code}}' --max-time 3 http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/health || true); "
        + "if [ \"$code\" = 200 ]; then echo \"GET /health answers 200\"; ok=1; break; fi; fi; "
        + "sleep 2; done; "
        + $"[ \"$ok\" = 1 ] || {{ echo \"no healthy answer after a minute; last status: $(docker inspect -f '{{{{.State.Status}}}}' orch-{app} 2>/dev/null || echo none)\"; docker logs --tail 5 orch-{app} 2>&1 | tail -5; exit 1; }}";

    private Task<ShellResult> OnMachineAsync(string machine, string script, TimeSpan timeout, CancellationToken cancellationToken) =>
        shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, Prelude + script], timeout, cancellationToken);

    // The name of the container is the repository's, with only what a container name takes; the port is stable for a repository, from the base port
    internal static string AppName(string repository) => NotAName().Replace(repository, "-").ToLowerInvariant();

    internal int PortOf(string repository) => DeploymentPort.Of(repository, options.DeployPortBase);

    private static MissionHandOver Failed(string commit, List<string> evidence, string claim, string previous) =>
        new(claim, $"{claim} Previous version: {previous}. Nothing was rolled back.", evidence, [claim], Complex: false, ComplexReason: null, commit);

    private static MissionHandOver Refused(ClaimedMission mission, string reason) =>
        new($"Nothing was deployed for {mission.Repository}: {reason}", reason, [], [reason], Complex: true, reason, Commit: null);

    private static void Keep(List<string> evidence, string step, ShellResult result) =>
        evidence.Add($"{step}:exit={result.ExitCode}:sha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output)))}");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Tail(string output) => string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RepositoryShape();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex NotAName();

    [GeneratedRegex(@"^PREVIOUS (.+)$", RegexOptions.Multiline)]
    private static partial Regex PreviousLine();
}
