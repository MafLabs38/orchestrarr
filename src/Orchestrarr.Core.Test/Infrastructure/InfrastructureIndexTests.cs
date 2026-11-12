using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class InfrastructureIndexTests
{
    private static InfrastructureSnapshot Snapshot(params InfrastructureFile[] files) => new(DateTimeOffset.UnixEpoch, files, []);

    [Fact]
    public void An_image_is_found_by_its_name_wherever_it_is_written_and_in_whatever_spelling()
    {
        var index = InfrastructureIndex.Build(Snapshot(
            new InfrastructureFile("example/infra", "k8s/db.yaml", "kubernetes", "image: postgres:15-alpine\n"),
            new InfrastructureFile("example/other", "compose.yml", "compose", "    image: docker.io/library/postgres:16-alpine\n")));

        var found = index.Find(ImageReference.Parse("postgres:15-alpine")!);

        found.Select(location => (location.Repository, location.Path, location.Image.Tag)).Should().Equal(("example/infra", "k8s/db.yaml", "15-alpine"), ("example/other", "compose.yml", "16-alpine"));
        index.Find(ImageReference.Parse("mariadb:11")!).Should().BeEmpty();
    }

    [Fact]
    public void The_text_that_was_read_stays_with_the_index_and_nothing_is_in_scope_without_a_file_or_a_problem()
    {
        var index = InfrastructureIndex.Build(Snapshot(new InfrastructureFile("example/infra", "k8s/db.yaml", "kubernetes", "image: postgres:15\n")));

        index.Content("example/infra", "k8s/db.yaml").Should().Be("image: postgres:15\n");
        index.Content("example/infra", "elsewhere.yaml").Should().BeNull();
        index.InScope.Should().BeTrue();
        InfrastructureIndex.Empty.InScope.Should().BeFalse();
        InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch, [], [new RepositoryProblem("example/infra", "unreadable")])).InScope.Should().BeTrue("a repository that cannot be read is still in the scope");
    }
}
