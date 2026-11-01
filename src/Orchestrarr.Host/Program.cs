using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Providers.Portarr;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPortarrFleetSource(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton<WorkItemCatalog>();

var app = builder.Build();

var api = app.MapGroup("/api/v1");

// The decision cards waiting for the operator. Read-only in this vertical: nothing can be approved yet.
api.MapGet("/work-items", async (WorkItemCatalog catalog, bool? refresh, CancellationToken ct) =>
    Results.Ok(await catalog.GetAsync(refresh ?? false, ct)));

api.MapGet("/work-items/{**id}", async (WorkItemCatalog catalog, string id, CancellationToken ct) =>
    await catalog.FindAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

api.MapGet("/system/status", async (WorkItemCatalog catalog, CancellationToken ct) =>
{
    var report = await catalog.GetAsync(refresh: false, ct);
    return Results.Ok(new
    {
        name = "Orchestrarr",
        version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev",
        source = new { kind = catalog.SourceKind, configured = catalog.SourceKind != "none", readAt = report.ReadAt, error = report.Error, stale = report.Stale },
    });
});

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
