using Persistence.Utilities;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class CommandParserTests
{
    [Fact]
    public void ParsesSingleCommandObject()
    {
        var data = JsonNode.Parse("""{ "schedule": { "name": "standup" } }""");

        var result = CommandParser.Parse(data).Single();

        Assert.Equal("schedule", result.Command);
        Assert.Equal("standup", result.Fields?["name"]?.GetValue<string>());
    }

    [Fact]
    public void LowercasesCommandName()
    {
        var data = JsonNode.Parse("""{ "Schedule": {} }""");

        Assert.Equal("schedule", CommandParser.Parse(data).Single().Command);
    }

    [Fact]
    public void ParsesTopLevelArrayOfCommands()
    {
        var data = JsonNode.Parse("""[ { "add": { "content": "x" } }, { "remove": { "id": 1 } } ]""");

        var results = CommandParser.Parse(data).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("add", results[0].Command);
        Assert.Equal("remove", results[1].Command);
    }

    [Fact]
    public void ParsesCommandsWrapperProperty()
    {
        var data = JsonNode.Parse("""{ "commands": [ { "log": { "action_type": "t" } } ] }""");

        var result = CommandParser.Parse(data).Single();

        Assert.Equal("log", result.Command);
        Assert.Equal("t", result.Fields?["action_type"]?.GetValue<string>());
    }

    [Fact]
    public void ReturnsErrorForNullData()
    {
        var result = CommandParser.Parse(null).Single();

        Assert.Equal("error", result.Command);
        Assert.Null(result.Fields);
    }

    [Fact]
    public void ReturnsErrorForEmptyCommandObject()
    {
        var data = JsonNode.Parse("{}");

        Assert.Equal("error", CommandParser.Parse(data).Single().Command);
    }
}
