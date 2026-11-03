using System.Net;
using System.Text.Json;

namespace Orchestrarr.Core.WorkItems;

/// <summary>Why a source could not be read, as a code the interface words. The details stay in the server's log.</summary>
public enum SourceError
{
    /// <summary>Anything not listed below.</summary>
    Failed,

    /// <summary>The source cannot be reached (network, refused connection, server error).</summary>
    Unreachable,

    /// <summary>The source refused the credentials, or asks to sign in first.</summary>
    Unauthorized,

    /// <summary>The source did not answer in time.</summary>
    Timeout,

    /// <summary>The source answered with something that is not the expected report.</summary>
    InvalidResponse,
}

public static class SourceErrors
{
    public static SourceError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => SourceError.Unauthorized,
            // Not following redirects, a 3xx is a sign-in page in front of the source
            HttpRequestException { StatusCode: >= HttpStatusCode.Moved and < HttpStatusCode.BadRequest } => SourceError.Unauthorized,
            HttpRequestException => SourceError.Unreachable,
            TaskCanceledException => SourceError.Timeout,
            JsonException or InvalidOperationException => SourceError.InvalidResponse,
            _ => SourceError.Failed,
        };
    }
}
