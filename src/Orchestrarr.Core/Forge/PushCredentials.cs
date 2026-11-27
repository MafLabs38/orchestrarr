namespace Orchestrarr.Core.Forge;

/// <summary>A credential that pushes branches of one repository, and nothing else, until it expires (ADR 0057). The token is never written to a log, an audit entry or a dossier.</summary>
public sealed record PushCredential(string Token, DateTimeOffset ExpiresAt, IReadOnlyList<string> Permissions);

/// <summary>
/// Who makes a push credential on the forge (ADR 0057): for one repository, short-lived, with the permission to change the pipeline files only when it is asked for. The control plane asks, after it checked that the operation and its
/// approved frame justify it; a runner never holds what makes them. Null: no credential can be made now.
/// </summary>
public interface IPushCredentials
{
    Task<PushCredential?> IssueAsync(string repositoryFullName, bool pipelines, CancellationToken cancellationToken);

    /// <summary>Makes a credential unusable on the forge at once, before it expires (the agent finished, the control plane refused, the stop is engaged). True when the forge says it is revoked, or that it was no longer valid.</summary>
    Task<bool> RevokeAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Used when nothing makes push credentials: a free agent cannot push, and its problem is refused by the runner.</summary>
public sealed class NoPushCredentials : IPushCredentials
{
    public Task<PushCredential?> IssueAsync(string repositoryFullName, bool pipelines, CancellationToken cancellationToken) => Task.FromResult<PushCredential?>(null);

    public Task<bool> RevokeAsync(string token, CancellationToken cancellationToken) => Task.FromResult(true);
}
