using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class VersionPatchTests
{
    private const string Manifest = "a: 1\nb: 2\nc: 3\n        image: ghcr.io/example/web:1.4.0\nd: 4\ne: 5\nf: 6\ng: 7\n";

    private static CodeLocation First(string kind, string content) => VersionParsers.Find(kind, "example/infra", "k8s/web.yaml", content)[0];

    [Fact]
    public void Only_the_line_of_the_version_changes_and_the_change_is_a_unified_diff_of_that_line_alone()
    {
        var change = VersionPatch.Replace(Manifest, First("kubernetes", Manifest), "1.5.0")!;

        change.NewContent.Should().Be(Manifest.Replace("web:1.4.0", "web:1.5.0", StringComparison.Ordinal));
        change.Before.Trim().Should().Be("image: ghcr.io/example/web:1.4.0");
        change.After.Trim().Should().Be("image: ghcr.io/example/web:1.5.0");
        change.Diff.Should().Be("--- a/k8s/web.yaml\n+++ b/k8s/web.yaml\n@@ -4,1 +4,1 @@\n-        image: ghcr.io/example/web:1.4.0\n+        image: ghcr.io/example/web:1.5.0\n",
            "no line around it goes out: the file is a private one");
    }

    [Theory]
    [InlineData("image: \"postgres:15-alpine\"", "image: \"postgres:15.9-alpine\"", "15.9-alpine")]
    [InlineData("image: postgres", "image: postgres:15.9", "15.9")]
    [InlineData("image: docker.io/library/postgres:latest # pinned later", "image: docker.io/library/postgres:15.9 # pinned later", "15.9")]
    public void The_name_as_it_is_written_stays_and_the_tag_or_digest_becomes_the_new_tag(string line, string expected, string newTag)
    {
        var change = VersionPatch.Replace(line, First("kubernetes", line), newTag)!;

        change.After.Should().Be(expected);
    }

    [Fact]
    public void A_reference_pinned_by_digest_is_never_changed_for_a_tag()
    {
        const string pinned = "image: app:1.0@sha256:abc123\nimage: other@sha256:def456\n";

        VersionPatch.Replace(pinned, VersionParsers.Find("kubernetes", "example/infra", "a.yaml", pinned)[0], "2.0").Should().BeNull();
        VersionPatch.Replace(pinned, VersionParsers.Find("kubernetes", "example/infra", "a.yaml", pinned)[1], "2.0").Should().BeNull();
    }

    [Fact]
    public void A_kustomize_tag_changes_in_flow_and_in_block_style()
    {
        const string flow = "images:\n- {name: ghcr.io/example/api, newTag: local-1}\n";
        const string block = "images:\n- name: ghcr.io/example/api\n  newTag: local-1\n";

        VersionPatch.Replace(flow, First("kustomize", flow), "local-2")!.After.Should().Be("- {name: ghcr.io/example/api, newTag: local-2}");
        VersionPatch.Replace(block, First("kustomize", block), "local-2")!.After.Should().Be("  newTag: local-2");
    }

    [Fact]
    public void A_terraform_image_changes_inside_its_quotes()
    {
        const string terraform = "resource \"docker_image\" \"app\" {\n  name = \"ghcr.io/example/app:2.1.0\"\n}\n";

        VersionPatch.Replace(terraform, First("terraform-docker", terraform), "2.2.0")!.After.Should().Be("  name = \"ghcr.io/example/app:2.2.0\"");
    }

    [Fact]
    public void A_line_that_Flux_owns_or_that_changed_since_it_was_read_or_a_bad_tag_gives_no_change()
    {
        const string managed = "image: ghcr.io/example/web:0.3.0 # {\"$imagepolicy\": \"flux-system:web\"}\n";
        var location = First("kubernetes", Manifest);

        VersionPatch.Replace(managed, First("kubernetes", managed), "0.4.0").Should().BeNull("Flux writes this line");
        VersionPatch.Replace("a: 1\n", location, "1.5.0").Should().BeNull("the line is not there any more");
        VersionPatch.Replace(Manifest.Replace("example/web", "example/other", StringComparison.Ordinal), location, "1.5.0").Should().BeNull("the image on the line is not the one that was read");
        VersionPatch.Replace(Manifest, location, "1.5 0").Should().BeNull();
        VersionPatch.Replace(Manifest, location, "1.4.0").Should().BeNull("nothing changes");
    }

    [Theory]
    [InlineData("1.0},{name:x,newTag:y")]
    [InlineData("1.0\"#")]
    [InlineData("1.0@sha256:abcdef")]
    [InlineData("1.0\n2")]
    [InlineData("-1.0")]
    [InlineData("")]
    [InlineData("a/b")]
    public void A_tag_that_is_not_in_the_grammar_of_a_tag_is_never_written(string tag)
    {
        const string manifest = "image: ghcr.io/example/web:1.4.0\n";

        VersionPatch.Replace(manifest, First("kubernetes", manifest), tag).Should().BeNull();
        ImageReference.IsTag(tag).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("v1.5.0-rc.1")]
    [InlineData("15-alpine")]
    [InlineData("local-20261030g")]
    public void The_usual_tags_pass_the_grammar(string tag) => ImageReference.IsTag(tag).Should().BeTrue();
}
