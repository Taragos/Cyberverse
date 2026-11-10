using Cyberverse.Server.Threading;

namespace Cyberverse.Server.Tests;

public class ServerThreadTests
{
    [Fact]
    public void RunsPostedActionsInOrderOnRunPending()
    {
        var thread = new ServerThread();
        var calls = new List<int>();
        var other = new Thread(() => { thread.Post(() => calls.Add(1)); thread.Post(() => calls.Add(2)); });
        other.Start();
        other.Join();

        Assert.Empty(calls);
        thread.RunPending();
        Assert.Equal([1, 2], calls);
    }

    [Fact]
    public void AThrowingActionDoesNotStopTheOthersAndRepostsWaitForTheNextRun()
    {
        var thread = new ServerThread();
        var calls = new List<string>();
        thread.Post(() => throw new InvalidOperationException("boom"));
        thread.Post(() =>
        {
            calls.Add("first");
            thread.Post(() => calls.Add("again"));
        });

        thread.RunPending();
        Assert.Equal(["first"], calls);
        thread.RunPending();
        Assert.Equal(["first", "again"], calls);
    }
}
