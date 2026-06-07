using Persistence.Services;

namespace Persistence.Tests;

public class LocalClaudeModelClientTests
{
    private static PromptRequest Request(params (string Role, string Content)[] messages) => new()
    {
        Messages = messages.Select(m => new PromptMessage(m.Role, m.Content)).ToList(),
    };

    [Fact]
    public async Task RoutesPromptThroughBrokerAndReturnsResponse()
    {
        var broker = new RemotePeerBroker();
        var client = new LocalClaudeModelClient(broker);

        var completion = client.CompleteAsync(Request(("user", "hello")));

        // The flattened prompt is parked on the broker for the external peer.
        var pending = broker.TryGetPending();
        Assert.NotNull(pending);
        Assert.Contains("hello", pending!.Prompt);
        Assert.Contains("[user]", pending.Prompt);

        broker.SubmitResponse(pending.Id, "<respond>hi</respond>");
        Assert.Equal("<respond>hi</respond>", await completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StreamYieldsResponseThenCompleted()
    {
        var broker = new RemotePeerBroker();
        var client = new LocalClaudeModelClient(broker);

        var events = new List<Persistence.Services.Streaming.ModelStreamEvent>();
        var enumerate = Task.Run(async () =>
        {
            await foreach (var e in client.StreamAsync(Request(("user", "x"))))
            {
                events.Add(e);
            }
        });

        // Let the stream park its request, then answer it.
        await WaitFor(() => broker.TryGetPending() is not null);
        broker.SubmitResponse(broker.TryGetPending()!.Id, "answer");

        await enumerate.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("answer", events[0].Text);
        Assert.Equal(Persistence.Services.Streaming.ModelStreamEventKind.OutputTextDelta, events[0].Kind);
        Assert.Equal(Persistence.Services.Streaming.ModelStreamEventKind.Completed, events[^1].Kind);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
