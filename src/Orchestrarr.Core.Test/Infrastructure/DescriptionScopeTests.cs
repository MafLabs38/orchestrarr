using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class DescriptionScopeTests
{
    private const string Good = """
        {
          "environments": [ { "id": "k3s", "name": "Cluster" }, { "id": "host-1" } ],
          "repositories": [
            { "repository": "example/infra", "kind": "kubernetes", "paths": ["apps/**/*.yaml"], "environment": "k3s" },
            { "repository": "example/cluster", "kind": "variables", "paths": ["ansible/group_vars/all.yml"] }
          ],
          "platforms": [
            { "id": "k3s", "kind": "k3s", "environment": "k3s",
              "version": { "repository": "example/cluster", "path": "ansible/group_vars/all.yml", "key": "k3s_version" } }
          ],
          "applications": []
        }
        """;

    [Fact]
    public void The_environments_the_platforms_and_the_repositories_are_read()
    {
        var scope = DescriptionScope.Parse(Good);

        scope.Environments.Select(environment => environment.Id).Should().Equal("k3s", "host-1");
        scope.Repositories.Should().HaveCount(2).And.ContainEquivalentOf(new RepositoryScope("example/infra", "kubernetes", ["apps/**/*.yaml"], "k3s"));
        var k3s = scope.Platforms.Single(platform => platform.Kind == "k3s");
        k3s.Environment.Should().Be("k3s");
        k3s.Version.Should().Be(new VersionPointer("example/cluster", "ansible/group_vars/all.yml", "k3s_version"));
    }

    [Fact]
    public void A_repository_may_be_named_once_for_each_kind_of_files_and_each_environment_but_not_twice_for_the_same()
    {
        const string perEnvironment = """
            { "environments": [ { "id": "staging" }, { "id": "production" } ],
              "repositories": [
                { "repository": "example/infra", "kind": "kustomize", "paths": ["overlays/staging/**"], "environment": "staging" },
                { "repository": "example/infra", "kind": "kustomize", "paths": ["overlays/prod/**"], "environment": "production" },
                { "repository": "example/infra", "kind": "terraform-docker", "paths": ["tf/*.tf"] } ] }
            """;
        const string twice = """
            { "repositories": [
                { "repository": "example/infra", "kind": "kustomize", "paths": ["a/**"] },
                { "repository": "example/infra", "kind": "kustomize", "paths": ["b/**"] } ] }
            """;

        DescriptionScope.Parse(perEnvironment).Repositories.Should().HaveCount(3);
        var act = () => DescriptionScope.Parse(twice);
        act.Should().Throw<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void A_description_with_no_section_is_an_empty_scope()
    {
        DescriptionScope.Parse("{}").Repositories.Should().BeEmpty();
        DescriptionScope.Parse("{\"applications\": []}").Platforms.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "repositories": [ { "repository": "no-owner", "kind": "kubernetes", "paths": ["a"] } ] }""", "owner/name")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "helm", "paths": ["a"] } ] }""", "kind of 'a/b'")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": [] } ] }""", "paths")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": ["../secret"] } ] }""", "paths")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": ["x"] }, { "repository": "a/b", "kind": "compose", "paths": ["y"] } ] }""", "twice")]
    [InlineData("""{ "environments": [ { "name": "no id" } ] }""", "environment has an id")]
    [InlineData("""{ "platforms": [ { "id": "p", "kind": "swarm" } ] }""", "kind")]
    [InlineData("""{ "platforms": [ { "id": "p", "kind": "os" } ] }""", "kind")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "terraform", "paths": ["x"] } ] }""", "kind of 'a/b'")]
    [InlineData("""{ "environments": [ { "id": "e" } ], "repositories": [ { "repository": "a/b", "kind": "compose", "paths": ["x"], "environment": "other" } ] }""", "not in the environments")]
    [InlineData("""{ "platforms": [ { "id": "p", "kind": "k3s", "version": { "repository": "a/b", "path": "x", "key": "k" } } ] }""", "repository of the description")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": ["x"] } ], "platforms": [ { "id": "p", "kind": "k3s", "version": { "repository": "a/b", "path": "x" } } ] }""", "with the key")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": [1, "a/**"] } ] }""", "are text")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": [null] } ] }""", "are text")]
    [InlineData("""{ "repositories": [ { "repository": "a/b", "kind": "compose", "paths": ["a\\b"] } ] }""", "relative")]
    [InlineData("""{ "environments": "k3s" }""", "is a list")]
    [InlineData("[]", "an object")]
    public void A_mistake_stops_the_start_and_says_where(string json, string expected)
    {
        var act = () => DescriptionScope.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void An_application_source_is_a_name_or_an_object_and_third_party_is_written_either_way()
    {
        var catalog = ApplicationCatalog.Parse("""
            { "applications": [
              { "id": "shop", "kind": "own", "source": { "repository": "example/shop" } },
              { "id": "db", "kind": "third-party", "source": { "image": "postgres" } },
              { "id": "old", "kind": "thirdParty", "source": "example/old" } ] }
            """);

        catalog.Find("shop")!.Source.Should().Be("example/shop");
        catalog.Find("db")!.Should().Match<ApplicationDescription>(description => description.Kind == "thirdParty" && description.Source == "postgres");
        catalog.Find("old")!.Source.Should().Be("example/old");
    }

    [Fact]
    public void A_work_item_is_described_under_its_own_id_or_under_one_of_its_applications()
    {
        var catalog = ApplicationCatalog.Parse("""{ "applications": [ { "id": "n8n", "kind": "thirdParty", "source": { "image": "n8nio/n8n" } } ] }""");
        WorkItem Item(string id, params string[] apps) => new(id, "n8n", null, apps, Severity.Info, [], [], [], PullRequestsRead.NotNeeded);

        catalog.Find(Item("image:n8nio/n8n", "n8n"))!.Id.Should().Be("n8n");
        catalog.Find(Item("image:other", "unknown")).Should().BeNull();
    }

    [Fact]
    public void A_work_item_that_is_several_applications_with_different_descriptions_is_described_by_none_of_them()
    {
        var catalog = ApplicationCatalog.Parse("""
            { "applications": [
              { "id": "a", "kind": "thirdParty", "source": { "image": "x" }, "update": { "promote": ["patch"] } },
              { "id": "b", "kind": "thirdParty", "source": { "image": "x" }, "update": { "promote": ["patch", "minor"] } },
              { "id": "c", "kind": "thirdParty", "source": { "image": "x" }, "update": { "promote": ["patch"] } } ] }
            """);
        WorkItem Item(params string[] apps) => new("image:x", "x", null, apps, Severity.Info, [], [], [], PullRequestsRead.NotNeeded);

        catalog.Find(Item("a", "b")).Should().BeNull("which one governs is not guessed");
        catalog.Find(Item("a", "unknown", "a")).Should().NotBeNull("one description, even named twice, is not ambiguous");
        catalog.Find(Item("a", "c")).Should().NotBeNull("two applications with the same description are not ambiguous");
    }
}
