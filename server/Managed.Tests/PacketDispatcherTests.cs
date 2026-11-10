using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Tests;

public class PacketDispatcherTests
{
    private static byte[] Body<T>(T packet) where T : struct, IPacket<T>
    {
        var writer = new PacketWriter();
        Packet.Write(writer, packet);
        return writer.WrittenSpan[MessageFrame.Size..].ToArray();
    }

    private static readonly PlayerEquipItem Equip = new() { Slot = 3, ItemId = 0x1234, IsWeapon = true };

    [Fact]
    public void RunsEveryHandlerForAMessageTypeInOrder()
    {
        var dispatcher = new PacketDispatcher();
        var calls = new List<string>();
        dispatcher.Register<PlayerEquipItem>((connection, packet) => calls.Add($"a {connection} {packet.ItemId:X}"));
        dispatcher.Register<PlayerEquipItem>((connection, packet) => calls.Add($"b {connection} {packet.Slot}"));

        var result = dispatcher.Dispatch(PlayerEquipItem.MessageId, 7, Body(Equip));

        Assert.Equal(DispatchResult.Handled, result);
        Assert.Equal(["a 7 1234", "b 7 3"], calls);
    }

    [Fact]
    public void AThrowingHandlerDoesNotStopTheOthers()
    {
        var dispatcher = new PacketDispatcher();
        var ran = false;
        dispatcher.Register<PlayerEquipItem>((_, _) => throw new InvalidOperationException("boom"));
        dispatcher.Register<PlayerEquipItem>((_, _) => ran = true);

        Assert.Equal(DispatchResult.Handled, dispatcher.Dispatch(PlayerEquipItem.MessageId, 1, Body(Equip)));
        Assert.True(ran);
    }

    [Fact]
    public void DropsMalformedPacketsBeforeAnyHandler()
    {
        var dispatcher = new PacketDispatcher();
        var ran = false;
        dispatcher.Register<PlayerUnmountCar>((_, _) => ran = true);

        Assert.Equal(DispatchResult.Malformed, dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, [0x01]));
        Assert.False(ran);
    }

    [Fact]
    public void ReportsMessageTypesWithoutHandlers()
    {
        var dispatcher = new PacketDispatcher();
        Assert.Equal(DispatchResult.NoHandler, dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, []));

        var registration = dispatcher.Register<PlayerUnmountCar>((_, _) => { });
        registration.Dispose();
        Assert.Equal(DispatchResult.NoHandler, dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, []));
    }

    [Fact]
    public void DisposingRemovesOnlyThatRegistration()
    {
        var dispatcher = new PacketDispatcher();
        var count = 0;
        Networking.PacketHandler<PlayerUnmountCar> handler = (_, _) => count++;
        var first = dispatcher.Register(handler);
        dispatcher.Register(handler);

        first.Dispose();
        first.Dispose();
        dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, []);

        Assert.Equal(1, count);
    }

    [Fact]
    public void AHandlerMayUnregisterWhileThePacketRuns()
    {
        var dispatcher = new PacketDispatcher();
        var calls = 0;
        IDisposable? self = null;
        self = dispatcher.Register<PlayerUnmountCar>((_, _) => self!.Dispose());
        dispatcher.Register<PlayerUnmountCar>((_, _) => calls++);

        dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, []);
        dispatcher.Dispatch(PlayerUnmountCar.MessageId, 1, []);

        Assert.Equal(2, calls);
    }

    private struct ClashingPacket : IServerBoundPacket<ClashingPacket>
    {
        public static ushort MessageId => PlayerUnmountCar.MessageId;
        public static byte Channel => 0;
        public static bool Reliable => true;

        public readonly void Write(PacketWriter writer)
        {
        }

        public static bool TryRead(ref PacketReader reader, out ClashingPacket value)
        {
            value = default;
            return true;
        }
    }

    [Fact]
    public void RejectsASecondPacketTypeOnTheSameMessageId()
    {
        var dispatcher = new PacketDispatcher();
        dispatcher.Register<PlayerUnmountCar>((_, _) => { });

        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Register<ClashingPacket>((_, _) => { }));
        Assert.Contains(nameof(PlayerUnmountCar), error.Message);
    }
}
