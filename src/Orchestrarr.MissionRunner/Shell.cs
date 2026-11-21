using System.Diagnostics;
using System.Text;

namespace Orchestrarr.MissionRunner;

public sealed record ShellResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Runs a program and gives back what it wrote. A fake in the tests: nothing else in the runner starts a process.</summary>
public interface IShell
{
    Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessShell : IShell
{
    private const int MaxOutputCharacters = 200_000;

    public async Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Collect(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (output)
            {
                if (output.Length < MaxOutputCharacters)
                {
                    output.AppendLine(line);
                }
            }
        }
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            lock (output)
            {
                return new ShellResult(-1, output.ToString(), TimedOut: true);
            }
        }
        lock (output)
        {
            return new ShellResult(process.ExitCode, output.ToString(), TimedOut: false);
        }
    }
}
