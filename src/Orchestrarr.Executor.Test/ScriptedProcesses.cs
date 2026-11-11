namespace Orchestrarr.Executor.Test;

internal sealed record Call(string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string? WorkingDirectory = null)
{
    /// <summary>The folder a docker run mounts as /work.</summary>
    public string? MountedWork => Arguments.SkipWhile(argument => argument != "-v").Skip(1).FirstOrDefault()?.Split(':')[0];

    /// <summary>The value after a flag, for example <c>--git-dir</c>.</summary>
    public string? After(string flag) => Arguments.SkipWhile(argument => argument != flag).Skip(1).FirstOrDefault();
}

/// <summary>A process runner that answers from a script instead of starting git or docker, remembers every call and can do what the real program would do to the disk.</summary>
internal sealed class ScriptedProcesses : IProcessRunner
{
    private readonly List<(Func<Call, bool> When, Queue<ProcessResult> Results, Action<Call>? Effect)> _scripts = [];

    private readonly List<Func<Call, bool>> _hangs = [];

    public List<Call> Calls { get; } = [];

    /// <summary>A call that matches never ends by itself: it waits until it is cancelled, as a hanging container would.</summary>
    public void Hang(Func<Call, bool> when) => _hangs.Add(when);

    /// <summary>Answers every call that matches with this result, after doing <paramref name="effect"/> (what the program would write).</summary>
    public void Script(Func<Call, bool> when, ProcessResult result, Action<Call>? effect = null) => _scripts.Insert(0, (when, new Queue<ProcessResult>([result]), effect));

    /// <summary>Answers the calls that match one after the other with these results; the last one repeats.</summary>
    public void Sequence(Func<Call, bool> when, params ProcessResult[] results) => _scripts.Insert(0, (when, new Queue<ProcessResult>(results), null));

    private static async Task<ProcessResult> WaitForCancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new ProcessResult(0, false, string.Empty);
    }

    public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, int maxOutputCharacters, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var call = new Call(file, arguments, environment, workingDirectory);
        Calls.Add(call);
        if (_hangs.Exists(hang => hang(call)))
        {
            return WaitForCancellation(cancellationToken);
        }
        foreach (var (when, results, effect) in _scripts)
        {
            if (when(call))
            {
                effect?.Invoke(call);
                var answer = results.Count > 1 ? results.Dequeue() : results.Peek();
                // The lines of the output reach the listener as they would from a real process
                foreach (var line in answer.OutputTail.Split('\n'))
                {
                    onLine?.Invoke(line);
                }
                return Task.FromResult(answer);
            }
        }
        // By default: a fetch that works, at the approved commit, and a container that passes
        return Task.FromResult(arguments.Contains("rev-parse") ? new ProcessResult(0, false, ExecutorFixture.Head) : new ProcessResult(0, false, "ok"));
    }
}
