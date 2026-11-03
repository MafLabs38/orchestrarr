using System.Text.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr.Test;

public sealed class PortarrFleetMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static FleetSnapshot Map(string json)
    {
        var report = JsonSerializer.Deserialize<PortarrMaintenance>(json, PortarrFleetMapper.JsonOptions)!;
        return PortarrFleetMapper.Map(report, Now);
    }

    private static RepositoryState Repository(string body) => Map($$"""{ "repositories": [ { "fullName": "example/app", {{body}} } ] }""").Repositories.Single();

    private static ImageState Image(string body) => Map($$"""{ "images": [ { "reference": "app:1", {{body}} } ] }""").Images.Single();

    [Fact]
    public void The_alerts_of_each_scan_are_mapped_with_their_state()
    {
        var repository = Repository("""
            "dependabot": { "state": "on", "critical": 1, "high": 2, "medium": 3, "low": 4, "other": 9, "total": 19 },
            "codeScanning": { "state": "on", "high": 1, "total": 1 },
            "secretScanning": { "state": "on", "total": 2 }
            """);

        repository.DependencyAlerts.Should().Be(AlertScan.Of(new AlertCounts(1, 2, 3, 4)));
        repository.CodeScanningAlerts.Should().Be(AlertScan.Of(new AlertCounts(0, 1, 0, 0)));
        repository.SecretScanningAlerts.Should().Be(2);
    }

    [Theory]
    [InlineData("on", AlertScanState.On)]
    [InlineData("off", AlertScanState.Off)]
    [InlineData("unknown", AlertScanState.Unknown)]
    [InlineData("something-new", AlertScanState.Unknown)]
    public void The_state_of_a_scan_is_mapped_and_an_unknown_word_is_unknown(string state, AlertScanState expected)
    {
        Repository("\"dependabot\": { \"state\": \"" + state + "\" }").DependencyAlerts.State.Should().Be(expected);
    }

    [Fact]
    public void A_missing_scan_is_unknown_not_clean()
    {
        Repository("\"archived\": false").DependencyAlerts.Should().Be(new AlertScan(AlertScanState.Unknown, AlertCounts.None));
    }

    [Fact]
    public void A_bare_repository_has_nothing_to_report_and_the_flags_are_mapped()
    {
        var repository = Repository("\"archived\": true, \"fork\": true");

        repository.Archived.Should().BeTrue();
        repository.Fork.Should().BeTrue();
        repository.OpenDependencyPullRequests.Should().Be(0);
        repository.Ci.Should().Be(CiState.Unknown);
        repository.AppIds.Should().BeEmpty();
        repository.ImageReferences.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "status": "completed", "conclusion": "success", "failed": false }""", CiState.Passing)]
    [InlineData("""{ "status": "completed", "conclusion": "failure", "failed": true }""", CiState.Failing)]
    [InlineData("""{ "status": "in_progress", "failed": false }""", CiState.Running)]
    [InlineData("""{ "status": "completed", "conclusion": "cancelled", "failed": false }""", CiState.Unknown)]
    [InlineData("""{ "failed": false }""", CiState.Unknown)]
    public void The_last_ci_run_is_mapped(string run, CiState expected)
    {
        Repository("\"ci\": " + run).Ci.Should().Be(expected);
    }

    [Theory]
    [InlineData("up-to-date", ImageStatus.UpToDate)]
    [InlineData("behind", ImageStatus.Behind)]
    [InlineData("newer-build", ImageStatus.NewerBuild)]
    [InlineData("floating", ImageStatus.Floating)]
    [InlineData("pending", ImageStatus.Pending)]
    [InlineData("unknown", ImageStatus.Unknown)]
    [InlineData("something-new", ImageStatus.Unknown)]
    public void The_status_of_an_image_is_mapped(string status, ImageStatus expected)
    {
        Image("\"status\": \"" + status + "\"").Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("build", ImageChange.Build)]
    [InlineData("patch", ImageChange.Patch)]
    [InlineData("minor", ImageChange.Minor)]
    [InlineData("major", ImageChange.Major)]
    [InlineData("something-new", ImageChange.None)]
    public void The_kind_of_change_of_an_image_is_mapped(string change, ImageChange expected)
    {
        Image("\"change\": \"" + change + "\"").Change.Should().Be(expected);
    }

    [Fact]
    public void An_image_carries_its_versions_its_repository_and_where_it_runs()
    {
        var image = Image("""
            "current": "2.0.0", "latest": "3.0.1", "repositoryFullName": "example/app",
            "users": [ { "environmentId": "e1", "environmentName": "Server 1", "name": "app", "namespace": "ns" } ]
            """);

        image.Current.Should().Be("2.0.0");
        image.Latest.Should().Be("3.0.1");
        image.RepositoryFullName.Should().Be("example/app");
        image.Placements.Should().Equal(new Placement("e1", "Server 1", "ns", "app"));
    }

    [Fact]
    public void An_image_with_no_users_runs_nowhere_known()
    {
        Image("\"status\": \"behind\"").Placements.Should().BeEmpty();
    }
}
