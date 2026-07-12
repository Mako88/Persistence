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

        [Command("repeat", "Repeat the text N times")]
        [CommandField("text", "string", required: true)]
        [CommandField("count", "int", required: true)]
        private Task<string> Repeat(WorkingContextEntity context, JsonNode? command, CancellationToken ct)
        {
            // count is read as an int — passing text here throws a type-mismatch the base humanizes.
            var count = command?["count"]?.GetValue<int>() ?? 0;
            var text = command?["text"]?.GetValue<string>();
            return Task.FromResult(string.Concat(Enumerable.Repeat(text, count)));
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
    public async Task UnknownCommandSuggestsTheClosestKnownName()
    {
        // A near-miss of a real command should get a "did you mean?" so the peer can self-correct,
        // not just a bare "unknown". "ecko" is one edit from "echo".
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "ecko": { "text": "hi" } }"""));

        var tool = Assert.Single(published);
        Assert.Contains("Unknown command: 'ecko'", tool.Result);
        Assert.Contains("Did you mean 'echo'?", tool.Result);
    }

    [Fact]
    public async Task UnknownFieldSuggestsTheClosestKnownField()
    {
        // A typo'd field is otherwise silently ignored; the hint must name it and nudge toward the
        // real field. "txt" is one edit from the declared "text" (and isn't its singular/plural form).
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "echo": { "txt": "hi" } }"""));

        var tool = Assert.Single(published);
        Assert.Contains("ignored unknown field(s): 'txt'", tool.Result);
        Assert.Contains("did you mean 'text'?", tool.Result);
    }

    [Fact]
    public async Task TypeMismatchYieldsAHumanizedMessageNotAClrTypeName()
    {
        // Passing text where an integer field is read (GetValue<int>) throws a System.Text.Json
        // conversion error naming CLR types; the base must translate it to the peer's vocabulary.
        var (handler, published) = CreateHandler();

        await handler.HandleAsync(NewContext(), JsonNode.Parse("""{ "repeat": { "text": "hi", "count": "lots" } }"""));

        var tool = Assert.Single(published);
        Assert.Contains("Error executing 'repeat'", tool.Result);
        Assert.Contains("a whole number", tool.Result);   // friendly name for the expected int
        Assert.DoesNotContain("System.", tool.Result);    // no raw CLR type leaked to the peer
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
