using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class CommandHandlerTests
{
    /// <summary>Minimal concrete handler exposing an "echo" command and a "pick" command that (like the
    /// real tag command) declares both singular and plural spellings of the same field.</summary>
    private sealed class TestHandler(IEventBus bus) : CommandHandler(bus)
    {
        [Command("echo", "Echo the text back")]
        [CommandField("text", "string", required: true)]
        private Task<string> Echo(WorkingContextEntity context, JsonNode? command, CancellationToken ct) =>
            Task.FromResult($"echoed: {command?["text"]?.GetValue<string>()}");

        [Command("pick", "Pick tags")]
        [CommandField("tag", "string")]
        [CommandField("tags", "array")]
        private Task<string> Pick(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
        {
            var single = command?["tag"]?.GetValue<string>();
            var many = command?["tags"]?.AsArray()?.Count ?? 0;
            return Task.FromResult($"tag={single ?? "none"} tags={many}");
        }
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
    public async Task ResponseStructureWordAsFieldGetsTargetedHint()
    {
        // A peer putting a tag/control word like "continue" inside a command's parentheses is a
        // common small-model slip; the feedback should call it out specifically, not just "ignored".
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(),
            JsonNode.Parse("""{ "echo": { "text": "hi", "continue": true } }"""));

        var tool = Assert.Single(published);
        Assert.Equal("echoed: hi", tool.Result.Split('\n')[0]); // command still ran
        Assert.Contains("response structure", tool.Result);
        Assert.Contains("top level", tool.Result);
    }

    [Fact]
    public async Task AcceptsSingularOrPluralCommandName()
    {
        var (handler, published) = CreateHandler();

        // "echos" is the plural of the declared "echo" — it should resolve, not error.
        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "echos": { "text": "hi" } }"""));

        Assert.Equal("echoed: hi", Assert.Single(published).Result);
    }

    [Fact]
    public async Task AcceptsSingularOrPluralFieldName()
    {
        var (handler, published) = CreateHandler();

        // "texts" is the plural of the declared "text" field — re-keyed to "text", not silently ignored.
        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "echo": { "texts": "hi" } }"""));

        Assert.Equal("echoed: hi", Assert.Single(published).Result);
    }

    [Fact]
    public async Task DoesNotMangleCommandsThatDeclareBothSingularAndPluralFields()
    {
        var (handler, published) = CreateHandler();

        // 'pick' declares both `tag` and `tags`; a `tags` array must NOT be moved onto `tag`.
        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "pick": { "tags": ["a", "b"] } }"""));

        Assert.Equal("tag=none tags=2", Assert.Single(published).Result);
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
