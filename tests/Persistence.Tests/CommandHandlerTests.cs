using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class CommandHandlerTests
{
    /// <summary>Minimal concrete handler exposing a single "echo" command.</summary>
    private sealed class TestHandler(IEventBus bus) : CommandHandler(bus)
    {
        [Command("echo", "Echo the text back")]
        [CommandField("text", "string", required: true)]
        private Task<string> Echo(WorkingContextEntity context, JsonNode? command, CancellationToken ct) =>
            Task.FromResult($"echoed: {command?["text"]?.GetValue<string>()}");
    }

    private static WorkingContextEntity NewContext() =>
        new()
        {
            Name = "test",
            Summary = "test",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    private static (TestHandler Handler, List<ToolInvoked> Published) CreateHandler()
    {
        var bus = new EventBus();
        var published = new List<ToolInvoked>();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });
        return (new TestHandler(bus), published);
    }

    [Fact]
    public async Task DispatchesCommandAndRecordsResultFragment()
    {
        var (handler, _) = CreateHandler();
        var context = NewContext();

        await handler.HandleAsync(context, JsonNode.Parse("""{ "echo": { "text": "hi" } }"""));

        var fragment = Assert.Single(context.ContextFragments.Values);
        Assert.Equal(ContextFragmentType.ActionResponse, fragment.FragmentType);
        Assert.Contains("echoed: hi", fragment.Content);
    }

    [Fact]
    public async Task PublishesToolInvokedForEachCommand()
    {
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "echo": { "text": "hi" } }"""));

        var tool = Assert.Single(published);
        Assert.Equal("echo", tool.Tool);
        Assert.Equal("echoed: hi", tool.Result);
        Assert.Contains("\"text\":\"hi\"", tool.Request.Replace(" ", ""));
    }

    [Fact]
    public async Task ReportsUnknownCommand()
    {
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "bogus": {} }"""));

        var tool = Assert.Single(published);
        Assert.Equal("bogus", tool.Tool);
        Assert.Contains("Unknown command", tool.Result);
    }

    [Fact]
    public async Task ListCommandReturnsAvailableCommands()
    {
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "list": {} }"""));

        var tool = Assert.Single(published);
        Assert.Contains("echo", tool.Result);
    }
}
