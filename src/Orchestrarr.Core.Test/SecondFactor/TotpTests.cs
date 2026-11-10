using System.Text;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Core.Test.SecondFactor;

public sealed class TotpTests
{
    // RFC 6238 appendix B: the secret is the ASCII digits, repeated to the length of the hash, and the codes are eight digits.
    private static readonly byte[] Sha1Secret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Sha256Secret = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Sha512Secret = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890123456789012345678901234");

    private static TotpOptions Eight(TotpAlgorithm algorithm) => new() { Digits = 8, Algorithm = algorithm };

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Sha1_gives_the_codes_of_the_rfc(long seconds, string expected)
    {
        var options = Eight(TotpAlgorithm.Sha1);
        Totp.Code(Sha1Secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(seconds), options), options).Should().Be(expected);
    }

    [Theory]
    [InlineData(59L, "46119246")]
    [InlineData(1111111109L, "68084774")]
    public void Sha256_gives_the_codes_of_the_rfc(long seconds, string expected)
    {
        var options = Eight(TotpAlgorithm.Sha256);
        Totp.Code(Sha256Secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(seconds), options), options).Should().Be(expected);
    }

    [Fact]
    public void Sha512_gives_the_code_of_the_rfc()
    {
        var options = Eight(TotpAlgorithm.Sha512);
        Totp.Code(Sha512Secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(59), options), options).Should().Be("90693936");
    }

    [Fact]
    public void A_code_is_padded_with_zeros_to_its_length()
    {
        // 07081804 above starts with a zero: with six digits some code must too
        var options = new TotpOptions();
        var codes = Enumerable.Range(0, 400).Select(step => Totp.Code(Sha1Secret, step, options)).ToList();
        codes.Should().OnlyContain(code => code.Length == 6);
        codes.Should().Contain(code => code.StartsWith('0'));
    }

    [Fact]
    public void The_window_accepts_a_step_either_side_and_no_more()
    {
        var options = new TotpOptions();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = Totp.StepAt(now, options);

        Totp.MatchingStep(Sha1Secret, Totp.Code(Sha1Secret, step, options), now, options).Should().Be(step);
        Totp.MatchingStep(Sha1Secret, Totp.Code(Sha1Secret, step - 1, options), now, options).Should().Be(step - 1);
        Totp.MatchingStep(Sha1Secret, Totp.Code(Sha1Secret, step + 1, options), now, options).Should().Be(step + 1);
        Totp.MatchingStep(Sha1Secret, Totp.Code(Sha1Secret, step - 2, options), now, options).Should().BeNull();
        Totp.MatchingStep(Sha1Secret, Totp.Code(Sha1Secret, step + 2, options), now, options).Should().BeNull();
    }

    [Fact]
    public void A_wrong_secret_never_matches()
    {
        var options = new TotpOptions();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var code = Totp.Code(Sha256Secret, Totp.StepAt(now, options), options);

        Totp.MatchingStep(Sha1Secret, code, now, options).Should().BeNull();
    }
}

public sealed class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encodes_the_vectors_of_the_rfc_without_padding(string text, string expected)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(text)).Should().Be(expected);
    }

    [Theory]
    [InlineData("MZXW6YTBOI", "foobar")]
    [InlineData("mzxw6ytboi======", "foobar")]
    [InlineData("MZXW 6YTB-OI", "foobar")]
    [InlineData("MY", "f")]
    public void Decodes_whatever_the_case_the_spaces_and_the_padding(string text, string expected)
    {
        Base32.TryDecode(text, out var data).Should().BeTrue();
        Encoding.ASCII.GetString(data).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MZXW1")]
    [InlineData("M")]
    [InlineData("MZ")]
    public void Refuses_a_text_that_is_not_a_secret(string? text)
    {
        Base32.TryDecode(text, out _).Should().BeFalse();
    }

    [Fact]
    public void A_new_secret_survives_a_round_trip_and_two_secrets_differ()
    {
        var secret = OtpAuthUri.NewSecret();

        secret.Should().HaveCount(OtpAuthUri.SecretBytes);
        Base32.TryDecode(Base32.Encode(secret), out var back).Should().BeTrue();
        back.Should().Equal(secret);
        OtpAuthUri.NewSecret().Should().NotEqual(secret);
    }

    [Fact]
    public void The_address_for_an_authenticator_carries_the_secret_and_the_shape_of_the_code()
    {
        var uri = OtpAuthUri.Build("Orchestrarr", "alice@homelab.example", Encoding.ASCII.GetBytes("foobar"), new TotpOptions());

        uri.Should().Be("otpauth://totp/Orchestrarr:alice%40homelab.example?secret=MZXW6YTBOI&issuer=Orchestrarr&algorithm=SHA1&digits=6&period=30");
    }
}
