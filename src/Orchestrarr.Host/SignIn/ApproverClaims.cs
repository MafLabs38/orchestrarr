using System.Globalization;
using System.Security.Claims;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.SignIn;

/// <summary>Who a signed-in person is for the approvals: read from the claims the identity provider put in the session, and from nothing else.</summary>
internal static class ApproverClaims
{
    public const string Subject = "sub";
    public const string AuthenticationContext = "acr";
    public const string AuthenticationTime = "auth_time";
    public const string PreferredName = "preferred_username";
    public const string FullName = "name";

    private const int DisplayNameLength = 100;

    /// <summary>
    /// The approver of this session, or null when nobody is signed in or the identity is not one the order can carry. The subject is the
    /// identifier the identity provider gives, which does not change when the person is renamed.
    /// </summary>
    public static Approver? From(ClaimsPrincipal user, SignInOptions options)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        var subject = user.FindFirst(Subject)?.Value;
        if (!OrderValidation.IsValidSubject(subject))
        {
            return null;
        }
        // The authorization code flow always has a person behind it: a service account cannot take part, so it cannot be an operator (D-47).
        return new Approver(subject!, IsPerson: true, IsOperator(user, options), StepUpAt(user, options));
    }

    /// <summary>A name to show, never to decide on: it can be changed by the person or by an administrator.</summary>
    public static string? DisplayName(ClaimsPrincipal user)
    {
        var name = user.FindFirst(PreferredName)?.Value ?? user.FindFirst(FullName)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        return name.Length > DisplayNameLength ? name[..DisplayNameLength] : name;
    }

    // A group is written with or without its path ("/orchestrarr-operators"): both name the same group
    private static bool IsOperator(ClaimsPrincipal user, SignInOptions options) =>
        user.FindAll(options.GroupsClaim).SelectMany(Groups).Any(group => string.Equals(group.TrimStart('/'), options.OperatorGroup.TrimStart('/'), StringComparison.Ordinal));

    // A claim holds one group, or a list written as a JSON array when the provider sends them in one claim
    private static IEnumerable<string> Groups(Claim claim)
    {
        if (claim.ValueType != "JSON_ARRAY" && !claim.Value.StartsWith('['))
        {
            return [claim.Value];
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(claim.Value) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // The moment of the second factor: when the session was opened at the level the configuration calls a step-up
    private static DateTimeOffset? StepUpAt(ClaimsPrincipal user, SignInOptions options) =>
        options.StepUpAcr is { Length: > 0 } acr
        && user.FindFirst(AuthenticationContext)?.Value == acr
        && long.TryParse(user.FindFirst(AuthenticationTime)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
