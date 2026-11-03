using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Host.Test;

/// <summary>The host reading the sample file: the same data as the demonstration.</summary>
public sealed class SampleApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
    }
}

/// <summary>The sample fleet but no forge: not in Development, whose settings also read sample pull requests.</summary>
public sealed class NoForgeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
    }
}

/// <summary>The host with no source configured: not in Development, whose settings read the sample file.</summary>
public sealed class UnconfiguredApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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

/// <summary>The sample host with a forge that answers for every repository, or fails with a given exception.</summary>
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
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPullRequestSource>();
            services.AddSingleton<IPullRequestSource>(new FakeForge(Failure));
            ServiceOverrides?.Invoke(services);
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
        builder.ConfigureTestServices(services =>
        {
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
