using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class VersionParsersTests
{
    [Theory]
    [InlineData("postgres:15-alpine", "postgres", "15-alpine")]
    [InlineData("docker.io/library/postgres:15-alpine", "postgres", "15-alpine")]
    [InlineData("\"n8nio/n8n:1.108.2\"", "n8nio/n8n", "1.108.2")]
    [InlineData("GHCR.io/Example/App:2.0.1", "ghcr.io/example/app", "2.0.1")]
    [InlineData("registry.example:5000/team/app:3", "registry.example:5000/team/app", "3")]
    [InlineData("nginx", "nginx", null)]
    public void An_image_is_told_by_its_name_and_its_tag_whatever_the_way_it_is_written(string written, string name, string? tag)
    {
        var image = ImageReference.Parse(written)!;

        image.Name.Should().Be(name);
        image.Tag.Should().Be(tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("${IMAGE}")]
    [InlineData("{{ .Values.image }}")]
    [InlineData("two words")]
    public void What_is_not_a_reference_is_not_read(string written) => ImageReference.Parse(written).Should().BeNull();

    [Fact]
    public void A_digest_is_kept_apart_and_a_missing_tag_or_latest_is_floating()
    {
        ImageReference.Parse("app@sha256:abc")!.Digest.Should().Be("sha256:abc");
        ImageReference.Parse("app")!.IsFloating.Should().BeTrue();
        ImageReference.Parse("app:latest")!.IsFloating.Should().BeTrue();
        ImageReference.Parse("app:1.2")!.IsFloating.Should().BeFalse();
    }

    [Fact]
    public void The_image_lines_of_a_manifest_are_found_with_their_line_and_a_commented_one_is_not()
    {
        const string manifest = """
            apiVersion: apps/v1
            spec:
              containers:
                - name: web
                  image: ghcr.io/example/web:1.4.0
                # image: ghcr.io/example/old:0.1
                - image: "postgres:16-alpine"
                  imagePullPolicy: Always
            """;

        var found = VersionParsers.Find("kubernetes", "example/infra", "k8s/web.yaml", manifest);

        found.Select(location => (location.Line, location.Image.Name, location.Image.Tag)).Should().Equal((5, "ghcr.io/example/web", "1.4.0"), (7, "postgres", "16-alpine"));
        found.Should().OnlyContain(location => location.Form == VersionForm.ImageLine && location.Managed == null);
    }

    [Fact]
    public void A_line_that_Flux_rewrites_is_marked_as_its_own()
    {
        var found = VersionParsers.Find("kubernetes", "example/infra", "apps/web.yaml", """
            image: ghcr.io/example/web:0.3.0 # {"$imagepolicy": "flux-system:web"}
            """);

        found.Should().ContainSingle().Which.Managed.Should().Be("flux");
    }

    [Fact]
    public void The_tags_of_a_kustomization_are_found_in_flow_and_in_block_style()
    {
        const string kustomization = """
            images:
            - {name: ghcr.io/example/api, newTag: local-2026}
            - name: ghcr.io/example/front
              newTag: local-2027
            """;

        var found = VersionParsers.Find("kustomize", "example/infra", "k8s/overlays/prod/kustomization.yaml", kustomization);

        found.Select(location => (location.Line, location.Image.Name, location.Image.Tag, location.Form)).Should().Equal(
            (2, "ghcr.io/example/api", "local-2026", VersionForm.KustomizeTag), (4, "ghcr.io/example/front", "local-2027", VersionForm.KustomizeTag));
    }

    [Fact]
    public void A_kustomize_entry_that_renames_the_image_is_indexed_by_the_image_that_is_deployed()
    {
        const string kustomization = """
            images:
            - name: redis
              newName: registry.example/mirror/redis
              newTag: 7.2.3
            - {name: web, newName: registry.example/mirror/web, newTag: 1.0.0}
            - name: db
              newTag: 15
              newName: registry.example/mirror/db
            """;

        var found = VersionParsers.Find("kustomize", "example/infra", "kustomization.yaml", kustomization);

        found.Select(location => (location.Line, location.Image.Name, location.Image.Tag)).Should().Equal(
            (4, "registry.example/mirror/redis", "7.2.3"), (5, "registry.example/mirror/web", "1.0.0"), (7, "registry.example/mirror/db", "15"));
    }

    [Fact]
    public void A_kustomize_tag_that_Flux_rewrites_is_marked_as_its_own_in_block_and_in_flow_style()
    {
        const string kustomization = """
            images:
            - name: ghcr.io/example/api
              newTag: 1.2.3 # {"$imagepolicy": "flux-system:api:tag"}
            - {name: ghcr.io/example/web, newTag: 2.0.0} # {"$imagepolicy": "flux-system:web:tag"}
            - {name: ghcr.io/example/job, newTag: 3.0.0}
            """;

        var found = VersionParsers.Find("kustomize", "example/infra", "kustomization.yaml", kustomization);

        found.Select(location => (location.Image.Name, location.Managed)).Should().Equal(("ghcr.io/example/api", "flux"), ("ghcr.io/example/web", "flux"), ("ghcr.io/example/job", null));
        VersionPatch.Replace(kustomization, found[0], "1.2.4").Should().BeNull("Flux writes this line");
    }

    [Fact]
    public void A_terraform_image_is_read_from_a_docker_image_resource_and_a_name_elsewhere_is_not_an_image()
    {
        const string terraform = """
            resource "docker_image" "app" {
              name = "ghcr.io/example/app:2.1.0"
            }
            resource "docker_container" "app" {
              name  = "app"
              image = docker_image.app.image_id
            }
            resource "docker_container" "tool" {
              name  = "tool"
              image = "alpine:3.20"
            }
            """;

        var found = VersionParsers.Find("terraform-docker", "example/infra", "main.tf", terraform);

        found.Select(location => (location.Line, location.Image.Name, location.Image.Tag)).Should().Equal((2, "ghcr.io/example/app", "2.1.0"), (10, "alpine", "3.20"));
    }

    [Fact]
    public void A_kind_that_is_not_known_finds_nothing_and_a_variable_is_read_by_its_key()
    {
        VersionParsers.Find("helm", "example/infra", "values.yaml", "image: app:1").Should().BeEmpty();
        VersionParsers.Variable("---\nk3s_version: \"v1.36.5+k3s1\"      # stable\nother: 1\n", "k3s_version").Should().Be((2, "v1.36.5+k3s1"));
        VersionParsers.Variable("other: 1\n", "k3s_version").Should().BeNull();
    }
}
