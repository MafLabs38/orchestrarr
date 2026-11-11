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
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(127, false, $"{file}: not found or not executable");
        }
        process.StandardInput.Close();
        // The lines are cut as they are read, so that a program that prints without ever ending a line cannot fill the memory of the executor.
        // A line of standard output is passed on as it arrives: what an agent says is shown while it works.
        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, line =>
            {
                tail.Add(line);
                onLine?.Invoke(line);
            }),
            PumpAsync(process.StandardError, line => tail.Add(line)));

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
            catch (Exception ended) when (ended is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It ended between the timeout and the kill, or part of its tree could not be killed: the timeout is still what happened
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Drained(pumps);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, true, tail.ToString());
        }
        // The streams are drained once the process is gone: a last line may still be on its way
        await process.WaitForExitAsync(CancellationToken.None);
        await Drained(pumps);
        return new ProcessResult(process.ExitCode, false, tail.ToString());
    }

    // A grandchild that keeps the pipe open must not keep the executor waiting
    private static async Task Drained(Task pumps)
    {
        try
        {
            await pumps.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // What was read is kept; the rest is not waited for
        }
    }

    /// <summary>The longest line kept whole: what comes after it, up to the end of the line, is dropped and a marker says so.</summary>
    internal const int MaxLineCharacters = 65536;

    private static string Finished(StringBuilder line, bool cut) => cut ? line.Append(" [line cut]").ToString() : line.ToString();

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var dropping = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            foreach (var character in buffer.AsSpan(0, read))
            {
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }
                    onLine(Finished(line, dropping));
                    line.Clear();
                    dropping = false;
                }
                else if (line.Length < MaxLineCharacters)
                {
                    line.Append(character);
                }
                else
                {
                    dropping = true;
                }
            }
        }
        if (line.Length > 0)
        {
            onLine(Finished(line, dropping));
        }
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
