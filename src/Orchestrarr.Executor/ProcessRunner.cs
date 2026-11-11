using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrarr.Executor;

/// <summary>How a process ended: its exit code, whether it was stopped for taking too long, and the end of what it printed (both streams).</summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, string OutputTail);

/// <summary>Starts a program with arguments (never through a shell). A seam so that the runners are tested without git or docker.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        int maxOutputCharacters,
        CancellationToken cancellationToken,
        Action<string>? onLine = null);
}

public sealed partial class SystemProcessRunner : IProcessRunner
{
    [GeneratedRegex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Ansi();

    public async Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        int maxOutputCharacters,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        var tail = new TailBuffer(maxOutputCharacters);
        process.OutputDataReceived += (_, data) =>
        {
            tail.Add(data.Data);
            if (data.Data is not null)
            {
                // A line of standard output as it arrives: what an agent says is shown while it works
                onLine?.Invoke(data.Data);
            }
        };
        process.ErrorDataReceived += (_, data) => tail.Add(data.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(127, false, $"{file}: not found or not executable");
        }
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended between the timeout and the kill: the timeout is still what happened
            }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, true, tail.ToString());
        }
        // The streams are drained once the process is gone: a last line may still be on its way
        await process.WaitForExitAsync(CancellationToken.None);
        return new ProcessResult(process.ExitCode, false, tail.ToString());
    }

    /// <summary>The last characters of what a command printed, without the colour codes of a terminal. A command that prints a lot costs nothing but a moving window.</summary>
    private sealed class TailBuffer(int capacity)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();

        public void Add(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (_gate)
            {
                _text.Append(Ansi().Replace(line, string.Empty)).Append('\n');
                if (_text.Length > capacity * 2)
                {
                    _text.Remove(0, _text.Length - capacity);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var all = _text.ToString().TrimEnd();
                return all.Length > capacity ? all[^capacity..] : all;
            }
        }
    }
}
