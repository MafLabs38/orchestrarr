using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

/// <summary>Says once, at start, which sources of the missions are not wired yet, so that "no mandate is ever issued" is not mistaken for a bug: every act is refused until they exist (ADR 0031 point 7).</summary>
internal sealed class MissionsNotWiredNotice(IFactsSource facts, IApprovalSource approvals, IApprovalWithdrawals withdrawals, ILogger<MissionsNotWiredNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = new[]
        {
            facts is ConservativeFacts ? "the source of the facts (forge, Portarr)" : null,
            approvals is NoApprovals ? "the plan service that gives an approval" : null,
            withdrawals is UnreadableWithdrawals ? "the list of the withdrawn certificates" : null,
        }.OfType<string>().ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("The missions are inert: {Missing} not wired yet, so every act is refused and no real mandate can be issued", string.Join(", ", missing));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
