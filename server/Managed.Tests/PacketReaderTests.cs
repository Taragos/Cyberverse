using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Tests;

/// <summary>Packets come from clients, so reading has to fail cleanly on anything malformed.</summary>
public class PacketReaderTests
{
    private static byte[] Body<T>(T packet) where T : struct, IPacket<T>
    {
        var writer = new PacketWriter();
        packet.Write(writer);
        return writer.ToArray();
    }

    private static readonly InitAuth Auth = new() { ProtocolVersion = 0xDEADBEEF, Username = "Jürgen" };

    [Fact]
    public void RoundTripsAPacket()
    {
        Assert.True(Packet.TryRead<InitAuth>(Body(Auth), out var read));
        Assert.Equal(0xDEADBEEF, read.ProtocolVersion);
        Assert.Equal("Jürgen", read.Username);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var packet = new EquipItemEntity
        {
            NetworkedEntityId = ulong.MaxValue, Slot = 2, ItemId = 0x0123456789ABCDEF, IsWeapon = true, IsUnequipping = false,
        };
        Assert.True(Packet.TryRead<EquipItemEntity>(Body(packet), out var read));
        Assert.Equal(packet, read);
    }

    [Fact]
    public void EmptyPacketHasNoBytes()
    {
        Assert.Empty(Body(new PlayerUnmountCar()));
        Assert.True(Packet.TryRead<PlayerUnmountCar>([], out _));
    }

    [Fact]
    public void EveryTruncationFails()
    {
        var bytes = Body(new PlayerShoot { ItemIdWeapon = 1, StartPoint = new Vector3(1, 2, 3), Charge = 0.5f });
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.False(Packet.TryRead<PlayerShoot>(bytes.AsSpan(0, length), out _), $"{length} of {bytes.Length} bytes");
        }

        var auth = Body(Auth);
        for (var length = 0; length < auth.Length; length++)
        {
            Assert.False(Packet.TryRead<InitAuth>(auth.AsSpan(0, length), out _), $"{length} of {auth.Length} bytes");
        }
    }

    [Fact]
    public void TrailingBytesFail()
    {
        Assert.False(Packet.TryRead<InitAuth>([.. Body(Auth), 0], out _));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)] // far past the end of the packet
    [InlineData(8u)] // one byte past the end: "Jürgen" is 7 bytes
    public void StringLongerThanThePacketFails(uint claimed)
    {
        var bytes = Body(Auth);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), claimed);
        Assert.False(Packet.TryRead<InitAuth>(bytes, out _));
    }

    [Fact]
    public void StringOverItsMaxFailsEvenIfTheBytesAreThere()
    {
        var writer = new PacketWriter();
        writer.WriteU32(1);
        writer.WriteString(new string('a', 256), uint.MaxValue);
        Assert.False(Packet.TryRead<InitAuth>(writer.ToArray(), out _));

        writer.Reset();
        writer.WriteU32(1);
        writer.WriteString(new string('a', 255), uint.MaxValue);
        writer.WriteString("v0.1.0", uint.MaxValue);
        writer.WriteString("2.31.0", uint.MaxValue);
        Assert.True(Packet.TryRead<InitAuth>(writer.ToArray(), out var read));
        Assert.Equal(255, read.Username.Length);
    }

    [Fact]
    public void WriterRefusesAStringOverItsMax()
    {
        var packet = new InitAuth { Username = new string('a', 256) };
        Assert.Throws<ArgumentException>(() => Body(packet));
    }

    [Fact]
    public void InvalidUtf8Fails()
    {
        var writer = new PacketWriter();
        writer.WriteU32(1);
        writer.WriteU32(2);
        writer.WriteU8(0xC3); // a lead byte followed by another lead byte
        writer.WriteU8(0xC3);
        Assert.False(Packet.TryRead<InitAuth>(writer.ToArray(), out _));
    }

    [Fact]
    public void UnknownEnumValueFails()
    {
        var bytes = Body(new InitAuthResult { AuthResult = EAuthResult.ValidationFailed, ProtocolVersion = 1 });
        Assert.True(Packet.TryRead<InitAuthResult>(bytes, out _));
        bytes[0] = 3;
        Assert.False(Packet.TryRead<InitAuthResult>(bytes, out _));
    }

    [Fact]
    public void BoolOtherThanZeroOrOneFails()
    {
        var bytes = Body(new PlayerEquipItem { IsWeapon = true });
        bytes[16] = 2;
        Assert.False(Packet.TryRead<PlayerEquipItem>(bytes, out _));
    }

    [Fact]
    public void ArrayCountThatCantFitFailsBeforeAllocating()
    {
        // No packet has an array yet, so this checks the reader method the generated code calls.
        var writer = new PacketWriter();
        writer.WriteU32(1000);
        writer.WriteU64(0);
        var bytes = writer.ToArray();

        var reader = new PacketReader(bytes);
        Assert.False(reader.TryReadCount(uint.MaxValue, 12, out _)); // 1000 Vector3s don't fit in 8 bytes
        reader = new PacketReader(bytes);
        Assert.False(reader.TryReadCount(999, 0, out _)); // over the max
        reader = new PacketReader(bytes);
        Assert.True(reader.TryReadCount(uint.MaxValue, 0, out var count));
        Assert.Equal(1000, count);

        writer.Reset();
        writer.WriteU32(2);
        writer.WriteU64(0);
        reader = new PacketReader(writer.ToArray());
        Assert.True(reader.TryReadCount(uint.MaxValue, 4, out count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void FrameIsReadFromTheFirstFourBytes()
    {
        var writer = new PacketWriter();
        Packet.Write(writer, new SpawnEntity { NetworkedEntityId = 5 });
        var bytes = writer.ToArray();
        Assert.Equal(new byte[] { 1, 1, 0, 0 }, bytes[..4]); // channel 1, message id 1 (little endian), reserved

        var reader = new PacketReader(bytes);
        Assert.True(MessageFrame.TryRead(ref reader, out var frame));
        Assert.Equal(new MessageFrame(SpawnEntity.Channel, SpawnEntity.MessageId), frame);

        var tooShort = new PacketReader(bytes.AsSpan(0, 3));
        Assert.False(MessageFrame.TryRead(ref tooShort, out _));
    }
}
