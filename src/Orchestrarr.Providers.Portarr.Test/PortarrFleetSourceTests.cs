using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr.Test;

public sealed class PortarrFleetSourceTests
{
    private static readonly string SampleFile = Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json");
    private static readonly Uri BaseUrl = new("http://portarr.example:8484");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    private PortarrFleetSource Source(StubHandler handler, string? apiKey = null, long maxBytes = 1024 * 1024)
    {
        return PortarrFleetSource.Create(new PortarrOptions { BaseUrl = BaseUrl, ApiKey = apiKey, MaxResponseBytes = maxBytes }, _time, handler);
    }

    [Fact]
    public async Task The_sample_file_is_read_and_stamped_with_the_time_of_the_read()
    {
        var source = new SampleFleetSource(SampleFile, _time);

        var fleet = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Kind.Should().Be(FleetSourceKinds.Sample);
        source.Configured.Should().BeTrue();
        fleet.ReadAt.Should().Be(_time.GetUtcNow());
        fleet.Repositories.Should().NotBeEmpty();
        fleet.Images.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_missing_sample_file_is_an_error_not_an_empty_fleet()
    {
        var read = () => new SampleFleetSource(Path.Combine(AppContext.BaseDirectory, "nope.json"), _time).ReadAsync(TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Portarr_is_read_with_a_single_get_at_its_maintenance_route_with_the_api_key_header()
    {
        var handler = new StubHandler("""{ "repositories": [ { "fullName": "example/app", "dependabotPullRequests": 2 } ] }""");

        var source = Source(handler, apiKey: "ptr_secret");
        var fleet = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Kind.Should().Be(FleetSourceKinds.Portarr);
        fleet.Repositories.Should().ContainSingle().Which.OpenDependencyPullRequests.Should().Be(2);
        handler.Request!.Method.Should().Be(HttpMethod.Get);
        handler.Request.RequestUri.Should().Be(new Uri("http://portarr.example:8484/api/v1/maintenance"));
        handler.Request.Headers.GetValues(PortarrFleetSource.ApiKeyHeader).Should().Equal("ptr_secret");
    }

    [Fact]
    public async Task The_names_and_icons_of_the_applications_are_read_and_the_images_know_the_applications_that_use_them()
    {
        var handler = new StubHandler("""{ "images": [ { "reference": "n8nio/n8n:1.108.2", "users": [ { "environmentId": "k3s", "environmentName": "K3s", "name": "n8n-1", "appIds": ["n8n"] }, { "environmentId": "k3s", "environmentName": "K3s", "name": "n8n-2", "appIds": ["n8n"] } ] } ] }""");
        handler.Others["/api/v1/apps"] = (HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes("""[ { "id": "n8n", "name": "n8n", "icon": "n8n" }, { "id": "bare", "name": "", "icon": " " } ]"""));

        var fleet = await Source(handler, apiKey: "ptr_secret").ReadAsync(TestContext.Current.CancellationToken);

        fleet.Applications.Should().BeEquivalentTo([new ApplicationInfo("n8n", "n8n", "n8n"), new ApplicationInfo("bare", "bare", null)]);
        fleet.Images.Single().AppIds.Should().Equal("n8n");
        handler.Requests.Select(request => request.RequestUri!.AbsolutePath).Should().Equal("/api/v1/maintenance", "/api/v1/apps");
        handler.Requests.Should().OnlyContain(request => request.Headers.Contains(PortarrFleetSource.ApiKeyHeader));
    }

    [Fact]
    public async Task Applications_that_cannot_be_read_do_not_cost_the_report()
    {
        var handler = new StubHandler("""{ "repositories": [ { "fullName": "example/app" } ] }""");
        handler.Others["/api/v1/apps"] = (HttpStatusCode.InternalServerError, "application/json", []);

        var fleet = await Source(handler).ReadAsync(TestContext.Current.CancellationToken);

        fleet.Repositories.Should().ContainSingle();
        fleet.Applications.Should().BeEmpty();
    }

    [Fact]
    public async Task An_icon_is_relayed_with_its_type_kept_for_an_hour_and_only_a_picture_of_a_reasonable_size_is_accepted()
    {
        var handler = new StubHandler("{}");
        handler.Others["/api/v1/icons/jellyfin"] = (HttpStatusCode.OK, "image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"));
        handler.Others["/api/v1/icons/page"] = (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<html/>"));
        handler.Others["/api/v1/icons/huge"] = (HttpStatusCode.OK, "image/png", new byte[300 * 1024]);
        var source = Source(handler, apiKey: "ptr_secret");

        var first = await source.GetAsync("jellyfin", TestContext.Current.CancellationToken);
        var second = await source.GetAsync("jellyfin", TestContext.Current.CancellationToken);

        first!.ContentType.Should().Be("image/svg+xml");
        second.Should().BeSameAs(first, "the picture is kept");
        handler.Requests.Count.Should().Be(1);
        (await source.GetAsync("page", TestContext.Current.CancellationToken)).Should().BeNull("a page is not a picture");
        (await source.GetAsync("huge", TestContext.Current.CancellationToken)).Should().BeNull();
        _time.Advance(TimeSpan.FromHours(2));
        await source.GetAsync("jellyfin", TestContext.Current.CancellationToken);
        handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("jellyfin", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_missing_icon_is_remembered_but_a_refusal_or_a_failure_of_portarr_is_asked_again()
    {
        var handler = new StubHandler("{}");
        handler.Others["/api/v1/icons/gone"] = (HttpStatusCode.NotFound, "text/plain", []);
        handler.Others["/api/v1/icons/down"] = (HttpStatusCode.InternalServerError, "text/plain", []);
        handler.Others["/api/v1/icons/locked"] = (HttpStatusCode.Unauthorized, "text/plain", []);
        var source = Source(handler, apiKey: "ptr_secret");
        var ct = TestContext.Current.CancellationToken;

        foreach (var name in new[] { "gone", "down", "locked" })
        {
            (await source.GetAsync(name, ct)).Should().BeNull();
            (await source.GetAsync(name, ct)).Should().BeNull();
        }

        int Asked(string name) => handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith(name, StringComparison.Ordinal));
        Asked("gone").Should().Be(1, "no icon is an answer, kept for an hour");
        Asked("down").Should().Be(2, "a failure is not an answer");
        Asked("locked").Should().Be(2, "a refusal is not an answer");
    }

    [Fact]
    public async Task Without_an_api_key_no_header_is_sent()
    {
        var handler = new StubHandler("{}");

        await Source(handler).ReadAsync(TestContext.Current.CancellationToken);

        handler.Request!.Headers.Contains(PortarrFleetSource.ApiKeyHeader).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task A_refusal_or_a_failure_is_an_error_that_keeps_the_status(HttpStatusCode status)
    {
        var read = () => Source(new StubHandler("{}", status)).ReadAsync(TestContext.Current.CancellationToken);

        (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData("null", typeof(InvalidOperationException))]
    [InlineData("{bad", typeof(System.Text.Json.JsonException))]
    public async Task An_answer_that_is_not_a_report_is_an_error(string body, Type expected)
    {
        var read = () => Source(new StubHandler(body)).ReadAsync(TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<Exception>().Where(e => expected.IsInstanceOfType(e));
    }

    [Fact]
    public async Task A_report_larger_than_the_limit_is_refused_instead_of_buffered()
    {
        var read = () => Source(new StubHandler(new string(' ', 5_000) + "{}"), maxBytes: 1_000).ReadAsync(TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_key_never_goes_to_another_address()
    {
        var elsewhere = new Listener(_ => (HttpStatusCode.OK, "{}"));
        var configured = new Listener(_ => (HttpStatusCode.Found, null), location: elsewhere.Address);
        using (elsewhere)
        using (configured)
        {
            var source = PortarrFleetSource.Create(new PortarrOptions { BaseUrl = configured.Address, ApiKey = "ptr_secret" }, _time);

            var read = () => source.ReadAsync(TestContext.Current.CancellationToken);

            (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Found);
            configured.Requests.Should().Be(1);
            elsewhere.Requests.Should().Be(0);
        }
    }

    [Fact]
    public void A_source_cannot_be_built_without_an_address()
    {
        var build = () => PortarrFleetSource.Create(new PortarrOptions(), _time);

        build.Should().Throw<InvalidOperationException>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        /// <summary>The first request: the maintenance report.</summary>
        public HttpRequestMessage? Request => Requests.FirstOrDefault();

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>What a path answers when it is not the maintenance report: (status, content type, body).</summary>
        public Dictionary<string, (HttpStatusCode Status, string Type, byte[] Body)> Others { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Others.TryGetValue(request.RequestUri!.AbsolutePath, out var other))
            {
                var answer = new HttpResponseMessage(other.Status) { Content = new ByteArrayContent(other.Body) };
                answer.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(other.Type);
                return Task.FromResult(answer);
            }
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/apps", StringComparison.Ordinal) ? "[]" : _body, Encoding.UTF8, "application/json") });
        }
    }

    // A real local server: the redirect behavior belongs to the network handler, which a stub would bypass
    private sealed class Listener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public Listener(Func<HttpListenerRequest, (HttpStatusCode Status, string? Body)> answer, Uri? location = null)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Address.ToString());
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        Interlocked.Increment(ref _requests);
                        var (status, body) = answer(context.Request);
                        context.Response.StatusCode = (int)status;
                        if (location is not null)
                        {
                            context.Response.RedirectLocation = location.ToString();
                        }

                        var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
                        await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    {
                        return;
                    }
                }
            }, _stop.Token);
        }

        public Uri Address { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }
    }
}
