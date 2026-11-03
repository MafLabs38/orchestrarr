using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host;
using Orchestrarr.Host.Approvals;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => FleetSourceFactory.Create(builder.Configuration, builder.Environment.ContentRootPath, provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(provider => PullRequestSourceFactory.Create(builder.Configuration, builder.Environment.ContentRootPath, provider.GetRequiredService<TimeProvider>()));
// Approving: recording the operator's decision, with its trail and the global stop. Nothing here runs anything yet.
var approvals = builder.Configuration.GetSection(ApprovalsOptions.SectionName).Get<ApprovalsOptions>() ?? new ApprovalsOptions();
SandboxGuard.Check(approvals, builder.Environment, builder.Configuration);
builder.Services.AddSingleton(approvals);

// The powers the operator listed in the configuration (SPEC D-10, D-11): none unless a grant is written. Checked now, not at a request.
var grants = (builder.Configuration.GetSection(PowersOptions.SectionName).Get<PowersOptions>() ?? new PowersOptions()).ToGrants(builder.Environment.IsDevelopment());
builder.Services.AddSingleton<IPowerPolicy>(new ConfiguredPowerPolicy(grants));
builder.Services.AddSingleton<IGrantedPowers, PolicyGrantedPowers>();

builder.Services.AddSingleton<IApproverAccessor>(provider => approvals.Mode == ApprovalsMode.Development
    ? new DevelopmentApprover(approvals, provider.GetRequiredService<TimeProvider>())
    : new NoApprover());
builder.Services.AddSingleton<IApprovalStore>(new InMemoryApprovalStore(approvals.QueueCapacity));
builder.Services.AddSingleton<InMemoryKillSwitch>();
builder.Services.AddSingleton<IKillSwitchControl>(provider => provider.GetRequiredService<InMemoryKillSwitch>());
builder.Services.AddSingleton<IKillSwitch>(provider => provider.GetRequiredService<InMemoryKillSwitch>());
builder.Services.AddSingleton<InMemoryAuditLog>(new InMemoryAuditLog(approvals.AuditCapacity));
builder.Services.AddSingleton<IAuditLog>(provider => provider.GetRequiredService<InMemoryAuditLog>());
builder.Services.AddSingleton<IAuditSink>(provider => provider.GetRequiredService<InMemoryAuditLog>());
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton(builder.Configuration.GetSection(WorkItemCatalogOptions.SectionName).Get<WorkItemCatalogOptions>() ?? new WorkItemCatalogOptions());
builder.Services.AddSingleton<WorkItemCatalog>();

var app = builder.Build();

var api = app.MapGroup("/api/v1");

// The decision cards waiting for the operator, and the approvals recorded for the launcher. Approving records a decision; nothing runs.
api.MapGet("/work-items", async (WorkItemCatalog catalog, bool? refresh, CancellationToken ct) =>
    Results.Ok(await catalog.GetAsync(refresh ?? false, ct)));

api.MapGet("/work-items/{**id}", async (WorkItemCatalog catalog, string id, CancellationToken ct) =>
    await catalog.FindAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

api.MapApprovalEndpoints();

api.MapGet("/system/status", (WorkItemCatalog catalog, IApproverAccessor accessor, IKillSwitchControl killSwitch, HttpContext context) =>
{
    // The status never reads the fleet: it says what the last read found, and nothing before the first one
    var report = catalog.Peek();
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
    return Results.Ok(new SystemStatus("Orchestrarr", version, new SourceStatus(catalog.SourceKind, catalog.SourceConfigured, report?.ReadAt, report?.Error, report?.Stale ?? false), new ForgeStatus(catalog.ForgeKind, catalog.ForgeConfigured), ApprovalEndpoints.StatusOf(accessor, context, killSwitch)));
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1118:Utility classes should not have public constructors", Justification = "The generated entry point must stay instantiable: WebApplicationFactory<Program> in the integration tests.")]
public partial class Program;

namespace Orchestrarr.Host
{
    /// <summary>What <c>GET /api/v1/system/status</c> answers.</summary>
    public sealed record SystemStatus(string Name, string Version, SourceStatus Source, ForgeStatus Forge, ApprovalStatus Approvals);

    /// <summary>The forge where pull requests are read: its kind, and whether one is connected.</summary>
    public sealed record ForgeStatus(string Kind, bool Configured);

    /// <summary>The source of findings: its kind, whether one is configured, and the state of the last read.</summary>
    public sealed record SourceStatus(string Kind, bool Configured, DateTimeOffset? ReadAt, SourceError? Error, bool Stale);
}
