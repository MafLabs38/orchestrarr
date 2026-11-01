using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

public static class PortarrServiceCollectionExtensions
{
    /// <summary>
    /// Registers the source of the fleet: a Portarr when <c>Portarr:BaseUrl</c> is set, else the sample file of <c>Portarr:SampleFile</c>
    /// (relative to the content root), else a source that reads nothing.
    /// </summary>
    public static IServiceCollection AddPortarrFleetSource(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.Configure<PortarrOptions>(configuration.GetSection(PortarrOptions.SectionName));

        services.AddHttpClient(PortarrFleetSource.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<PortarrOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(20);
            if (options.BaseUrl is null)
            {
                return;
            }

            client.BaseAddress = options.BaseUrl;
            var key = Environment.GetEnvironmentVariable(options.ApiKeyEnv);
            if (!string.IsNullOrEmpty(key))
            {
                client.DefaultRequestHeaders.Add(PortarrFleetSource.ApiKeyHeader, key);
            }
        });

        services.AddSingleton<IFleetSource>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PortarrOptions>>().Value;
            var time = provider.GetRequiredService<TimeProvider>();
            if (options.BaseUrl is not null)
            {
                return new PortarrFleetSource(provider.GetRequiredService<IHttpClientFactory>().CreateClient(PortarrFleetSource.HttpClientName), time);
            }

            return string.IsNullOrWhiteSpace(options.SampleFile)
                ? new EmptyFleetSource(time)
                : new SampleFleetSource(Path.GetFullPath(options.SampleFile, contentRoot), time);
        });

        return services;
    }
}
