using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class FirstDependencyFindingsTests
{
    private sealed class Fixed(string known, params DependencyFinding[] findings) : IDependencyFindings
    {
        public Task<IReadOnlyList<DependencyFinding>> ReadAsync(string repository, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DependencyFinding>>(repository == known ? findings : []);
    }

    private static readonly DependencyFinding Planted = new("advisory", "node", "20", null, "critical", "planted by the lab", null);
    private static readonly DependencyFinding OnTheForge = new("advisory", "urllib3", null, "2.5.0", "high", "an alert of the forge", null);

    [Fact]
    public async Task The_first_source_that_says_something_about_a_repository_is_the_one_read()
    {
        var sources = new FirstDependencyFindings([new Fixed("example/lab", Planted), new Fixed("example/lab", OnTheForge), new Fixed("example/forge", OnTheForge)]);

        (await sources.ReadAsync("example/lab", TestContext.Current.CancellationToken)).Should().Equal(Planted);
        (await sources.ReadAsync("example/forge", TestContext.Current.CancellationToken)).Should().Equal(OnTheForge);
        (await sources.ReadAsync("example/none", TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
