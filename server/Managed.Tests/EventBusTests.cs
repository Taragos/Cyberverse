using Cyberverse.Server.Events;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Tests;

public class EventBusTests
{
    private static readonly Player Bob = new() { Name = "Bob", ConnectionId = 4 };

    [Fact]
    public void CallsSubscribersInOrderAndSurvivesOneThatThrows()
    {
        var bus = new EventBus();
        var calls = new List<string>();
        bus.Subscribe<PlayerAuthenticated>(e => calls.Add("first " + e.Player.Name));
        bus.Subscribe<PlayerAuthenticated>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<PlayerAuthenticated>(e => calls.Add("third " + e.Player.Name));

        bus.Publish(new PlayerAuthenticated(Bob));

        Assert.Equal(["first Bob", "third Bob"], calls);
    }

    [Fact]
    public void DeliversOnlyTheSubscribedType()
    {
        var bus = new EventBus();
        var left = 0;
        bus.Subscribe<PlayerLeft>(_ => left++);

        bus.Publish(new PlayerAuthenticated(Bob));
        bus.Publish(new PlayerLeft(Bob));

        Assert.Equal(1, left);
    }

    [Fact]
    public void DisposingUnsubscribes()
    {
        var bus = new EventBus();
        var calls = 0;
        var subscription = bus.Subscribe<PlayerLeft>(_ => calls++);
        bus.Publish(new PlayerLeft(Bob));
        subscription.Dispose();
        bus.Publish(new PlayerLeft(Bob));

        Assert.Equal(1, calls);
    }
}
