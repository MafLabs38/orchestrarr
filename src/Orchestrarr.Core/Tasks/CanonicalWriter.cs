using System.Buffers.Binary;
using System.Text;

namespace Orchestrarr.Core.Tasks;

/// <summary>
/// Writes values so that no value can move a boundary or pass for another: a presence byte and a length for a text, a fixed width for a
/// number, whole seconds for a time. What is hashed is these bytes, so two different contents never share them.
/// </summary>
internal sealed class CanonicalWriter
{
    private readonly MemoryStream _stream = new();

    public void Text(string? value)
    {
        if (value is null)
        {
            _stream.WriteByte(0);
            return;
        }
        _stream.WriteByte(1);
        var bytes = Encoding.UTF8.GetBytes(value);
        Int(bytes.Length);
        _stream.Write(bytes);
    }

    public void Int(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void Long(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void Seconds(DateTimeOffset value) => Long(value.ToUnixTimeSeconds());

    public byte[] ToArray() => _stream.ToArray();
}
