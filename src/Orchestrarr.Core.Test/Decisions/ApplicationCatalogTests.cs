using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Test.Decisions;

public sealed class ApplicationCatalogTests
{
    private const string Full = """
        { "applications": [ {
          "id": "repository:example/shop", "kind": "own", "source": "example/shop",
          "qualify": { "mode": "smoke" },
          "promote": { "rollback": "previous-tag", "check": "/health" },
          "update": { "promote": ["patch", "minor"] } } ] }
        """;

    [Fact]
    public void A_full_description_says_what_may_be_done_and_how_far_an_update_may_go()
    {
        var catalog = ApplicationCatalog.Parse(Full);

        var shop = catalog.Find("repository:example/shop")!;
        shop.Kind.Should().Be("own");
        shop.Source.Should().Be("example/shop");
        shop.Qualify.Should().Be(QualifyMode.Smoke);
        shop.CanRollBack.Should().BeTrue();
        shop.HasCheckAfterDeploy.Should().BeTrue();
        shop.PromotesAutomatically.Should().Equal(UpdateKind.Patch, UpdateKind.Minor);
        catalog.Find("repository:example/other").Should().BeNull();
    }

    [Fact]
    public void Only_the_id_the_kind_and_the_source_are_required_and_a_missing_field_is_unknown_never_guessed()
    {
        var catalog = ApplicationCatalog.Parse("""{ "applications": [ { "id": "repository:example/a", "kind": "thirdParty", "source": "example/a" } ] }""");

        var a = catalog.Find("repository:example/a")!;
        a.Qualify.Should().Be(QualifyMode.Unknown);
        a.CanRollBack.Should().BeFalse();
        a.HasCheckAfterDeploy.Should().BeFalse();
        a.PromotesAutomatically.Should().BeEmpty("nothing is promoted unless the description says which classes may be");
    }

    [Fact]
    public void The_version_changes_with_the_content_and_is_empty_without_descriptions()
    {
        var one = ApplicationCatalog.Parse(Full).Version;

        one.Should().HaveLength(12);
        ApplicationCatalog.Parse(Full).Version.Should().Be(one);
        ApplicationCatalog.Parse(Full.Replace("smoke", "e2e", StringComparison.Ordinal)).Version.Should().NotBe(one);
        ApplicationCatalog.Parse("""{ "applications": [] }""").Version.Should().BeEmpty();
        new EmptyApplicationCatalog().Version.Should().BeEmpty();
        new EmptyApplicationCatalog().Find("repository:example/shop").Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "applications": [ { "kind": "own", "source": "x" } ] }""", "id, a kind and a source")]
    [InlineData("""{ "applications": [ { "id": "a", "source": "x" } ] }""", "id, a kind and a source")]
    [InlineData("""{ "applications": [ { "id": "a", "kind": "own" } ] }""", "id, a kind and a source")]
    [InlineData("""{ "applications": [ { "id": "a", "kind": "mine", "source": "x" } ] }""", "own or thirdParty")]
    [InlineData("""{ "applications": [ { "id": "a", "kind": "own", "source": "x" }, { "id": "a", "kind": "own", "source": "y" } ] }""", "described twice")]
    [InlineData("""{ "applications": [ { "id": "a", "kind": "own", "source": "x", "qualify": { "mode": "magic" } } ] }""", "cannot be read")]
    [InlineData("not json", "cannot be read")]
    public void A_wrong_description_stops_the_start_and_says_why(string content, string message)
    {
        var read = () => ApplicationCatalog.Parse(content);

        read.Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Classes_that_are_not_a_kind_of_update_are_ignored()
    {
        var catalog = ApplicationCatalog.Parse("""{ "applications": [ { "id": "a", "kind": "own", "source": "x", "update": { "promote": ["unknown", "patch", "patch"] } } ] }""");

        catalog.Find("a")!.PromotesAutomatically.Should().Equal(UpdateKind.Patch);
    }
}
