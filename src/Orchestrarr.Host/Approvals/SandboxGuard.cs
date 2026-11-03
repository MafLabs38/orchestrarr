using System.Net;

namespace Orchestrarr.Host.Approvals;

/// <summary>
/// What keeps the sandbox operator (no sign-in) in the sandbox. It is refused unless the environment is Development <em>and</em> the host
/// listens only on the loopback, on every way an address can be given, and the addresses that were really bound are checked once the host
/// started. The two routes that record something also answer only to a direct address (see <see cref="IsDirectHost"/>).
/// </summary>
internal static class SandboxGuard
{
    // Where an address can come from. All of them are read: only one takes effect, which is not known here, so every one must be safe.
    private static readonly string[] UrlKeys = ["urls", "ASPNETCORE_URLS", "DOTNET_URLS"];

    // A port alone listens on every interface
    private static readonly string[] PortKeys = ["http_ports", "https_ports", "HTTP_PORTS", "HTTPS_PORTS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS"];

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

        var addresses = UrlKeys.Select(key => configuration[key]).Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"]))
            .SelectMany(Split);
        if (PortKeys.Any(key => !string.IsNullOrWhiteSpace(configuration[key])) || !IsLoopbackOnly(addresses))
        {
            throw new InvalidOperationException("Approvals:Mode=Development is only allowed when the host listens on the loopback address.");
        }
    }

    /// <summary>Whether every address is the loopback: <c>localhost</c> or an IP address that is one. A wildcard, or any other name, is not.</summary>
    public static bool IsLoopbackOnly(IEnumerable<string> addresses) => addresses.All(IsLoopback);

    // Kestrel listens on every interface for a host name that is neither localhost nor an IP address, even one that System.Uri turns into
    // "localhost" ("loopback"): the host is read from the text as it is, never through System.Uri.
    private static bool IsLoopback(string address)
    {
        var host = HostOf(address);
        return host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)));
    }

    // The host of "http://host:port/path", "http://[::1]:port": null when it is not an address
    private static string? HostOf(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0)
        {
            return null;
        }
        var authority = address[(scheme + 3)..].Split('/')[0];
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return null; // user information is not an address: Kestrel would read the whole text as a name
        }
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || !IsPort(authority[(close + 1)..]))
            {
                return null;
            }
            // Brackets are for an IPv6 address only: Kestrel keeps them around a name or an IPv4 address, and then binds every interface
            var inner = authority[1..close];
            return IPAddress.TryParse(inner, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? inner : null;
        }
        var colons = authority.Count(character => character == ':');
        return colons switch
        {
            0 => authority,
            1 when IsPort(authority[authority.IndexOf(':', StringComparison.Ordinal)..]) => authority[..authority.IndexOf(':', StringComparison.Ordinal)],
            _ => null,
        };
    }

    // What follows a host: nothing, or a colon and a number
    private static bool IsPort(string text) => text.Length == 0 || (text[0] == ':' && text.Length > 1 && text.Skip(1).All(char.IsAsciiDigit));

    private static IEnumerable<string> Split(string? urls) =>
        (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Stops the host once it has started if it listens beyond the loopback, whatever the configuration said: the guard of the
    /// configuration reads what is set, this reads what was bound.
    /// </summary>
    public static void StopIfListeningBeyondLoopback(WebApplication app, ApprovalsOptions options)
    {
        if (options.Mode != ApprovalsMode.Development)
        {
            return;
        }
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var bound = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
            if (!IsLoopbackOnly(bound))
            {
                app.Logger.LogCritical("The sandbox operator is on and the host listens beyond the loopback ({Addresses}): stopping.", string.Join(", ", bound));
                app.Lifetime.StopApplication();
            }
        });
    }

    /// <summary>
    /// A request made to an IP address or to <c>localhost</c>. A page that is reached by a name (a DNS rebinding to the loopback) is not
    /// direct: the name is the attacker's.
    /// </summary>
    public static bool IsDirectHost(HttpContext context)
    {
        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host.Trim('[', ']'), out _);
    }

}
