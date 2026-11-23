using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>The fake source of advisories of the test bed: a JSON file read at each call, that says nothing when it cannot be read.</summary>
public sealed class FileDependencyFindingsTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"findings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task The_findings_of_a_repository_are_read_at_each_call()
    {
        await File.WriteAllTextAsync(_path, """{"example/shop":[{"kind":"advisory","package":"lodash","installed":"4.17.4","fixedIn":"4.17.21","severity":"high","summary":"prototype pollution"}]}""", Ct);
        var source = new FileDependencyFindings(_path);

        var first = await source.ReadAsync("example/shop", Ct);
        await File.WriteAllTextAsync(_path, "{}", Ct);
        var second = await source.ReadAsync("example/shop", Ct);

        first.Should().ContainSingle().Which.Line.Should().Contain("advisory on lodash").And.Contain("fixed in 4.17.21");
        second.Should().BeEmpty("the file is read again, a plant is seen without a restart");
    }

    [Fact]
    public async Task Another_repository_a_missing_file_and_a_broken_file_say_nothing()
    {
        var source = new FileDependencyFindings(_path);
        (await source.ReadAsync("example/shop", Ct)).Should().BeEmpty();

        await File.WriteAllTextAsync(_path, """{"example/other":[{"kind":"advisory","package":"x"}]}""", Ct);
        (await source.ReadAsync("example/shop", Ct)).Should().BeEmpty();

        await File.WriteAllTextAsync(_path, "not json", Ct);
        (await source.ReadAsync("example/shop", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_finding_with_no_package_is_dropped_and_the_list_is_bounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 80).Select(index => $$"""{"kind":"advisory","package":"p{{index}}"}"""));
        await File.WriteAllTextAsync(_path, $$"""{"example/shop":[{"kind":"advisory","package":""},{{many}}]}""", Ct);

        var found = await new FileDependencyFindings(_path).ReadAsync("example/shop", Ct);

        found.Should().HaveCount(50).And.OnlyContain(finding => finding.Package.Length > 0);
    }
}
