using System.Text.RegularExpressions;

namespace Orchestrarr.Host.Approvals;

/// <summary>
/// What keeps the sandbox operator (no sign-in) in the sandbox. It is refused unless the environment is Development <em>and</em> the host
/// listens only on the loopback: an image that is started as Development to get detailed errors, and listens on every interface, does not
/// start. The two routes that record something also answer only to a direct address (see <see cref="IsDirectHost"/>).
/// </summary>
internal static partial class SandboxGuard
{
    public static void Check(ApprovalsOptions options, IHostEnvironment environment, IConfiguration configuration)
    {
        if (options.Mode != ApprovalsMode.Development)
        {
            return;
        }
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Approvals:Mode=Development is only allowed in the Development environment.");
        }

        // No address configured: Kestrel listens on localhost only. Otherwise every address must be the loopback, not a wildcard.
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? configuration["DOTNET_URLS"];
        foreach (var url in (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var address = url.Replace("+", "wildcard", StringComparison.Ordinal).Replace("*", "wildcard", StringComparison.Ordinal);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || !parsed.IsLoopback)
            {
                throw new InvalidOperationException("Approvals:Mode=Development is only allowed when the host listens on the loopback address.");
            }
        }
    }

    /// <summary>
    /// A request made to an IP address or to <c>localhost</c>. A page that is reached by a name (a DNS rebinding to the loopback) is not
    /// direct: the name is the attacker's.
    /// </summary>
    public static bool IsDirectHost(HttpContext context)
    {
        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _);
    }

    public static bool IsRepositoryName(string value) => RepositoryName().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.None, 100)]
    private static partial Regex RepositoryName();
}
