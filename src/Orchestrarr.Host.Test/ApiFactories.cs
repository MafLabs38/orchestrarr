using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Claims;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Host.Test;

/// <summary>The host reading the sample file: the same data as the demonstration.</summary>
public sealed class SampleApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
        builder.ConfigureTestServices(services => services.AddTestSignIn());
    }
}

/// <summary>The sample fleet but no forge: not in Development, whose settings also read sample pull requests.</summary>
public sealed class NoForgeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "Memory");
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
        builder.ConfigureTestServices(services => services.AddTestSignIn());
    }
}

/// <summary>The host with no source configured: not in Development, whose settings read the sample file.</summary>
public sealed class UnconfiguredApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "Memory");
        builder.ConfigureTestServices(services => services.AddTestSignIn());
    }
}

/// <summary>The host whose source is down, with a message that must never reach a client.</summary>
public sealed class FailingApiFactory : WebApplicationFactory<Program>
{
    public const string SecretMessage = "Connection refused (internal-portarr.example:8484)";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddTestSignIn();
            services.RemoveAll<IFleetSource>();
            services.AddSingleton<IFleetSource>(new DownSource());
        });
    }

    private sealed class DownSource : IFleetSource
    {
        public string Kind => FleetSourceKinds.Portarr;

        public bool Configured => true;

        public Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken) => throw new HttpRequestException(SecretMessage);
    }
}


/// <summary>
/// Signs a test client in without the cookie: an Operator by default, another role or another name by header, or nobody (<c>anonymous</c>).
/// It also adds the header that a write from a person must carry, unless the test says it wants the real check. The sign-in itself, with its
/// cookie, is tested through the real routes in <c>AccountsApiTests</c>.
/// </summary>
internal sealed class TestSignInFilter : IStartupFilter
{
    public const string RoleHeader = "X-Test-Role";
    public const string UserHeader = "X-Test-User";
    public const string RealCsrfHeader = "X-Test-Real-Csrf";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, pipeline) =>
        {
            var headers = context.Request.Headers;
            if (!headers.ContainsKey(RealCsrfHeader))
            {
                headers["X-Orchestrarr-Request"] = "test";
            }

            var role = headers.TryGetValue(RoleHeader, out var chosen) ? chosen.ToString() : Roles.Operator;
            if (role != "anonymous" && Roles.Find(role) is { } known)
            {
                var subject = headers.TryGetValue(UserHeader, out var user) ? user.ToString() : "test-operator";
                var identity = new ClaimsIdentity("orchestrarr.cookie", ClaimTypes.Name, "orchestrarr:role");
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
                identity.AddClaim(new Claim(ClaimTypes.Name, subject));
                identity.AddClaim(new Claim("orchestrarr:role", known.Name));
                foreach (var permission in known.Permissions)
                {
                    identity.AddClaim(new Claim("orchestrarr:permission", permission));
                }

                context.User = new ClaimsPrincipal(identity);
            }

            return pipeline(context);
        });
        next(app);
    };
}

internal static class TestSignIn
{
    public static IServiceCollection AddTestSignIn(this IServiceCollection services) => services.AddSingleton<IStartupFilter, TestSignInFilter>();
}

/// <summary>
/// The test server has no network: this gives each request the address of its client. By default the loopback (the maintainer's own
/// machine); a test that wants a client from the network says so with the <c>X-Test-Remote</c> header.
/// </summary>
internal sealed class RemoteAddressFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, pipeline) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(Header, out var remote) && System.Net.IPAddress.TryParse(remote, out var address)
                ? address
                : System.Net.IPAddress.Loopback;
            return pipeline(context);
        });
        next(app);
    };
}

public sealed class ForgeApiFactory : WebApplicationFactory<Program>
{
    public Func<Exception>? Failure { get; init; }

    /// <summary>Other settings of the host (the grants, the capacities, the listening addresses…).</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>The environment of the host; Development, the sandbox's, when not set.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Services that replace the host's own.</summary>
    public Action<IServiceCollection>? ServiceOverrides { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (EnvironmentName is not null)
        {
            builder.UseEnvironment(EnvironmentName);
        }
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
        // Nobody keeps what a test approves: the sandbox's memory, unless a test asks for a file
        builder.UseSetting("Storage:Provider", "Memory");
        builder.UseSetting("Storage:MemoryStartsEngaged", "false");
        // The control plane alone: there is no executor behind it to check a code (a test that wants the code says so in its settings)
        builder.UseSetting("Tasks:RequireSecondFactor", "false");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPullRequestSource>();
            services.AddSingleton<IPullRequestSource>(new FakeForge(Failure));
            ServiceOverrides?.Invoke(services);
            services.AddSingleton<IStartupFilter, RemoteAddressFilter>();
            services.AddTestSignIn();
        });
    }

    private sealed class FakeForge(Func<Exception>? failure) : IPullRequestSource
    {
        public string Kind => "github";

        public bool Configured => true;

        public Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                return Task.FromException<PullRequestList>(failure());
            }

            var pullRequest = new PullRequestInfo(
                7, "chore(deps): Bump lodash from 4.17.4 to 5.0.0", new Uri($"https://forge.example/{repositoryFullName}/pull/7"), "dependabot[bot]", "bot/lodash", false,
                ChecksState.Passing, MergeState.Clean, ["dependencies"], [new DependencyChange("lodash", "4.17.4", "5.0.0", UpdateKind.Major)],
                new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), new string('c', 40));
            return Task.FromResult(new PullRequestList([pullRequest], Truncated: false));
        }
    }
}

/// <summary>The sample host whose source of findings counts how often it is read.</summary>
public sealed class CountingApiFactory : WebApplicationFactory<Program>
{
    public CountingSource Source { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "Memory");
        builder.ConfigureTestServices(services =>
        {
            services.AddTestSignIn();
            services.RemoveAll<IFleetSource>();
            services.AddSingleton<IFleetSource>(Source);
        });
    }

    public sealed class CountingSource : IFleetSource
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public string Kind => FleetSourceKinds.Portarr;

        public bool Configured => true;

        public Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new FleetSnapshot(DateTimeOffset.UtcNow, [], []));
        }
    }
}
