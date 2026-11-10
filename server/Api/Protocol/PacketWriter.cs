using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Cyberverse.Server.Protocol;

/// <summary>
/// Writes the wire format described in shared/protocol/protocol.yaml into a growing buffer. Reuse one instance with
/// <see cref="Reset"/> to avoid an allocation per packet.
/// </summary>
public sealed class PacketWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public void Reset() => _buffer.ResetWrittenCount();

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public void WriteU8(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
    }

    public void WriteI8(sbyte value) => WriteU8(unchecked((byte)value));

    public void WriteBool(bool value) => WriteU8(value ? (byte)1 : (byte)0);

    public void WriteU16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
    }

    public void WriteI16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
    }

    public void WriteU32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    public void WriteI32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    public void WriteU64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
    }

    public void WriteI64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
    }

    public void WriteF32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    public void WriteF64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
    }

    /// <summary>Writes null as an empty string. Throws if the UTF-8 form is longer than maxBytes.</summary>
    public void WriteString(string? value, uint maxBytes)
    {
        value ??= "";
        var length = Encoding.UTF8.GetByteCount(value);
        if ((uint)length > maxBytes)
        {
            throw new ArgumentException($"String is {length} bytes of UTF-8, the protocol allows {maxBytes}", nameof(value));
        }

        WriteU32((uint)length);
        Encoding.UTF8.GetBytes(value, _buffer.GetSpan(length));
        _buffer.Advance(length);
    }

    /// <summary>Writes an array's element count. Throws if it is over maxCount.</summary>
    public void WriteCount(int count, uint maxCount)
    {
        if ((uint)count > maxCount)
        {
            throw new ArgumentException($"Array has {count} elements, the protocol allows {maxCount}", nameof(count));
        }

        WriteU32((uint)count);
    }
}
