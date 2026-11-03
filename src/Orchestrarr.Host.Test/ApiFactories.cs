using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Host.Test;

/// <summary>The host reading the sample file: the same data as the demonstration.</summary>
public sealed class SampleApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
