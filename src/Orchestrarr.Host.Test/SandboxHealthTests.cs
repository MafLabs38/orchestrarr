using System.Net;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class SandboxHealthTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task The_control_plane_says_what_the_health_route_answered_and_that_it_read_it_itself()
    {
        const string Repository = "example/shop";
        var port = FreePort();
        var basePort = port - DeploymentPort.Of(Repository, 0);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        }, Ct);

        var said = await new HttpSandboxHealth("127.0.0.1", basePort).ReadAsync(Repository, Ct);

        await serving;
        said.Should().Contain("answered 200").And.Contain("read by the control plane");
    }

    [Fact]
    public async Task An_application_that_does_not_answer_is_said_not_to_answer()
    {
        var port = FreePort();

        var said = await new HttpSandboxHealth("127.0.0.1", port - DeploymentPort.Of("example/shop", 0)).ReadAsync("example/shop", Ct);

        said.Should().Contain("did not answer");
    }
}
