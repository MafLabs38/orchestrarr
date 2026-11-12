using Microsoft.Extensions.Configuration;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

/// <summary>The description file: YAML or JSON, one entry for the perimeter and the applications (ADR 0028).</summary>
public sealed class DescriptionFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("description").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_folder, name), content);
        return name;
    }

    private DescriptionContent Load(string key, string file) =>
        DescriptionFile.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = file }).Build(), _folder);

    private const string Yaml = """
        # the perimeter
        environments:
          - { id: k3s, name: "Cluster" }
        repositories:
          - repository: example/infra
            kind: kubernetes
            paths: ["apps/**/*.yaml"]   # only these
        platforms:
          - { id: k3s, kind: k3s, version: { repository: example/infra, path: apps/all.yml, key: k3s_version } }
        applications:
          - { id: n8n, kind: third-party, source: { image: n8nio/n8n }, environments: [k3s] }
          - id: shop
            kind: own
            source: example/shop
            update:
              promote: [patch, minor]
        """;

    [Fact]
    public void A_yaml_description_gives_the_applications_and_the_perimeter()
    {
        var content = Load(DescriptionFile.FileKey, Write("description.yaml", Yaml));

        content.Scope.Environments.Should().ContainSingle().Which.Id.Should().Be("k3s");
        content.Scope.Repositories.Should().ContainSingle().Which.Paths.Should().Equal("apps/**/*.yaml");
        content.Scope.Platforms.Should().ContainSingle().Which.Version!.Key.Should().Be("k3s_version");
        content.Applications.Find("n8n")!.Source.Should().Be("n8nio/n8n");
        content.Applications.Find("shop")!.PromotesAutomatically.Should().HaveCount(2);
        content.Applications.Version.Should().NotBeEmpty();
    }

    [Fact]
    public void A_json_description_and_the_old_name_of_the_key_still_read_and_have_no_perimeter()
    {
        var content = Load(DescriptionFile.LegacyFileKey, Write("applications.json", """{ "applications": [ { "id": "repository:example/shop", "kind": "own", "source": "example/shop" } ] }"""));

        content.Applications.Find("repository:example/shop").Should().NotBeNull();
        content.Scope.Repositories.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_set_describes_nothing_and_a_missing_or_wrong_file_stops_the_start()
    {
        DescriptionFile.Load(new ConfigurationBuilder().Build(), _folder).Scope.Repositories.Should().BeEmpty();
        var missing = () => Load(DescriptionFile.FileKey, "nowhere.yaml");
        var wrongYaml = () => Load(DescriptionFile.FileKey, Write("bad.yaml", "environments: [unclosed"));
        var wrongShape = () => Load(DescriptionFile.FileKey, Write("shape.yaml", "repositories:\n  - { repository: no-owner, kind: kubernetes, paths: [a] }\n"));

        missing.Should().Throw<InvalidOperationException>().WithMessage("*does not exist*");
        wrongYaml.Should().Throw<InvalidOperationException>().WithMessage("*cannot be read*");
        wrongShape.Should().Throw<InvalidOperationException>().WithMessage("*owner/name*");
    }

    [Fact]
    public void A_yaml_alias_and_a_file_that_is_too_big_are_refused()
    {
        var alias = () => Load(DescriptionFile.FileKey, Write("alias.yaml", "a: &x [1, 2]\nb: *x\n"));
        var big = () => Load(DescriptionFile.FileKey, Write("big.yaml", "# " + new string('x', 600 * 1024) + "\n"));

        alias.Should().Throw<InvalidOperationException>().WithMessage("*aliases*");
        big.Should().Throw<InvalidOperationException>().WithMessage("*not that big*");
    }

    [Fact]
    public void Numbers_and_booleans_keep_their_type_when_the_yaml_becomes_json()
    {
        var json = DescriptionFile.ToJson("a: 12\nb: true\nc: \"text\"\nd: 007\ne: [1, x]\n");

        json.Should().Be("""{"a":12,"b":true,"c":"text","d":"007","e":[1,"x"]}""");
    }
}
