using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.SignIn;

/// <summary>Who a signed-in person is for the approvals: read from the claims the identity provider put in the session, and from nothing else.</summary>
internal static class ApproverClaims
{
    private const int DisplayNameLength = 100;

    // The largest second DateTimeOffset.FromUnixTimeSeconds accepts (year 9999): beyond it the moment is not a moment
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>
    /// The approver of this session, or null when nobody is signed in or the identity is not one the order can carry. The subject is the
    /// identifier the identity provider gives, which does not change when the person is renamed.
    /// </summary>
    public static Approver? From(ClaimsPrincipal user, SignInSettings settings)
    {
        if (user.Identity?.IsAuthenticated != true || user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is not { } subject || !OrderValidation.IsValidSubject(subject))
        {
            return null;
        }
        // The authorization code flow always has a person behind it: a service account cannot take part, so it cannot be an operator (D-47).
        return new Approver(subject, IsPerson: true, IsMember(user, settings.GroupsClaim, settings.OperatorGroup), StepUpAt(user, settings));
    }

    /// <summary>
    /// A name to show, never to decide on: it can be changed by the person or by an administrator. The username, or else the full name.
    /// </summary>
    public static string? DisplayName(ClaimsPrincipal user)
    {
        var name = new[] { "preferred_username", "name" }.Select(type => user.FindFirst(type)?.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (name is null)
        {
            return null;
        }
        return name.Length > DisplayNameLength ? name[..DisplayNameLength] : name;
    }

    /// <summary>Whether the person belongs to the group, as the claim writes it: a group is compared exactly, so that a group of the same name elsewhere in a tree does not count.</summary>
    public static bool IsMember(ClaimsPrincipal user, string groupsClaim, string group) =>
        user.FindAll(groupsClaim).SelectMany(Groups).Any(candidate => string.Equals(candidate, group, StringComparison.Ordinal));

    // A claim holds one group, or a list that the provider wrote as a JSON array in one claim
    private static IEnumerable<string> Groups(Claim claim)
    {
        if (claim.ValueType != JsonClaimValueTypes.JsonArray)
        {
            return [claim.Value];
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(claim.Value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The moment of the second factor: when the session was opened at the level the configuration calls a step-up
    private static DateTimeOffset? StepUpAt(ClaimsPrincipal user, SignInSettings settings) =>
        settings.StepUpAcr is { Length: > 0 } acr
        && user.FindFirst(JwtRegisteredClaimNames.Acr)?.Value == acr
        && long.TryParse(user.FindFirst(JwtRegisteredClaimNames.AuthTime)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds is > 0 and <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
