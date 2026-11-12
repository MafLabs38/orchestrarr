namespace Orchestrarr.Providers.GitHub;

/// <summary>Settings of the GitHub provider (<c>GitHub</c> section). Without a <see cref="Token"/>, the forge is not connected.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public static readonly string[] DefaultBotLogins = ["dependabot[bot]", "renovate[bot]"];

    /// <summary>Address of the REST API. It must be https (http only to the local machine): the token is sent in a header. Redirects are not followed.</summary>
    public Uri ApiUrl { get; set; } = new("https://api.github.com/");

    /// <summary>Address of the web site, used to build the links to pull requests (never taken from the API's answer). Must be https.</summary>
    public Uri WebUrl { get; set; } = new("https://github.com/");

    /// <summary>
    /// A token that can only read: <c>Pull requests: read</c>, <c>Checks: read</c> and <c>Commit statuses: read</c> on the repositories.
    /// Never put it in a file that is committed: use <c>GitHub__Token</c> or <c>dotnet user-secrets</c>.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// The logins whose pull requests are dependency updates. Null: Dependabot and Renovate. A list given in the configuration replaces
    /// this default (a configuration array would otherwise be added to a default one).
    /// </summary>
    public string[]? BotLogins { get; set; }

    /// <summary>At most this many pull requests are read per repository: each costs requests, and the rate limit is shared.</summary>
    public int MaxPullRequests { get; set; } = 30;

    /// <summary>At most this many pages of 100 pull requests are read per repository.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>How long what was read for a repository is kept, to spare the rate limit, unless a refresh is asked.</summary>
    public int CacheSeconds { get; set; } = 300;

    public int TimeoutSeconds { get; set; } = 20;

    public long MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

    public static readonly string[] DefaultChecksThatNeedSecrets = ["sonarqube", "ai-review*"];

    /// <summary>
    /// The names of the checks (a <c>*</c> at the end matches any ending) that cannot run for a bot's pull request, because the forge does not give secrets to a bot:
    /// a pull request whose only failing checks are these is "not run", not "failing". Null: SonarQube and the review agents. A list replaces the default.
    /// </summary>
    public string[]? ChecksThatNeedSecrets { get; set; }

    internal IReadOnlyList<string> NeedSecrets => ChecksThatNeedSecrets ?? DefaultChecksThatNeedSecrets;

    internal IReadOnlyList<string> Bots => BotLogins is { Length: > 0 } ? BotLogins : DefaultBotLogins;
}
