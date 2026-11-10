namespace Cyberverse.Server.Protocol;

/// <summary>A type with a wire form: the generated packets, structs and anything a plugin writes by hand.</summary>
public interface IWireType<TSelf> where TSelf : struct, IWireType<TSelf>
{
    void Write(PacketWriter writer);

    static abstract bool TryRead(ref PacketReader reader, out TSelf value);
}

/// <summary>
/// A packet: a wire type with a message id. The native layer never looks at packet types, so a plugin can add one by
/// implementing this (with an id the schema doesn't use) without rebuilding it.
/// </summary>
public interface IPacket<TSelf> : IWireType<TSelf> where TSelf : struct, IPacket<TSelf>
{
    static abstract ushort MessageId { get; }

    /// <summary>The GameNetworkingSockets lane, written into the frame.</summary>
    static abstract byte Channel { get; }

    /// <summary>Resent until it arrives (k_nSteamNetworkingSend_Reliable), or sent once.</summary>
    static abstract bool Reliable { get; }
}

/// <summary>Sent by the client.</summary>
public interface IServerBoundPacket<TSelf> : IPacket<TSelf> where TSelf : struct, IServerBoundPacket<TSelf>;

/// <summary>Sent by the server.</summary>
public interface IClientBoundPacket<TSelf> : IPacket<TSelf> where TSelf : struct, IClientBoundPacket<TSelf>;

/// <summary>
/// The 4 bytes in front of every packet: u8 channel, u16 message id, u8 reserved (zero; ignored when reading). The C++
/// side is MessageFrame in shared/protocol/Protocol.h.
/// </summary>
public readonly record struct MessageFrame(byte Channel, ushort MessageId)
{
    public const int Size = 4;

    public static bool TryRead(ref PacketReader reader, out MessageFrame frame)
    {
        frame = default;
        if (!reader.TryReadU8(out var channel) || !reader.TryReadU16(out var messageId) || !reader.TryReadU8(out _))
        {
            return false;
        }

        frame = new MessageFrame(channel, messageId);
        return true;
    }

    public void Write(PacketWriter writer)
    {
        writer.WriteU8(Channel);
        writer.WriteU16(MessageId);
        writer.WriteU8(0);
    }
}

public static class Packet
{
    /// <summary>Writes the frame and the packet, i.e. a whole message as it goes on the wire.</summary>
    public static void Write<T>(PacketWriter writer, T packet) where T : struct, IPacket<T>
    {
        new MessageFrame(T.Channel, T.MessageId).Write(writer);
        packet.Write(writer);
    }

    /// <summary>Reads a packet's body (the message after its frame). Fails unless the packet uses every byte.</summary>
    public static bool TryRead<T>(ReadOnlySpan<byte> body, out T packet) where T : struct, IPacket<T>
    {
        var reader = new PacketReader(body);
        return T.TryRead(ref reader, out packet) && reader.Remaining == 0;
    }
}
