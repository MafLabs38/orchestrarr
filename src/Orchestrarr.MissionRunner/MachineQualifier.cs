using System.Globalization;
using Orchestrarr.Core.Machines;

namespace Orchestrarr.MissionRunner;

/// <summary>
/// The qualification of a lab machine (ADR 0047): the checks that make it Idle, made by the runner that works on it. A staging machine is first put back to its reference by the lab tool, which isolates it before it starts.
/// Then, **from inside the machine**: it is reachable, it reaches **none** of the addresses it must not reach (the canaries: the control plane, the other lab machine, the hypervisor), and it reaches what the work needs
/// (the registries, the forge). A machine that reaches a canary, or that cannot be checked, is not qualified.
/// </summary>
public sealed class MachineQualifier(IShell shell, MissionRunnerOptions options)
{
    private static readonly TimeSpan ResetTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The checks. <paramref name="restore"/>: whether the machine is put back to its reference first (a staging machine; a release machine keeps what it runs).</summary>
    public async Task<IReadOnlyList<MachineCheck>> QualifyAsync(string machine, bool restore, CancellationToken cancellationToken)
    {
        var checks = new List<MachineCheck>();
        if (restore)
        {
            var restored = await RestoreAsync(machine, cancellationToken);
            checks.Add(restored);
            if (!restored.Passed)
            {
                // A machine that could not be restored is not probed: what it holds is unknown
                return checks;
            }
        }
        var probe = await ProbeAsync(machine, cancellationToken);
        checks.AddRange(probe);
        return checks;
    }

    private async Task<MachineCheck> RestoreAsync(string machine, CancellationToken cancellationToken)
    {
        if (options.LabTool is not { Length: > 0 } tool)
        {
            return new MachineCheck("restored", false, "the runner has no tool to restore the machine (MissionRunner:LabTool)");
        }
        try
        {
            var reset = await shell.RunAsync(tool, ["reset", machine], ResetTimeout, cancellationToken);
            return new MachineCheck("restored", reset.ExitCode == 0, reset.ExitCode == 0 ? "back on its reference" : $"the restore failed: {Tail(reset.Output)}");
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new MachineCheck("restored", false, $"the restore could not be run: {failure.Message}");
        }
    }

    private async Task<IReadOnlyList<MachineCheck>> ProbeAsync(string machine, CancellationToken cancellationToken)
    {
        var canaries = options.IsolationCanaries;
        var routes = options.RequiredRoutes;
        // Every address was checked to be host:port in plain characters when the settings were read; nothing else goes into the script
        var script = "for t in " + string.Join(' ', canaries.Select(address => "C:" + address).Concat(routes.Select(address => "R:" + address))) + "; do kind=${t%%:*}; a=${t#*:}; h=${a%%:*}; p=${a##*:}; "
            + "if timeout 3 bash -c \"</dev/tcp/$h/$p\" 2>/dev/null; then echo \"OPEN $kind $a\"; else echo \"CLOSED $kind $a\"; fi; done";
        ShellResult result;
        try
        {
            result = await shell.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", machine, script], ProbeTimeout, cancellationToken);
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [new MachineCheck("reachable", false, $"the machine could not be reached: {failure.Message}")];
        }
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = lines.Select(line => line.Split(' ', 3)).Where(parts => parts.Length == 3 && parts[0] is "OPEN" or "CLOSED").ToList();
        if (result.ExitCode != 0 || result.TimedOut || seen.Count != canaries.Length + routes.Length)
        {
            return [new MachineCheck("reachable", false, result.TimedOut ? "the probe timed out" : $"the probe did not answer ({Tail(result.Output)})")];
        }
        var checks = new List<MachineCheck> { new("reachable", true, "answers over ssh") };
        var open = seen.Where(parts => parts[0] == "OPEN" && parts[1] == "C").Select(parts => parts[2]).ToList();
        checks.Add(IsolationCheck(canaries.Length, open));
        var missing = seen.Where(parts => parts[0] == "CLOSED" && parts[1] == "R").Select(parts => parts[2]).ToList();
        checks.Add(new MachineCheck("routes", missing.Count == 0, missing.Count == 0 ? "the registries and the forge answer" : $"it does not reach {string.Join(", ", missing)}"));
        return checks;
    }

    private static MachineCheck IsolationCheck(int canaries, IReadOnlyList<string> open)
    {
        if (canaries == 0)
        {
            return new MachineCheck("isolation", false, "no canary is configured (MissionRunner:IsolationCanaries): isolation cannot be verified");
        }
        return open.Count == 0
            ? new MachineCheck("isolation", true, string.Create(CultureInfo.InvariantCulture, $"{canaries} address(es) of the network are out of reach"))
            : new MachineCheck("isolation", false, $"it reaches {string.Join(", ", open)}");
    }

    private static string Tail(string output) => string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}
