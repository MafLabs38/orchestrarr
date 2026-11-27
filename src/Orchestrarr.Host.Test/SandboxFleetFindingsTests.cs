using Orchestrarr.Core.Fleet;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class SandboxFleetFindingsTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class Fleet(params RepositoryState[] repositories) : IFleetSource
    {
        public string Kind => "test";

        public bool Configured => true;

        public Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetSnapshot(DateTimeOffset.UnixEpoch, repositories, []));
    }

    private static RepositoryState Repository(string name, int alerts) =>
        new(name, null, false, false, AlertScan.Of(new AlertCounts(0, alerts, 0, 0)), AlertScan.Clean, 0, 0, CiState.Passing, [], []);

    [Fact]
    public async Task A_repository_that_the_lab_planted_findings_on_has_at_least_that_many_alerts_and_nothing_real_is_lowered()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, """{ "o/stack": [ { "kind": "advisory", "package": "postgres (container image)" } ], "o/shop": [ { "kind": "advisory", "package": "lodash" } ] }""", Ct);
        try
        {
            var snapshot = await new SandboxFleetFindings(new Fleet(Repository("o/stack", 0), Repository("o/shop", 3), Repository("o/other", 0)), file).ReadAsync(Ct);

            snapshot.Repositories.Single(repository => repository.FullName == "o/stack").DependencyAlerts.Counts.Total.Should().Be(1);
            snapshot.Repositories.Single(repository => repository.FullName == "o/shop").DependencyAlerts.Counts.Total.Should().Be(3, "the real source said more");
            snapshot.Repositories.Single(repository => repository.FullName == "o/other").DependencyAlerts.Counts.Total.Should().Be(0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_missing_file_plants_nothing()
    {
        var snapshot = await new SandboxFleetFindings(new Fleet(Repository("o/stack", 0)), "/nonexistent/findings.json").ReadAsync(Ct);

        snapshot.Repositories.Single().DependencyAlerts.Counts.Total.Should().Be(0);
    }
}
