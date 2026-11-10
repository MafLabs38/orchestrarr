using System.Globalization;
using System.Security.Cryptography;

namespace Orchestrarr.Core.SecondFactor;

/// <summary>What an authenticator app is given to enrol an account: a random secret, and the <c>otpauth://</c> address that a QR code carries.</summary>
public static class OtpAuthUri
{
    /// <summary>160 bits, the size RFC 4226 recommends for the secret.</summary>
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static string Build(string issuer, string account, ReadOnlySpan<byte> secret, TotpOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var algorithm = options.Algorithm.ToString().ToUpperInvariant();
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm={algorithm}&digits={options.Digits}&period={options.StepSeconds}");
    }
}
