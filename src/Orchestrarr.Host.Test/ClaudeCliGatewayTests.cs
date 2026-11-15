using System.Runtime.Versioning;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;

namespace Orchestrarr.Host.Test;

[UnsupportedOSPlatform("windows")]
public sealed class ClaudeCliGatewayTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-agent-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AgentRequest Request(TimeSpan? timeout = null) => new("the system prompt", "the user prompt", "model-x", timeout ?? TimeSpan.FromSeconds(20));

    private string Script(string body)
    {
        var path = Path.Combine(_folder, "fake-claude");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void The_arguments_give_no_tool_no_mcp_no_session_and_the_system_prompt_as_an_argument_not_a_shell_line()
    {
        var (program, arguments) = new ClaudeCliGateway(new AgentsOptions { Command = "claude" }).Command(Request());

        program.Should().Be("claude");
        arguments.Should().ContainInConsecutiveOrder("--tools", string.Empty, "--strict-mcp-config");
        arguments.Should().Contain(["--no-session-persistence", "--disable-slash-commands", "-p"]);
        arguments.Should().ContainInConsecutiveOrder("--setting-sources", string.Empty, "--system-prompt");
        arguments.Should().ContainInConsecutiveOrder("--model", "model-x");
        arguments.Should().ContainInConsecutiveOrder("--system-prompt", "the system prompt");
        arguments.Should().NotContain("the user prompt", "the dossier goes on standard input: an argument is limited in length and shows in the process list");
    }

    [Fact]
    public async Task The_user_prompt_goes_on_standard_input_and_the_program_sees_nothing_of_the_environment_of_the_host()
    {
        Environment.SetEnvironmentVariable("ORCHESTRARR_TEST_SECRET", "leaked");
        try
        {
            var command = Script("input=$(cat)\necho \"$input|${ORCHESTRARR_TEST_SECRET:-absent}\"\n");

            var reply = await new ClaudeCliGateway(new AgentsOptions { Command = command }).RunAsync(Request(), TestContext.Current.CancellationToken);

            reply.Ok.Should().BeTrue();
            reply.Text.Trim().Should().Be("the user prompt|absent");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORCHESTRARR_TEST_SECRET", null);
        }
    }

    [Fact]
    public void A_wrapper_goes_in_front_of_the_program()
    {
        var (program, arguments) = new ClaudeCliGateway(new AgentsOptions { Command = "claude", Wrapper = ["systemd-run", "--user", "--wait"] }).Command(Request());

        program.Should().Be("systemd-run");
        arguments.Take(3).Should().Equal("--user", "--wait", "claude");
    }

    [Fact]
    public async Task The_text_of_the_answer_comes_back_and_the_standard_input_is_closed()
    {
        var command = Script("cat > /dev/null\necho '{\"decision\":\"grant\",\"reasons\":[\"ok\"]}'\n");

        var reply = await new ClaudeCliGateway(new AgentsOptions { Command = command }).RunAsync(Request(), TestContext.Current.CancellationToken);

        reply.Ok.Should().BeTrue();
        reply.Text.Should().Contain("\"decision\":\"grant\"");
    }

    [Fact]
    public async Task The_token_goes_in_the_environment_and_never_in_the_arguments_or_the_output_the_gateway_returns_only_the_answer()
    {
        var tokenFile = Path.Combine(_folder, "token");
        await File.WriteAllTextAsync(tokenFile, "secret-token-value\n", TestContext.Current.CancellationToken);
        var command = Script("echo \"args:$*\" > \"" + Path.Combine(_folder, "seen") + "\"\n[ -n \"$CLAUDE_CODE_OAUTH_TOKEN\" ] && echo token-present\n");

        var reply = await new ClaudeCliGateway(new AgentsOptions { Command = command, TokenFile = tokenFile }).RunAsync(Request(), TestContext.Current.CancellationToken);

        reply.Text.Trim().Should().Be("token-present");
        (await File.ReadAllTextAsync(Path.Combine(_folder, "seen"), TestContext.Current.CancellationToken)).Should().NotContain("secret-token-value");
    }

    [Fact]
    public async Task A_program_that_fails_or_cannot_start_is_no_answer_with_a_reason()
    {
        var failing = await new ClaudeCliGateway(new AgentsOptions { Command = Script("exit 3\n") }).RunAsync(Request(), TestContext.Current.CancellationToken);
        var missing = await new ClaudeCliGateway(new AgentsOptions { Command = Path.Combine(_folder, "nothing") }).RunAsync(Request(), TestContext.Current.CancellationToken);

        failing.Ok.Should().BeFalse();
        failing.Failure.Should().Contain("code 3");
        missing.Ok.Should().BeFalse();
        missing.Failure.Should().Contain("could not be started");
    }

    [Fact]
    public async Task A_program_that_does_not_answer_in_time_is_killed_and_the_failure_says_so()
    {
        var command = Script("sleep 30\n");

        var reply = await new ClaudeCliGateway(new AgentsOptions { Command = command }).RunAsync(Request(TimeSpan.FromMilliseconds(500)), TestContext.Current.CancellationToken);

        reply.Ok.Should().BeFalse();
        reply.Failure.Should().Contain("in time");
    }

    [Fact]
    public async Task The_program_runs_in_an_empty_folder_of_its_own_that_is_removed_after()
    {
        var seen = Path.Combine(_folder, "cwd");
        var command = Script("pwd > \"" + seen + "\"\nls -A | wc -l >> \"" + seen + "\"\n");

        await new ClaudeCliGateway(new AgentsOptions { Command = command }).RunAsync(Request(), TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(seen, TestContext.Current.CancellationToken);
        lines[1].Trim().Should().Be("0", "the folder is empty");
        Directory.Exists(lines[0]).Should().BeFalse("it is removed after the run");
    }

    [Fact]
    public async Task Agents_that_are_not_enabled_answer_with_a_reason_and_run_nothing()
    {
        var reply = await new DisabledGateway().RunAsync(Request(), TestContext.Current.CancellationToken);

        reply.Ok.Should().BeFalse();
        reply.Failure.Should().Contain("not enabled");
    }

    [Fact]
    public async Task A_token_file_that_is_named_and_missing_is_no_answer_not_a_silent_fall_back_to_another_login()
    {
        var command = Script("echo ran\n");

        var reply = await new ClaudeCliGateway(new AgentsOptions { Command = command, TokenFile = Path.Combine(_folder, "nothing") }).RunAsync(Request(), TestContext.Current.CancellationToken);

        reply.Ok.Should().BeFalse();
        reply.Failure.Should().Contain("token file");
    }

    [Fact]
    public async Task Without_a_token_file_the_program_keeps_the_login_of_the_user_and_with_one_it_sees_only_its_own_folder()
    {
        var command = Script("echo \"$HOME\"\n");

        var login = await new ClaudeCliGateway(new AgentsOptions { Command = command }).RunAsync(Request(), TestContext.Current.CancellationToken);
        var tokenFile = Path.Combine(_folder, "token");
        await File.WriteAllTextAsync(tokenFile, "the-token", TestContext.Current.CancellationToken);
        var token = await new ClaudeCliGateway(new AgentsOptions { Command = command, TokenFile = tokenFile }).RunAsync(Request(), TestContext.Current.CancellationToken);

        login.Text.Trim().Should().Be(Environment.GetEnvironmentVariable("HOME"));
        token.Text.Trim().Should().StartWith(Path.GetTempPath().TrimEnd('/')).And.Contain("orchestrarr-agent-");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_token_file_that_cannot_be_read_is_no_answer_with_a_reason()
    {
        var unreadable = Path.Combine(_folder, "unreadable-token");
        await File.WriteAllTextAsync(unreadable, "the-token", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);

        var reply = await new ClaudeCliGateway(new AgentsOptions { Command = Script("echo hi\n"), TokenFile = unreadable }).RunAsync(Request(), TestContext.Current.CancellationToken);

        reply.Ok.Should().BeFalse();
        reply.Failure.Should().Contain("cannot be read");
    }
}
