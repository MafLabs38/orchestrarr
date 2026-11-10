namespace Orchestrarr.Executor;

internal static class EntryPoint
{
    private static Task<int> Main(string[] args) => ExecutorProgram.RunAsync(args);
}
