using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.PacketHandling;

public delegate void HandlePacket<in T>(GameServer server, EMessageTypeServerbound messageType, byte channelId,
    uint connectionId, T content) where T : struct;

/// <summary>A registered handler before its packet is read: frame, sender and the packet's bytes after the frame.</summary>
public delegate void HandleRawPacket(MessageFrame frame, uint connectionId, ReadOnlySpan<byte> body);
