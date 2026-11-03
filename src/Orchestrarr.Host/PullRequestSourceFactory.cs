using Orchestrarr.Core.Forge;
using Orchestrarr.Providers.GitHub;

namespace Orchestrarr.Host;

/// <summary>
/// Chooses the forge from the configuration: GitHub when <c>GitHub:Token</c> is set, else the sample file of
/// <c>Forge:SamplePullRequestsFile</c> (relative to the content root), else a source that reads nothing.
/// </summary>
internal static class PullRequestSourceFactory
{
    public const string SampleFileKey = "Forge:SamplePullRequestsFile";

    public static IPullRequestSource Create(IConfiguration configuration, string contentRoot, TimeProvider time)
    {
        var options = configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new GitHubOptions();
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return GitHubPullRequestSource.Create(options, time);
        }

        var sample = configuration[SampleFileKey];
        return string.IsNullOrWhiteSpace(sample) ? new NoPullRequestSource() : new SamplePullRequestSource(Path.GetFullPath(sample, contentRoot));
    }
}
