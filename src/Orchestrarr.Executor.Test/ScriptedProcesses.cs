namespace Orchestrarr.Executor.Test;

internal sealed record Call(string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>A process runner that answers from a script instead of starting git or docker, and remembers every call.</summary>
internal sealed class ScriptedProcesses : IProcessRunner
{
    private readonly List<(Func<Call, bool> When, Queue<ProcessResult> Results)> _scripts = [];

    public List<Call> Calls { get; } = [];

    /// <summary>Answers every call that matches with this result.</summary>
    public void Script(Func<Call, bool> when, ProcessResult result) => _scripts.Insert(0, (when, new Queue<ProcessResult>([result])));

    /// <summary>Answers the calls that match one after the other with these results; the last one repeats.</summary>
    public void Sequence(Func<Call, bool> when, params ProcessResult[] results) => _scripts.Insert(0, (when, new Queue<ProcessResult>(results)));

    public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, int maxOutputCharacters, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var call = new Call(file, arguments, environment);
        Calls.Add(call);
        foreach (var (when, results) in _scripts)
        {
            if (when(call))
            {
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
