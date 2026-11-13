using System.Diagnostics;
using System.Text;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Agents;

/// <summary>The settings of the agents (ADR 0033): the program that runs the model, and what wraps it where it cannot start directly.</summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Agents";

    /// <summary>The program: Claude Code, run non-interactively under the maintainer's subscription.</summary>
    public string Command { get; set; } = "claude";

    /// <summary>A prefix of arguments, for a host where the program refuses to start inside another session (for example <c>systemd-run --user --wait --pipe --quiet</c>). Empty: none.</summary>
    public string[] Wrapper { get; set; } = [];

    /// <summary>The model of the controller. A setting, never a constant of the code (the model of each role is configuration).</summary>
    public string ControllerModel { get; set; } = "sonnet";

    public int ControllerTimeoutSeconds { get; set; } = 180;

    /// <summary>A file with the subscription token, given to the program as an environment variable and never in an argument. Unset: the program uses the login of the user it runs as.</summary>
    public string? TokenFile { get; set; }

    /// <summary>
    /// Whether the control plane may run the agent program itself. **Off by default**: in a deployment the agents run in a sandbox of a runner profile (ARCHITECTURE section 3, D-25), and this
    /// in-process way is the interim for development and for the first controller.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>The gateway when the agents are not enabled: nothing runs, and the answer says why.</summary>
public sealed class DisabledGateway : IAgentGateway
{
    public Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken) => Task.FromResult(new AgentReply(false, string.Empty, "the agents are not enabled (Agents:Enabled)"));
}

/// <summary>
/// Runs the model through the Claude Code program with **no tools** (ADR 0032, 0033): the arguments are a list, never a shell line; the standard input is closed; the process is killed at the
/// time limit; the token goes in the environment, never in an argument or in the output. What it returns is the text of the answer and nothing the caller can mistake for a command.
/// </summary>
public sealed class ClaudeCliGateway(AgentsOptions options) : IAgentGateway
{
    private const int MaxOutputCharacters = 64 * 1024;

    /// <summary>The program and its arguments for a request: no tools, no MCP server, no session kept, no skill, the system prompt replaced.</summary>
    public (string Program, IReadOnlyList<string> Arguments) Command(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = [];
        var wrapper = options.Wrapper;
        var program = wrapper.Length > 0 ? wrapper[0] : options.Command;
        if (wrapper.Length > 0)
        {
            arguments.AddRange(wrapper.Skip(1));
            arguments.Add(options.Command);
        }
        arguments.AddRange(["-p", "--model", request.Model, "--tools", string.Empty, "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands", "--setting-sources", string.Empty, "--system-prompt", request.SystemPrompt, request.UserPrompt]);
        return (program, arguments);
    }

    public async Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var (program, arguments) = Command(request);
        using var workspace = new TemporaryFolder();
        var start = new ProcessStartInfo(program) { WorkingDirectory = workspace.Path, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (options.TokenFile is { Length: > 0 } file)
        {
            // A token file that is named and not there is no answer: running under the login of whoever started the host would be running as someone else, in silence
            if (!File.Exists(file))
            {
                return new AgentReply(false, string.Empty, "the token file named in Agents:TokenFile does not exist");
            }
            start.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
        }
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AgentReply(false, string.Empty, "the agent program could not be started");
        }
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(request.Timeout);
        try
        {
            var output = Read(process.StandardOutput, limit.Token);
            var errors = Read(process.StandardError, limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var text = await output;
            _ = await errors;
            return process.ExitCode == 0 ? new AgentReply(true, text, null) : new AgentReply(false, string.Empty, $"the agent program ended with code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new AgentReply(false, string.Empty, cancellationToken.IsCancellationRequested ? "stopped" : "the agent did not answer in time");
        }
    }

    // A bounded read: an answer longer than the bound is cut, never kept whole
    private static async Task<string> Read(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length < MaxOutputCharacters)
            {
                text.Append(buffer, 0, Math.Min(read, MaxOutputCharacters - text.Length));
            }
        }
        return text.ToString();
    }
}

/// <summary>An empty folder for the run, removed after it: no `CLAUDE.md`, no project settings and nothing of the host's tree for the program to read.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("orchestrarr-agent-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
