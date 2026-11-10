using System.Buffers.Binary;
using System.Text;

namespace Cyberverse.Server.Protocol;

/// <summary>
/// Reads the wire format described in shared/protocol/protocol.yaml. Packets come from clients, so every method checks
/// the remaining length first and returns false instead of reading past the end. A string or array whose length prefix
/// is larger than its max or than what is left of the packet fails before anything is allocated.
/// </summary>
public ref struct PacketReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    private bool TryTake(int count, out ReadOnlySpan<byte> bytes)
    {
        if (count > Remaining)
        {
            bytes = default;
            return false;
        }

        bytes = _data.Slice(_position, count);
        _position += count;
        return true;
    }

    public bool TryReadU8(out byte value)
    {
        value = 0;
        if (!TryTake(1, out var bytes)) return false;
        value = bytes[0];
        return true;
    }

    public bool TryReadI8(out sbyte value)
    {
        value = 0;
        if (!TryReadU8(out var raw)) return false;
        value = unchecked((sbyte)raw);
        return true;
    }

    /// <summary>C++ only defines 0 and 1 for a bool, so any other byte fails the packet.</summary>
    public bool TryReadBool(out bool value)
    {
        value = false;
        if (!TryReadU8(out var raw) || raw > 1) return false;
        value = raw == 1;
        return true;
    }

    public bool TryReadU16(out ushort value)
    {
        value = 0;
        if (!TryTake(2, out var bytes)) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    public bool TryReadI16(out short value)
    {
        value = 0;
        if (!TryTake(2, out var bytes)) return false;
        value = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        return true;
    }

    public bool TryReadU32(out uint value)
    {
        value = 0;
        if (!TryTake(4, out var bytes)) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadI32(out int value)
    {
        value = 0;
        if (!TryTake(4, out var bytes)) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadU64(out ulong value)
    {
        value = 0;
        if (!TryTake(8, out var bytes)) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    public bool TryReadI64(out long value)
    {
        value = 0;
        if (!TryTake(8, out var bytes)) return false;
        value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return true;
    }

    public bool TryReadF32(out float value)
    {
        value = 0;
        if (!TryTake(4, out var bytes)) return false;
        value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return true;
    }

    public bool TryReadF64(out double value)
    {
        value = 0;
        if (!TryTake(8, out var bytes)) return false;
        value = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        return true;
    }

    /// <summary>A u32 byte count, then that many bytes of UTF-8. Invalid UTF-8 fails the packet.</summary>
    public bool TryReadString(uint maxBytes, out string value)
    {
        value = "";
        if (!TryReadU32(out var length) || length > maxBytes || length > (uint)Remaining) return false;
        TryTake((int)length, out var bytes);
        try
        {
            value = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an array's u32 element count. Fails if it is over maxCount, or if that many elements of at least
    /// minElementSize bytes each can't fit in the rest of the packet, so a forged count can't make the caller allocate.
    /// </summary>
    public bool TryReadCount(uint maxCount, int minElementSize, out int count)
    {
        count = 0;
        if (!TryReadU32(out var raw) || raw > maxCount || (long)raw * minElementSize > Remaining) return false;
        count = (int)raw;
        return true;
    }
}
