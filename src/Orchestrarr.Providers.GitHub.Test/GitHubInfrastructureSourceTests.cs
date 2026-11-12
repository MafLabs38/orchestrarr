using System.Net;
using System.Text;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class GitHubInfrastructureSourceTests
{
    private static readonly string ShaA = new('a', 40);
    private static readonly string ShaB = new('b', 40);
    private readonly Routes _handler = new();

    private GitHubInfrastructureSource Source() => GitHubInfrastructureSource.Create(new GitHubOptions { Token = "ghp_secret" }, _handler);

    private static RepositoryScope Scope(string repository = "example/infra", params string[] paths) => new(repository, "kubernetes", paths.Length == 0 ? ["apps/**/*.yaml"] : paths);

    private void Tree(string repository, string entries, bool truncated = false) =>
        _handler.On($"/repos/{repository}/git/trees/HEAD", $$"""{ "sha": "{{ShaA}}", "truncated": {{(truncated ? "true" : "false")}}, "tree": [ {{entries}} ] }""");

    private void Blob(string repository, string sha, string text) =>
        _handler.On($"/repos/{repository}/git/blobs/{sha}", $$"""{ "encoding": "base64", "content": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}}" }""");

    private static string Entry(string path, string sha, long size = 100, string type = "blob") => $$"""{ "path": "{{path}}", "type": "{{type}}", "sha": "{{sha}}", "size": {{size}} }""";

    [Fact]
    public async Task The_files_that_a_path_of_the_description_matches_are_read_and_nothing_else()
    {
        Tree("example/infra", string.Join(",", Entry("apps/web/deployment.yaml", ShaA), Entry("apps/web/secret.enc.yaml", ShaB), Entry("docs/readme.md", ShaB), Entry("apps/web", ShaB, type: "tree")));
        Blob("example/infra", ShaA, "image: web:1\n");

        var snapshot = await Source().ReadAsync([Scope()], TestContext.Current.CancellationToken);

        snapshot.Files.Should().ContainSingle().Which.Should().Be(new InfrastructureFile("example/infra", "apps/web/deployment.yaml", "kubernetes", "image: web:1\n"));
        snapshot.Problems.Should().BeEmpty();
        _handler.Paths.Should().NotContain(path => path.Contains(ShaB, StringComparison.Ordinal), "a secret file, a file outside the paths and a folder are not fetched");
    }

    [Fact]
    public async Task A_file_that_did_not_change_is_not_read_again_and_one_that_changed_is()
    {
        Tree("example/infra", Entry("apps/a.yaml", ShaA));
        Blob("example/infra", ShaA, "image: a:1\n");
        var source = Source();
        await source.ReadAsync([Scope()], TestContext.Current.CancellationToken);
        await source.ReadAsync([Scope()], TestContext.Current.CancellationToken);
        _handler.Calls($"/repos/example/infra/git/blobs/{ShaA}").Should().Be(1);

        Tree("example/infra", Entry("apps/a.yaml", ShaB));
        Blob("example/infra", ShaB, "image: a:2\n");
        var snapshot = await source.ReadAsync([Scope()], TestContext.Current.CancellationToken);

        snapshot.Files.Single().Content.Should().Be("image: a:2\n");
    }

    [Fact]
    public async Task A_repository_that_cannot_be_read_is_a_problem_and_the_others_are_still_read()
    {
        _handler.On("/repos/example/gone/git/trees/HEAD", "{}", HttpStatusCode.NotFound);
        Tree("example/infra", Entry("apps/a.yaml", ShaA));
        Blob("example/infra", ShaA, "image: a:1\n");

        var snapshot = await Source().ReadAsync([Scope("example/gone"), Scope()], TestContext.Current.CancellationToken);

        snapshot.Files.Should().ContainSingle();
        snapshot.Problems.Should().ContainSingle().Which.Should().Be(new RepositoryProblem("example/gone", "not found or no access"));
    }

    [Theory]
    [InlineData("a/b/../c")]
    [InlineData("not a repository")]
    [InlineData("../x")]
    public async Task What_is_not_a_repository_name_is_never_sent_to_the_forge(string name)
    {
        var snapshot = await Source().ReadAsync([Scope(name)], TestContext.Current.CancellationToken);

        snapshot.Problems.Should().ContainSingle().Which.Reason.Should().Be("not a repository name");
        _handler.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tree_cut_by_the_forge_and_a_file_too_big_are_told_or_left_out()
    {
        Tree("example/big", Entry("apps/a.yaml", ShaA), truncated: true);
        Tree("example/infra", string.Join(",", Entry("apps/huge.yaml", ShaA, size: 10_000_000), Entry("apps/ok.yaml", ShaB)));
        Blob("example/infra", ShaB, "image: b:1\n");

        var snapshot = await Source().ReadAsync([Scope("example/big"), Scope()], TestContext.Current.CancellationToken);

        snapshot.Problems.Should().ContainSingle().Which.Reason.Should().Be("tree too large");
        snapshot.Files.Select(file => file.Path).Should().Equal("apps/ok.yaml");
    }

    [Fact]
    public void Without_a_token_or_with_an_address_that_is_not_https_the_start_stops()
    {
        var withoutToken = () => GitHubInfrastructureSource.Create(new GitHubOptions(), _handler);
        var plain = () => GitHubInfrastructureSource.Create(new GitHubOptions { Token = "t", ApiUrl = new Uri("http://api.example/") }, _handler);

        withoutToken.Should().Throw<InvalidOperationException>().WithMessage("*Token*");
        plain.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_told_so_that_an_image_does_not_look_as_if_it_were_written_nowhere()
    {
        Tree("example/infra", string.Join(",", Entry("apps/a.yaml", ShaA), Entry("apps/b.yaml", ShaB)));
        Blob("example/infra", ShaA, "image: a:1\n");
        _handler.On($"/repos/example/infra/git/blobs/{ShaB}", "{}", HttpStatusCode.BadGateway);

        var snapshot = await Source().ReadAsync([Scope()], TestContext.Current.CancellationToken);

        snapshot.Files.Should().ContainSingle();
        snapshot.Problems.Should().ContainSingle().Which.Should().Be(new RepositoryProblem("example/infra", "1 files could not be read"));
    }

    [Fact]
    public async Task An_answer_that_is_not_what_the_api_documents_is_a_problem_of_that_repository_and_the_others_are_still_read()
    {
        _handler.On("/repos/example/odd/git/trees/HEAD", "{ \"unexpected\": true }");
        Tree("example/infra", Entry("apps/a.yaml", ShaA));
        Blob("example/infra", ShaA, "image: a:1\n");

        var snapshot = await Source().ReadAsync([Scope("example/odd"), Scope()], TestContext.Current.CancellationToken);

        snapshot.Problems.Should().ContainSingle().Which.Should().Be(new RepositoryProblem("example/odd", "unreadable answer"));
        snapshot.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task The_environment_of_the_repository_goes_with_its_files()
    {
        Tree("example/infra", Entry("apps/a.yaml", ShaA));
        Blob("example/infra", ShaA, "image: a:1\n");

        var snapshot = await Source().ReadAsync([new RepositoryScope("example/infra", "kubernetes", ["apps/**/*.yaml"], "k3s")], TestContext.Current.CancellationToken);

        snapshot.Files.Single().Environment.Should().Be("k3s");
    }

    private sealed class Routes : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _routes = [];
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths => _paths;

        public void On(string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[path] = (body, status);

        public int Calls(string path) => _paths.Count(p => p == path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _paths.Add(path);
            var route = _routes.TryGetValue(path, out var found) ? found : ("{}", HttpStatusCode.NotFound);
            return Task.FromResult(new HttpResponseMessage(route.Item2) { Content = new StringContent(route.Item1, Encoding.UTF8, "application/json") });
        }
    }
}
