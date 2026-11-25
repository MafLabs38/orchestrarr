using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Missions;

/// <summary>What a pull request is, when its turn comes, compared with what the maintainer ticked (ADR 0055).</summary>
public enum Freshness
{
    /// <summary>As it was ticked, and nothing in its way: it starts.</summary>
    Ready,

    /// <summary>The bot made it again on a newer base since the tick (a new head), and it is the bot's: the work follows it, on the new head.</summary>
    Followed,

    /// <summary>Behind its base or in conflict with it, and made by a bot: the bot is asked to rebase it, and the lot goes on with something else meanwhile.</summary>
    NeedsRebase,

    /// <summary>It moved and is not a bot's, or it is merged: it cannot be done on what was ticked. The maintainer ticks it again.</summary>
    Left,
}

/// <summary>The verdict, with the head to follow (Followed) or the reason in words (NeedsRebase, Left).</summary>
public sealed record FreshnessVerdict(Freshness Kind, string? Head, string? Reason);

/// <summary>
/// The decision of ADR 0055 on a pull request that waits for its turn. **Pure**: it reads what the forge said and the plan entry, and never writes. The author is the guard of the one case where the work follows a new head: only
/// a pull request that a configured bot made is followed (the staging proof, the pipeline and the controller still judge the new head), and anything else that moved is left, as ADR 0049 said.
/// </summary>
public static class PullRequestFreshness
{
    public static FreshnessVerdict Of(PlanEntry entry, PullRequestFacts? facts, Func<string?, bool> isBot)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(isBot);
        // A forge that does not answer is not a reason to hold the lot: the proof refuses what moved, as it always did
        if (entry.PullRequest is null || facts is null)
        {
            return new FreshnessVerdict(Freshness.Ready, null, null);
        }
        if (facts.Merged)
        {
            return new FreshnessVerdict(Freshness.Left, null, "the pull request is already merged");
        }
        // A bot closes its own pull request when another change made it useless (the dependency was raised some other way): there is nothing left to do on it
        if (facts.Closed)
        {
            return new FreshnessVerdict(Freshness.Left, null, "the pull request is closed (its bot closes a pull request that another change made useless)");
        }
        var bot = isBot(facts.Author);
        if (!string.Equals(facts.HeadSha, entry.HeadSha, StringComparison.Ordinal))
        {
            return bot
                ? new FreshnessVerdict(Freshness.Followed, facts.HeadSha, null)
                : new FreshnessVerdict(Freshness.Left, null, "the pull request moved since the maintainer ticked it and it is not a bot's: it is ticked again on the commit it is at");
        }
        // The merge state is worked out by the forge lazily, a while after the base moved: how far behind the base the head is, is known at once, and is the one that says that a merge has just moved the base
        var behind = facts.BehindBy is > 0 || facts.Merge == MergeState.Behind;
        if ((behind || facts.Merge == MergeState.Conflicting) && bot)
        {
            return new FreshnessVerdict(Freshness.NeedsRebase, null, facts.Merge == MergeState.Conflicting ? "it conflicts with its base branch" : "it is behind its base branch");
        }
        return new FreshnessVerdict(Freshness.Ready, null, null);
    }
}
