using Microsoft.Extensions.Configuration;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Host.Test;

public sealed class FleetSourceFactoryTests
{
    private static string KindFor(Dictionary<string, string?> settings, string contentRoot = "/srv")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return FleetSourceFactory.Create(configuration, contentRoot, TimeProvider.System).Kind;
    }

    [Theory]
    [InlineData("http://portarr.example", null, FleetSourceKinds.Portarr)]
    [InlineData(null, "sample.json", FleetSourceKinds.Sample)]
    [InlineData("http://portarr.example", "sample.json", FleetSourceKinds.Portarr)]
    [InlineData(null, null, FleetSourceKinds.None)]
    [InlineData(null, "  ", FleetSourceKinds.None)]
    public void The_source_follows_the_configuration_and_a_portarr_wins_over_a_sample(string? baseUrl, string? sampleFile, string expected)
    {
        KindFor(new Dictionary<string, string?> { ["Portarr:BaseUrl"] = baseUrl, [FleetSourceFactory.SampleFileKey] = sampleFile }).Should().Be(expected);
    }

    [Fact]
    public async Task A_relative_sample_file_is_resolved_against_the_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orchestrarr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        await File.WriteAllTextAsync(Path.Combine(root, "data", "fleet.json"), """{ "repositories": [ { "fullName": "example/app" } ] }""", TestContext.Current.CancellationToken);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [FleetSourceFactory.SampleFileKey] = "data/fleet.json" }).Build();

            var fleet = await FleetSourceFactory.Create(configuration, root, TimeProvider.System).ReadAsync(TestContext.Current.CancellationToken);

            fleet.Repositories.Should().ContainSingle().Which.FullName.Should().Be("example/app");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
