using System.Security.Cryptography;

namespace Orchestrarr.Core.SecondFactor;

public enum TotpAlgorithm
{
    /// <summary>What every authenticator app reads. Fine for a one-time code that lives thirty seconds (RFC 6238).</summary>
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>The shape of a time-based one-time code. The defaults are the ones every authenticator app assumes.</summary>
public sealed record TotpOptions
{
    public int Digits { get; init; } = 6;

    public int StepSeconds { get; init; } = 30;

    /// <summary>How many steps either side of now are accepted: the clocks of a phone and of a host never agree to the second.</summary>
    public int Window { get; init; } = 1;

    public TotpAlgorithm Algorithm { get; init; } = TotpAlgorithm.Sha1;
}

/// <summary>RFC 6238 over RFC 4226. It only computes and compares; whether a code was already used is the guard's business.</summary>
public static class Totp
{
    public static long StepAt(DateTimeOffset time, TotpOptions options) => time.ToUnixTimeSeconds() / options.StepSeconds;

    /// <summary>The code of one step, zero-padded to <see cref="TotpOptions.Digits"/>.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step, TotpOptions options)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[64];
        var length = options.Algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(secret, counter, hash),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(secret, counter, hash),
#pragma warning disable CA5350 // RFC 6238 with SHA-1 is what authenticator apps implement; the code is a 6-digit value that is good for thirty seconds
            _ => HMACSHA1.HashData(secret, counter, hash),
#pragma warning restore CA5350
        };

        // Dynamic truncation (RFC 4226 section 5.3)
        var offset = hash[length - 1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, options.Digits);
        return (binary % modulus).ToString(new string('0', options.Digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The latest step in the window whose code is <paramref name="code"/>, or null. Every step of the window is compared, in constant
    /// time, whatever the first answer is.
    /// </summary>
    public static long? MatchingStep(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, TotpOptions options)
    {
        var current = StepAt(now, options);
        var given = System.Text.Encoding.ASCII.GetBytes(code);
        long? match = null;
        for (var step = current - options.Window; step <= current + options.Window; step++)
        {
            var expected = System.Text.Encoding.ASCII.GetBytes(Code(secret, step, options));
            if (CryptographicOperations.FixedTimeEquals(expected, given))
            {
                match = step;
            }
        }
        return match;
    }
}
