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
using Orchestrarr.Host.SignIn;
using Orchestrarr.Host.Storage;

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
approvals.Validate();
SandboxGuard.Check(approvals, builder.Environment, builder.Configuration);
// The powers the operator listed in the configuration (SPEC D-10, D-11): none unless a grant is written. Checked now, not at a request.
var grants = (builder.Configuration.GetSection(PowersOptions.SectionName).Get<PowersOptions>() ?? new PowersOptions()).ToGrants(builder.Environment.IsDevelopment());
// Signing in through the identity provider, when that is how operators are known (SPEC D-40, D-54). Checked now: a missing value stops the host.
SignInSettings? signIn = null;
if (approvals.Mode == ApprovalsMode.OpenIdConnect)
{
    signIn = (builder.Configuration.GetSection(SignInOptions.SectionName).Get<SignInOptions>() ?? new SignInOptions()).Validate(allowCleartextOnPrivateNetwork: builder.Environment.IsDevelopment());
    builder.Services.AddSignIn(signIn);
}
// Where the approvals, the trail and the global stop are kept: checked now, so that a file that cannot be used stops the host
var storage = (builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions()).Validate(builder.Environment.ContentRootPath, builder.Environment.IsDevelopment(), approving: approvals.Mode != ApprovalsMode.Disabled);
builder.Services.AddApprovals(approvals, grants, storage);
builder.Services.AddSingleton(builder.Configuration.GetSection(WorkItemCatalogOptions.SectionName).Get<WorkItemCatalogOptions>() ?? new WorkItemCatalogOptions());
builder.Services.AddSingleton<WorkItemCatalog>();

var app = builder.Build();
if (!storage.Persistent && approvals.Mode == ApprovalsMode.OpenIdConnect && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Storage:Provider is Memory: the approvals, the audit trail and the global stop are lost at every restart. Use Sqlite.");
}
SandboxGuard.StopIfListeningBeyondLoopback(app, approvals);

app.UseSecurityHeaders(overHttps: signIn?.Secure == true);

var api = app.MapGroup("/api/v1");
if (signIn is not null)
{
    // Everything the API says, reads included, is for a person who signed in: who approved what is not for anybody
    app.ConfigureSignIn(signIn, api);
}

// Approving is limited per person, so that a loop of refusals cannot fill the trail. After the sign-in, which tells who is asking.
app.UseRateLimiter();

// For a probe of the deployment: it says the host is up and nothing else
app.MapGet("/health", () => Results.Ok(new HealthStatus("ok")));

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
    return Results.Ok(new SystemStatus("Orchestrarr", version, new SourceStatus(catalog.SourceKind, catalog.SourceConfigured, report?.ReadAt, report?.Error, report?.Stale ?? false), new ForgeStatus(catalog.ForgeKind, catalog.ForgeConfigured), ApprovalEndpoints.StatusOf(accessor, context, killSwitch), new StorageStatus(storage.Kind)));
});

// An unknown route of the API, or of the sign-in (which is not there at all unless people sign in), is a 404, never the page of the interface
api.MapFallback(() => Results.NotFound());
app.MapFallback("/auth/{**rest}", () => Results.NotFound());

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
    public sealed record SystemStatus(string Name, string Version, SourceStatus Source, ForgeStatus Forge, ApprovalStatus Approvals, StorageStatus Storage);

    /// <summary>Where the approvals, the trail and the global stop are kept: <c>sqlite</c> survives a restart, <c>memory</c> does not.</summary>
    public sealed record StorageStatus(string Kind);

    /// <summary>What <c>GET /health</c> answers.</summary>
    public sealed record HealthStatus(string Status);

    /// <summary>The forge where pull requests are read: its kind, and whether one is connected.</summary>
    public sealed record ForgeStatus(string Kind, bool Configured);

    /// <summary>The source of findings: its kind, whether one is configured, and the state of the last read.</summary>
    public sealed record SourceStatus(string Kind, bool Configured, DateTimeOffset? ReadAt, SourceError? Error, bool Stale);
}
