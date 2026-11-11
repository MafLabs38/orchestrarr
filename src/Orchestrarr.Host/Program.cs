using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Accounts;
using Orchestrarr.Host.Decisions;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Settings;
using Orchestrarr.Host.Storage;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
// What changes is pushed to the screens as it changes (ADR 0025): a WebSocket that says what to read again, never the data itself
builder.Services.AddSignalR();
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddSingleton<ILiveNotifier>(provider => provider.GetRequiredService<LiveBroadcaster>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<LiveBroadcaster>());
builder.Services.AddSingleton(provider => FleetSourceFactory.Create(builder.Configuration, builder.Environment.ContentRootPath, provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(provider => PullRequestSourceFactory.Create(builder.Configuration, builder.Environment.ContentRootPath, provider.GetRequiredService<TimeProvider>()));
// Approving: recording the operator's decision, with its trail and the global stop. Nothing here runs anything yet.
var approvals = builder.Configuration.GetSection(ApprovalsOptions.SectionName).Get<ApprovalsOptions>() ?? new ApprovalsOptions();
approvals.Validate();
// The powers the operator listed in the configuration (SPEC D-10, D-11): none unless a grant is written. Checked now, not at a request.
var grants = (builder.Configuration.GetSection(PowersOptions.SectionName).Get<PowersOptions>() ?? new PowersOptions()).ToGrants(builder.Environment.IsDevelopment());
// People sign in with the local accounts of the product (ADR 0018), the same way as in Portarr and Mnemarr. Checked now: a missing value stops the host.
var auth = (builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions()).Validate();
// Where the approvals, the trail and the global stop are kept: checked now, so that a file that cannot be used stops the host
var storage = (builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions()).Validate(builder.Environment.ContentRootPath, builder.Environment.IsDevelopment(), approving: true);
// The tasks and the executors (ADR 0017): the code of the second factor, how long an approval waits, the tokens of the executors. Checked now.
var tasks = (builder.Configuration.GetSection(TasksOptions.SectionName).Get<TasksOptions>() ?? new TasksOptions()).Validate(approvals, storage, builder.Environment.ContentRootPath);
if (builder.Environment.IsProduction() && !tasks.Options.RequireSecondFactor)
{
    throw new InvalidOperationException("Tasks:RequireSecondFactor=false is for tests: in production the approvals carry the code of the authenticator.");
}
// What the screen of options may lower, if anything: off unless the host is started with it, and only in Development and Testing (ADR 0023)
var optionsSettings = (builder.Configuration.GetSection(SecurityOptionsConfiguration.SectionName).Get<SecurityOptionsConfiguration>() ?? new SecurityOptionsConfiguration()).Validate(builder.Environment.EnvironmentName);
builder.Services.AddApprovals(approvals, grants, storage, tasks, optionsSettings);
// The accounts share the database of the approvals, so they come after it; the cookie's keys sit next to the database when there is one
builder.Services.AddAccounts(auth, storage);
builder.Services.AddOrchestrarrSecurity(auth, storage is SqliteStorage file ? Path.Combine(Path.GetDirectoryName(file.Path) ?? builder.Environment.ContentRootPath, "keys") : null);
builder.Services.AddSingleton(builder.Configuration.GetSection(WorkItemCatalogOptions.SectionName).Get<WorkItemCatalogOptions>() ?? new WorkItemCatalogOptions());
builder.Services.AddSingleton<WorkItemCatalog>();
// What may be done to each application, from the private configuration (ADR 0020): checked now, a wrong file stops the host
builder.Services.AddSingleton(_ => ApplicationCatalogFactory.Create(builder.Configuration, builder.Environment.ContentRootPath));

var app = builder.Build();
if (!storage.Persistent && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Storage:Provider is Memory: the accounts, the approvals, the audit trail and the global stop are lost at every restart. Use Sqlite.");
}
if (!auth.SecureCookie && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Auth:CookieSecurity is SameAsRequest: over plain HTTP the passwords and the session cookie cross the network in clear. Use Always behind HTTPS.");
}
app.UseSecurityHeaders(overHttps: auth.SecureCookie);
// The files of the interface say nothing about the fleet: they are served before the sign-in is asked for, or the page itself could not load
// (a file with an extension matches no route, and the fallback policy would refuse it)
if (app.Environment.WebRootFileProvider.GetDirectoryContents("/").Exists)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
// Everything the API says, reads included, is for a person who signed in: who approved what is not for anybody (the fallback policy)
app.UseOrchestrarrSecurity();

// A change made through the API (an approval, the stop, an option, an account) is told to every screen once it has succeeded
var live = app.Services.GetRequiredService<ILiveNotifier>();
app.UseMiddleware<LiveChangeMiddleware>();
app.Services.GetRequiredService<WorkItemCatalog>().Changed += () => live.Publish(LiveTopics.Problems);
app.Services.GetRequiredService<ExecutorRegistry>().Changed += () => live.Publish(LiveTopics.Status);

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapAccountEndpoints();
api.MapOptionsEndpoints();

// Approving is limited per person, so that a loop of refusals cannot fill the trail. After the sign-in, which tells who is asking.
app.UseRateLimiter();

// For a probe of the deployment: it says the host is up and nothing else
app.MapGet("/health", () => Results.Ok(new HealthStatus("ok"))).AllowAnonymous();

// The decision cards waiting for the operator, and the approvals recorded for the launcher. Approving records a decision; nothing runs.
api.MapGet("/work-items", async (WorkItemCatalog catalog, bool? refresh, CancellationToken ct) =>
    Results.Ok(await catalog.GetAsync(refresh ?? false, ct))).RequireAuthorization(Permissions.FleetRead);

api.MapGet("/work-items/{**id}", async (WorkItemCatalog catalog, string id, CancellationToken ct) =>
    await catalog.FindAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound()).RequireAuthorization(Permissions.FleetRead);

api.MapApprovalEndpoints();
// The WebSocket of the screens: for whoever may read the fleet
app.MapHub<LiveHub>(LiveHub.Path).RequireAuthorization(Permissions.FleetRead);
api.MapProblemsEndpoints();
api.MapRunEndpoints();
// The routes of the executors carry their own token and no session: they are mapped outside the group that needs a person
app.MapExecutorEndpoints(tasks);

api.MapGet("/system/status", (WorkItemCatalog catalog, IApproverAccessor accessor, IKillSwitchControl killSwitch, ISecondFactorPolicy secondFactor, ExecutorRegistry executors, HttpContext context) =>
{
    // The status never reads the fleet: it says what the last read found, and nothing before the first one
    var report = catalog.Peek();
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
    return Results.Ok(new SystemStatus("Orchestrarr", version, new SourceStatus(catalog.SourceKind, catalog.SourceConfigured, report?.ReadAt, report?.Error, report?.Stale ?? false), new ForgeStatus(catalog.ForgeKind, catalog.ForgeConfigured), ApprovalEndpoints.StatusOf(accessor, context, killSwitch, secondFactor.Required), new StorageStatus(storage.Kind), executors.Snapshot()));
}).RequireAuthorization(Permissions.FleetRead);

// An unknown route of the API is a 404 for a person who signed in (a 401 for anybody else: the fallback policy), never the page of the interface
api.MapFallback(() => Results.NotFound());

// The user interface, once built into wwwroot (in development the Vite server serves it)
if (app.Environment.WebRootFileProvider.GetDirectoryContents("/").Exists)
{
    // The routes of the interface (a path with no extension) get its page; the data is behind the sign-in
    app.MapFallbackToFile("index.html").AllowAnonymous();
}

await app.RunAsync();

/// <summary>Entry point, public for the integration tests.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1118:Utility classes should not have public constructors", Justification = "The generated entry point must stay instantiable: WebApplicationFactory<Program> in the integration tests.")]
public partial class Program;

namespace Orchestrarr.Host
{
    /// <summary>What <c>GET /api/v1/system/status</c> answers.</summary>
    public sealed record SystemStatus(string Name, string Version, SourceStatus Source, ForgeStatus Forge, ApprovalStatus Approvals, StorageStatus Storage, IReadOnlyList<ExecutorStatus> Executors);

    /// <summary>Where the approvals, the trail and the global stop are kept: <c>sqlite</c> survives a restart, <c>memory</c> does not.</summary>
    public sealed record StorageStatus(string Kind);

    /// <summary>What <c>GET /health</c> answers.</summary>
    public sealed record HealthStatus(string Status);

    /// <summary>The forge where pull requests are read: its kind, and whether one is connected.</summary>
    public sealed record ForgeStatus(string Kind, bool Configured);

    /// <summary>The source of findings: its kind, whether one is configured, and the state of the last read.</summary>
    public sealed record SourceStatus(string Kind, bool Configured, DateTimeOffset? ReadAt, SourceError? Error, bool Stale);
}
