using Persistence.Services;

namespace Persistence.Tests;

public class RemotePeerBrokerTests
{
    [Fact]
    public void NoPendingInitially()
    {
        var broker = new RemotePeerBroker();
        Assert.Null(broker.TryGetPending());
    }

    [Fact]
    public async Task RequestThenRespondCompletesTheTask()
    {
        var broker = new RemotePeerBroker();

        var completion = broker.RequestCompletionAsync("the prompt");
        Assert.False(completion.IsCompleted);

        var pending = broker.TryGetPending();
        Assert.NotNull(pending);
        Assert.Equal("the prompt", pending!.Prompt);

        Assert.True(broker.SubmitResponse(pending.Id, "the answer"));
        Assert.Equal("the answer", await completion.WaitAsync(TimeSpan.FromSeconds(5)));

        // Pending clears once answered.
        Assert.Null(broker.TryGetPending());
    }

    [Fact]
    public void SubmitWithUnknownIdReturnsFalse()
    {
        var broker = new RemotePeerBroker();
        Assert.False(broker.SubmitResponse("nope", "x"));
    }

    [Fact]
    public async Task SubmitTwiceForSameIdReturnsFalseSecondTime()
    {
        var broker = new RemotePeerBroker();
        var completion = broker.RequestCompletionAsync("p");
        var id = broker.TryGetPending()!.Id;

        Assert.True(broker.SubmitResponse(id, "first"));
        Assert.False(broker.SubmitResponse(id, "second"));
        Assert.Equal("first", await completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellationDropsPendingAndCancelsTask()
    {
        var broker = new RemotePeerBroker();
        using var cts = new CancellationTokenSource();

        var completion = broker.RequestCompletionAsync("p", cts.Token);
        var id = broker.TryGetPending()!.Id;

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.Null(broker.TryGetPending());
        // A response arriving after cancellation no longer matches.
        Assert.False(broker.SubmitResponse(id, "late"));
    }

    [Fact]
    public async Task SequentialRequestsEachGetDistinctIds()
    {
        var broker = new RemotePeerBroker();

        var first = broker.RequestCompletionAsync("one");
        var firstId = broker.TryGetPending()!.Id;
        broker.SubmitResponse(firstId, "1");
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        var second = broker.RequestCompletionAsync("two");
        var secondId = broker.TryGetPending()!.Id;

        Assert.NotEqual(firstId, secondId);
        broker.SubmitResponse(secondId, "2");
        Assert.Equal("2", await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
