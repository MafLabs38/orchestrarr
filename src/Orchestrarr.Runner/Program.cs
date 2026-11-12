namespace Orchestrarr.Runner;

internal static class EntryPoint
{
    private static Task<int> Main(string[] args) => RunnerProgram.RunAsync(args);
}
