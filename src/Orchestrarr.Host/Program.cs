using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => FleetSourceFactory.Create(builder.Configuration, builder.Environment.ContentRootPath, provider.GetRequiredService<TimeProvider>()));
// No power is granted yet (SPEC D-10, D-11): the policy that will read the operator's grants replaces this one
builder.Services.AddSingleton<IPowerPolicy, NoPowers>();
builder.Services.AddSingleton(builder.Configuration.GetSection(WorkItemCatalogOptions.SectionName).Get<WorkItemCatalogOptions>() ?? new WorkItemCatalogOptions());
builder.Services.AddSingleton<WorkItemCatalog>();

var app = builder.Build();

var api = app.MapGroup("/api/v1");

// The decision cards waiting for the operator. Read-only in this vertical: every route is a GET, and nothing can be approved yet.
api.MapGet("/work-items", async (WorkItemCatalog catalog, bool? refresh, CancellationToken ct) =>
    Results.Ok(await catalog.GetAsync(refresh ?? false, ct)));

api.MapGet("/work-items/{**id}", async (WorkItemCatalog catalog, string id, CancellationToken ct) =>
    await catalog.FindAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

api.MapGet("/system/status", async (WorkItemCatalog catalog, CancellationToken ct) =>
{
    var report = await catalog.GetAsync(refresh: false, ct);
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
    return Results.Ok(new SystemStatus("Orchestrarr", version, new SourceStatus(catalog.SourceKind, catalog.SourceConfigured, report.ReadAt, report.Error, report.Stale)));
});

// An unknown route of the API is a 404, never the page of the interface
api.MapFallback(() => Results.NotFound());

// The user interface, once built into wwwroot (in development the Vite server serves it)
if (app.Environment.WebRootFileProvider.GetDirectoryContents("/").Exists)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

await app.RunAsync();

/// <summary>Entry point, public for the integration tests.</summary>
public partial class Program;

namespace Orchestrarr.Host
{
    /// <summary>What <c>GET /api/v1/system/status</c> answers.</summary>
    public sealed record SystemStatus(string Name, string Version, SourceStatus Source);

    /// <summary>The source of findings: its kind, whether one is configured, and the state of the last read.</summary>
    public sealed record SourceStatus(string Kind, bool Configured, DateTimeOffset? ReadAt, SourceError? Error, bool Stale);
}
