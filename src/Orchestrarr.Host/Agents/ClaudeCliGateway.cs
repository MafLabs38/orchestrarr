using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Agents;

/// <summary>The settings of the agents (ADR 0033): the program that runs the model, and what wraps it where it cannot start directly.</summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Agents";

    /// <summary>The program: Claude Code, run non-interactively under the maintainer's subscription.</summary>
    public string Command { get; set; } = "claude";

    /// <summary>A prefix of arguments, for a host where the program refuses to start inside another session (for example <c>systemd-run --user --wait --pipe --quiet --collect --same-dir --setenv=PATH --setenv=HOME --setenv=LANG --setenv=CLAUDE_CODE_OAUTH_TOKEN</c>). **A wrapper that starts the program somewhere else (a unit of the user manager) must carry what the gateway sets: the working directory (<c>--same-dir</c>) and each variable by name (<c>--setenv=NAME</c>, the value stays out of the command line)**; otherwise the program runs in a home folder with the manager's environment and not under the token it was given. A time limit or a stop kills the wrapper, so bound the unit too (<c>--property=RuntimeMaxSec</c>). Empty: none.</summary>
    public string[] Wrapper { get; set; } = [];

    /// <summary>The model of the controller. A setting, never a constant of the code (the model of each role is configuration).</summary>
    public string ControllerModel { get; set; } = "sonnet";

    public int ControllerTimeoutSeconds { get; set; } = 180;

    /// <summary>At most this many questions to the lead agent at once: each is a run of the subscription.</summary>
    public int LeadMaxConcurrent { get; set; } = 2;

    /// <summary>The most rows of one selection that the lead is asked about: each is a run of the subscription, and the validation waits for the answers.</summary>
    public int LeadMaxRows { get; set; } = 10;

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
public sealed class ClaudeCliGateway(AgentsOptions options, ILogger<ClaudeCliGateway>? logger = null) : IAgentGateway
{
    private const int MaxLoggedStderr = 500;
    private readonly ILogger<ClaudeCliGateway> _logger = logger ?? NullLogger<ClaudeCliGateway>.Instance;

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
        arguments.AddRange(["-p", "--model", request.Model, "--tools", string.Empty, "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands", "--setting-sources", string.Empty, "--system-prompt", request.SystemPrompt]);
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
        // The program gets nothing of the host's environment (its connection strings, its tokens, a proxy that would redirect the prompt): only what it needs to run, and its own token
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        // With a token file the program signs in with it and sees only its own folder; without one it uses the login of the user that runs the host (kept in that user's home), as documented
        start.Environment["HOME"] = options.TokenFile is { Length: > 0 } ? workspace.Path : Environment.GetEnvironmentVariable("HOME") ?? workspace.Path;
        start.Environment["LANG"] = "C.UTF-8";
        if (options.TokenFile is { Length: > 0 } file)
        {
            // A token file that is named and not there is no answer: running under the login of whoever started the host would be running as someone else, in silence
            if (!File.Exists(file))
            {
                return new AgentReply(false, string.Empty, "the token file named in Agents:TokenFile does not exist");
            }
            try
            {
                start.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A token file that cannot be read is no answer either, and the reason never carries the path's content
                return new AgentReply(false, string.Empty, "the token file named in Agents:TokenFile cannot be read");
            }
        }
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Whatever the platform says (a program that is not executable, a wrapper that is not there), the answer is one: it could not be started, and the reason never carries the text of the failure;
            // the operator reads why in the log (the token travels in the environment and is never part of an exception)
            _logger.LogWarning(failure, "The agent program could not be started");
            return new AgentReply(false, string.Empty, "the agent program could not be started");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(request.Timeout);
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            output = Read(process.StandardOutput, limit.Token);
            errors = Read(process.StandardError, limit.Token);
            await Feed(process.StandardInput, request.UserPrompt, limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var text = await output;
            var diagnostics = await errors;
            if (process.ExitCode == 0)
            {
                return new AgentReply(true, text, null);
            }
            // The reply keeps only the code; the log keeps the end of what the program wrote on its error output, with the token taken out and bounded, so that an expired token or a wrong wrapper can be found
            _logger.LogWarning("The agent program ended with code {ExitCode}", process.ExitCode);
            _logger.LogDebug("The end of the agent program's error output: {Tail}", Tail(diagnostics, start.Environment.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var token) ? token : null));
            return new AgentReply(false, string.Empty, $"the agent program ended with code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            await Stop(process);
            await Observe(output, errors);
            return new AgentReply(false, string.Empty, cancellationToken.IsCancellationRequested ? "stopped" : "the agent did not answer in time");
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Whatever breaks while the program runs, the program is stopped (a handle that is disposed does not kill it) and the answer is one reply, with no text of the failure
            await Stop(process);
            await Observe(output, errors);
            return new AgentReply(false, string.Empty, "the agent program failed while it ran");
        }
    }

    // The last characters of a text, with the secret taken out whatever it is
    private static string Tail(string? text, string? secret)
    {
        var clean = string.IsNullOrEmpty(secret) || text is null ? text ?? string.Empty : text.Replace(secret, "***", StringComparison.Ordinal);
        return clean.Length <= MaxLoggedStderr ? clean : clean[^MaxLoggedStderr..];
    }

    // The readers are cancelled with the limit: they are waited for, so that none faults unobserved once the process and the limit are disposed
    private static async Task Observe(params Task<string>?[] reads)
    {
        foreach (var read in reads.OfType<Task<string>>())
        {
            try
            {
                await read;
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The program was stopped: what it said is not wanted
            }
        }
    }

    // The program is killed with its tree and waited for (for a short time) before its folder is removed: it may already be gone, and the answer must still be "did not answer in time"
    private static async Task Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended between the timeout and the kill
        }
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            // It does not leave: the folder below is removed all the same
        }
    }

    // The prompt goes on standard input: an argument is limited in length and visible to every process of the machine. A program that ended before it read it speaks through its exit code.
    private static async Task Feed(StreamWriter input, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            await input.WriteAsync(prompt.AsMemory(), cancellationToken);
            await input.FlushAsync(cancellationToken);
            input.Close();
        }
        catch (IOException)
        {
            // The pipe is closed: the program is already gone
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

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A program that has not let go of its folder yet: it is a temporary folder, the system removes it later
        }
    }
}
