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

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
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
