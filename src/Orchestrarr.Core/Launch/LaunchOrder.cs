using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Launch;

/// <summary>
/// What the operator ticked, bound to one commit, one environment and one launcher (SPEC D-41). It is data, never a command or a prompt:
/// the launcher builds the instruction of the agent from it and from templates. <c>StepUpAt</c> is when the approver last passed a
/// fresh second factor, signed so that the launcher can check it again for production (SPEC D-47).
/// </summary>
public sealed record LaunchOrder(
    Guid Id,
    string WorkItemId,
    string Repository,
    string HeadSha,
    string? Environment,
    IReadOnlyList<StepKind> Steps,
    string ApproverSubject,
    DateTimeOffset? StepUpAt,
    string Audience,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Changing the encoding below changes this tag, so an order of one form never verifies as another.</summary>
    public const string FormatTag = "orchestrarr-order-v2";

    /// <summary>
    /// The exact bytes that are signed and verified. Every field is written with a presence byte and a length, so no value can move a
    /// boundary or pass for another field; absence and the empty string differ; times are whole seconds; the steps are sorted.
    /// </summary>
    public byte[] Canonical()
    {
        using var stream = new MemoryStream();
        Write(stream, FormatTag);
        Write(stream, Id.ToString("D"));
        Write(stream, WorkItemId);
        Write(stream, Repository);
        Write(stream, HeadSha);
        Write(stream, Environment);
        var steps = Steps.Distinct().Order().ToArray();
        WriteInt(stream, steps.Length);
        foreach (var step in steps)
        {
            Write(stream, step.ToString());
        }
        Write(stream, ApproverSubject);
        WriteSeconds(stream, StepUpAt);
        Write(stream, Audience);
        WriteSeconds(stream, IssuedAt);
        WriteSeconds(stream, ExpiresAt);
        return stream.ToArray();
    }

    /// <summary>
    /// The order as the signature covers it: whole seconds in UTC, the steps sorted and copied. The launcher checks and executes this, never
    /// the object it was handed, so that nothing the signature does not cover can differ between the check and the run.
    /// </summary>
    public LaunchOrder Normalized() => this with
    {
        Steps = [.. Steps.Distinct().Order()],
        StepUpAt = StepUpAt is { } stepUp ? Seconds(stepUp) : null,
        IssuedAt = Seconds(IssuedAt),
        ExpiresAt = Seconds(ExpiresAt),
    };

    /// <summary>A short, safe identifier of the signed content, for the audit trail.</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Canonical()))[..16].ToLowerInvariant();

    internal static DateTimeOffset Seconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static void Write(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }
        stream.WriteByte(1);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteSeconds(Stream stream, DateTimeOffset? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }
        stream.WriteByte(1);
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value.Value.ToUnixTimeSeconds());
        stream.Write(buffer);
    }
}

/// <summary>An order and its signature. The signature scheme is the signer's (see <see cref="IOrderVerifier"/>).</summary>
public sealed record SignedOrder(LaunchOrder Order, string Signature);
