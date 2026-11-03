using Orchestrarr.Core.Fleet;
using Orchestrarr.Providers.Portarr;

namespace Orchestrarr.Host;

/// <summary>
/// Chooses the source of the fleet from the configuration: a Portarr when <c>Portarr:BaseUrl</c> is set, else the sample file of
/// <c>Fleet:SampleFile</c> (relative to the content root), else a source that reads nothing. The host composes the sources: a provider only
/// knows itself.
/// </summary>
internal static class FleetSourceFactory
{
    public const string SampleFileKey = "Fleet:SampleFile";

    public static IFleetSource Create(IConfiguration configuration, string contentRoot, TimeProvider time)
    {
        var portarr = configuration.GetSection(PortarrOptions.SectionName).Get<PortarrOptions>() ?? new PortarrOptions();
        if (portarr.BaseUrl is not null)
        {
            return PortarrFleetSource.Create(portarr, time);
        }

        var sample = configuration[SampleFileKey];
        return string.IsNullOrWhiteSpace(sample)
            ? new EmptyFleetSource(time)
            : new SampleFleetSource(Path.GetFullPath(sample, contentRoot), time);
    }
}
