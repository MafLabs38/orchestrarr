using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class ImagePlacesTests
{
    private static CodeLocation Place(int line, string image) => new("o/r", "docker-compose.yml", line, VersionForm.ImageLine, ImageReference.Parse(image)!);

    [Fact]
    public void The_lead_is_told_the_file_the_line_and_the_reference_as_written()
    {
        PlanEndpoints.ImagePlaces([Place(3, "nginx:1.14"), Place(9, "redis")]).Should().Equal("image written in docker-compose.yml line 3 as nginx:1.14", "image written in docker-compose.yml line 9 as redis");
    }

    [Fact]
    public void Only_a_few_places_are_told()
    {
        PlanEndpoints.ImagePlaces([.. Enumerable.Range(1, 30).Select(line => Place(line, "nginx:1"))]).Should().HaveCount(10);
    }
}
