namespace Orchestrarr.Core.SecondFactor;

/// <summary>RFC 4648 base32, the encoding that authenticator apps read a secret in. Decoding ignores case, spaces, dashes and padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The unpadded form, which is what a <c>otpauth://</c> address carries.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var chars = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var next = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                chars[next++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0)
        {
            chars[next] = Alphabet[(buffer << (5 - bits)) & 31];
        }
        return new string(chars);
    }

    /// <summary>False when the text holds a character outside the alphabet, or leaves bits that cannot be a whole byte.</summary>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var cleaned = text.Where(c => c is not (' ' or '-' or '=')).Select(char.ToUpperInvariant).ToArray();
        if (cleaned.Length == 0)
        {
            return false;
        }
        var bytes = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in cleaned)
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        // What is left must be padding zeros of the last character, never a whole byte
        if (bits >= 5 || buffer != 0)
        {
            return false;
        }
        data = [.. bytes];
        return true;
    }
}
