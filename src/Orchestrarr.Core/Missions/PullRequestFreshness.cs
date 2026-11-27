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

    /// <summary>Its bot closed it: a bot closes its own pull request when another change made it useless (the base already holds that version or a newer one). Nothing is left to do: the operation is done by another one.</summary>
    Superseded,
}

/// <summary>The verdict, with the head to follow (Followed, and NeedsRebase when the bot made a new head that is not yet on the base as it is now) and the reason in words (NeedsRebase, Left).</summary>
public sealed record FreshnessVerdict(Freshness Kind, string? Head, string? Reason, string? Author = null);

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
        var bot = isBot(facts.Author);
        if (facts.Closed)
        {
            return bot
                ? new FreshnessVerdict(Freshness.Superseded, null, "its bot closed the pull request: another change already brought what it carried")
                : new FreshnessVerdict(Freshness.Left, null, "the pull request is closed");
        }
        var moved = !string.Equals(facts.HeadSha, entry.HeadSha, StringComparison.Ordinal);
        if (moved && !bot)
        {
            return new FreshnessVerdict(Freshness.Left, null, "the pull request moved since the maintainer ticked it and it is not a bot's: it is ticked again on the commit it is at");
        }
        // The author of a pull request stays the bot whoever pushes to its branch afterwards: a new head is the bot's only when every commit of the pull request is the bot's own, as the forge links them to accounts
        if (moved && !IsTheBotsOwn(facts))
        {
            return new FreshnessVerdict(Freshness.Left, null, "someone other than its bot pushed to the bot's branch: it is ticked again on the commit it is at");
        }
        // The merge state is worked out by the forge lazily, a while after the base moved: how far behind the base the head is, is known at once, and is the one that says that a merge has just moved the base. A new head that
        // the bot made is followed only once it is on the base as it is now: a bot that made it again on a base that moved since is asked again (the head is followed meanwhile)
        var behind = facts.BehindBy is > 0 || facts.Merge == MergeState.Behind;
        if ((behind || facts.Merge == MergeState.Conflicting) && bot)
        {
            return new FreshnessVerdict(Freshness.NeedsRebase, moved ? facts.HeadSha : null, facts.Merge == MergeState.Conflicting ? "it conflicts with its base branch" : "it is behind its base branch", facts.Author);
        }
        return moved ? new FreshnessVerdict(Freshness.Followed, facts.HeadSha, null) : new FreshnessVerdict(Freshness.Ready, null, null);
    }

    /// <summary>Whether every commit of the pull request is its author's (the bot's), as the forge links commits to accounts: a list that could not be read, or that is empty, is not.</summary>
    public static bool IsTheBotsOwn(PullRequestFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.CommitAuthors is { Count: > 0 } authors && authors.All(author => string.Equals(author, facts.Author, StringComparison.OrdinalIgnoreCase));
    }
}
