using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orchestrarr.MissionRunner;

/// <summary>The runner of missions: settings in <c>mission-runner.json</c> or <c>ORCHESTRARR_MissionRunner__…</c> variables. It serves one world and connects out only.</summary>
public static class MissionRunnerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("mission-runner.json", optional: true)
            .AddEnvironmentVariables("ORCHESTRARR_")
            .AddCommandLine(args)
            .Build();
        var options = configuration.GetSection(MissionRunnerOptions.SectionName).Get<MissionRunnerOptions>() ?? new MissionRunnerOptions();
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException failure)
        {
            await Console.Error.WriteLineAsync(failure.Message);
            return 2;
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IShell, ProcessShell>();
        builder.Services.AddSingleton<RepairAgent>();
        builder.Services.AddSingleton<IWorldWork, StagingProof>();
        builder.Services.AddSingleton<IWorldWork, GitWorld>();
        builder.Services.AddSingleton<IWorldWork, ReleaseWorld>();
        builder.Services.AddSingleton<IMissionControlPlane>(_ => new MissionControlPlane(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), options));
        builder.Services.AddHostedService<MissionRunnerWorker>();
        await builder.Build().RunAsync();
        return 0;
    }
}
