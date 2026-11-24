using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class ImagePlacesTests
{
    private static CodeLocation Place(int line, string image, string repository = "o/r", string? managed = null) =>
        new(repository, "docker-compose.yml", line, VersionForm.ImageLine, ImageReference.Parse(image)!, managed);

    [Fact]
    public void The_lead_is_told_the_file_the_line_and_the_reference_as_written()
    {
        PlanEndpoints.ImagePlaces([Place(3, "nginx:1.14"), Place(9, "redis")], "o/r").Should().Equal("image written in docker-compose.yml line 3 as nginx:1.14", "image written in docker-compose.yml line 9 as redis");
    }

    [Fact]
    public void Only_the_places_of_the_repository_of_the_problem_that_no_automation_rewrites_are_told()
    {
        PlanEndpoints.ImagePlaces([Place(3, "nginx:1.14", "O/R"), Place(4, "nginx:1.14", "o/infra"), Place(5, "nginx:1.14", managed: "flux")], "o/r").Should().Equal("image written in docker-compose.yml line 3 as nginx:1.14");
    }

    [Fact]
    public void Only_the_first_few_places_are_told_in_the_order_they_were_read()
    {
        var told = PlanEndpoints.ImagePlaces([.. Enumerable.Range(1, 30).Select(line => Place(line, "nginx:1"))], "o/r");

        told.Should().HaveCount(10);
        told[0].Should().Contain("line 1 ");
        told[^1].Should().Contain("line 10 ");
    }
}
