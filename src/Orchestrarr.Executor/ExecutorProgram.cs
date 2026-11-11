using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>
/// The commands of the executor:
/// <c>run</c> (the default), <c>enroll-operator --subject S [--replace]</c>, <c>operators</c>,
/// <c>delegations list</c>, <c>delegations add --subject S --kind K --repository R [--days 30] [--quota 5]</c>, <c>delegations revoke --subject S --id ID</c>.
/// </summary>
public static class ExecutorProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("executor.json", optional: true)
            .AddEnvironmentVariables("ORCHESTRARR_")
            .Build();
        var options = configuration.GetSection(ExecutorOptions.SectionName).Get<ExecutorOptions>() ?? new ExecutorOptions();
        var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";
        var named = Named(args);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var io = new TerminalIo();

        try
        {
            return command switch
            {
                "enroll-operator" => await Commands(options, io).EnrolAsync(Required(named, "subject"), named.ContainsKey("replace"), cancel.Token),
                "operators" => Commands(options, io).ListOperators(),
                "acts" => Commands(options, io).ListActs(),
                "delegations" => await DelegationsAsync(args, named, Commands(options, io), io, cancel.Token),
                "run" => await HostAsync(options, cancel.Token),
                _ => Fail(io, "Unknown command. Known: run, enroll-operator, operators, acts, delegations."),
            };
        }
        catch (Exception failure) when (failure is ArgumentException or FormatException or InvalidOperationException)
        {
            return Fail(io, failure.Message);
        }
    }

    private static int Fail(IConsoleIo io, string message)
    {
        io.WriteLine(message);
        return 2;
    }

    // One definition for the enrolment and for the check, so that the parameters of the codes cannot drift apart
    private static SecondFactorOptions SecondFactor() => new();

    private static OperatorCommands Commands(ExecutorOptions options, IConsoleIo io)
    {
        var store = new ExecutorStore(options.DataPath);
        var settings = SecondFactor();
        var guard = new SecondFactorGuard(store, store, TimeProvider.System, settings);
        return new OperatorCommands(store, guard, settings.Totp, options.Issuer, TimeProvider.System, io);
    }

    private static async Task<int> DelegationsAsync(string[] args, Dictionary<string, string> named, OperatorCommands commands, IConsoleIo io, CancellationToken cancellationToken)
    {
        var verb = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "list";
        switch (verb)
        {
            case "list":
                return commands.ListDelegations();
            case "add":
                if (!Enum.TryParse<TaskKind>(Required(named, "kind"), ignoreCase: true, out var kind))
                {
                    return Fail(io, "Unknown kind. Known: " + string.Join(", ", Enum.GetNames<TaskKind>()));
                }
                return await commands.AddDelegationAsync(
                    Required(named, "subject"),
                    kind,
                    Required(named, "repository"),
                    int.Parse(named.GetValueOrDefault("days", "30"), CultureInfo.InvariantCulture),
                    int.Parse(named.GetValueOrDefault("quota", "5"), CultureInfo.InvariantCulture),
                    cancellationToken);
            case "revoke":
                return await commands.RevokeDelegationAsync(Required(named, "subject"), Guid.Parse(Required(named, "id")), cancellationToken);
            default:
                return Fail(io, "delegations list | add | revoke");
        }
    }

    private static async Task<int> HostAsync(ExecutorOptions options, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new ExecutorStore(options.DataPath));
        builder.Services.AddSingleton(provider => new SecondFactorGuard(provider.GetRequiredService<ExecutorStore>(), provider.GetRequiredService<ExecutorStore>(), provider.GetRequiredService<TimeProvider>(), SecondFactor()));
        // With no journal configured the double control has nothing to read, so every batch is refused: that is the safe state
        builder.Services.AddSingleton<IApprovalJournalReader>(new FileApprovalJournal(options.JournalPath.Length > 0 ? options.JournalPath : Path.Combine(AppContext.BaseDirectory, "no-journal.jsonl")));
        // Validating runs for real when profiles are configured; merging stays a dry run (no write power yet)
        ITaskRunner validate = options.Validation.IsConfigured ? new ValidatePullRequestRunner(options.Validation, new SystemProcessRunner()) : new DryRunTaskRunner(TaskKind.ValidatePullRequest);
        builder.Services.AddSingleton<IEnumerable<ITaskRunner>>([validate, new DryRunTaskRunner(TaskKind.MergePullRequest)]);
        builder.Services.AddSingleton(provider => new ExecutorPipeline(
            provider.GetRequiredService<ExecutorStore>(),
            provider.GetRequiredService<SecondFactorGuard>(),
            provider.GetRequiredService<IApprovalJournalReader>(),
            options.Rights,
            provider.GetRequiredService<IEnumerable<ITaskRunner>>(),
            provider.GetRequiredService<TimeProvider>(),
            options.Name,
            options.RequireSecondFactor));
        builder.Services.AddSingleton<IControlPlane>(_ => new ControlPlaneClient(new HttpClient(), options.ControlPlane));
        builder.Services.AddHostedService<ExecutorWorker>();
        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }

    private static string Required(Dictionary<string, string> named, string name) =>
        named.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");

    internal static Dictionary<string, string> Named(string[] arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < arguments.Length)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
                continue;
            }
            var name = arguments[index][2..];
            var hasValue = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal);
            result[name] = hasValue ? arguments[index + 1] : string.Empty;
            index += hasValue ? 2 : 1;
        }
        return result;
    }
}

internal sealed class TerminalIo : IConsoleIo
{
    public void WriteLine(string text) => Console.WriteLine(text);

    public string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var typed = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && typed.Length > 0)
            {
                typed.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return typed.ToString();
    }
}
