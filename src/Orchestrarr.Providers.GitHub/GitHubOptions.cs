namespace Orchestrarr.Providers.GitHub;

/// <summary>Settings of the GitHub provider (<c>GitHub</c> section). Without a <see cref="Token"/>, the forge is not connected.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Address of the REST API. Redirects are not followed, so the token only goes there.</summary>
    public Uri ApiUrl { get; set; } = new("https://api.github.com/");

    /// <summary>Address of the web site, used to build the links to pull requests (never taken from the API's answer).</summary>
    public Uri WebUrl { get; set; } = new("https://github.com/");

    /// <summary>
    /// A token that can read pull requests and checks (read-only: <c>pull_requests: read</c> and <c>checks: read</c> on the repositories).
    /// Never put it in a file that is committed: use <c>GitHub__Token</c> or <c>dotnet user-secrets</c>.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>The logins whose pull requests are dependency updates.</summary>
    public string[] BotLogins { get; set; } = ["dependabot[bot]", "renovate[bot]"];

    /// <summary>At most this many pull requests are read per repository: each costs requests, and the rate limit is shared.</summary>
    public int MaxPullRequests { get; set; } = 30;

    /// <summary>How long what was read for a repository is kept, to spare the rate limit.</summary>
    public int CacheSeconds { get; set; } = 120;

    public int TimeoutSeconds { get; set; } = 20;

    public long MaxResponseBytes { get; set; } = 5 * 1024 * 1024;
}
