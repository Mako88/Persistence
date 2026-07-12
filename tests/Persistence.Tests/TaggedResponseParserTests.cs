using Persistence.Services;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class TaggedResponseParserTests
{
    private static readonly TaggedResponseParser Parser = new();

    [Fact]
    public void ParsesThinkAndRespondInOrder()
    {
        var turn = Parser.Parse(
            "<think>I should greet them.</think>\n<respond>Hello!</respond>\n<continue>false</continue>");

        Assert.True(turn.ParsedSuccessfully);
        Assert.False(turn.Continue);
        Assert.Equal(2, turn.Actions.Count);

        Assert.Equal(ModelAction.Think, turn.Actions[0].Action);
        Assert.Equal("I should greet them.", turn.Actions[0].Data?.GetValue<string>());

        Assert.Equal(ModelAction.RespondToUser, turn.Actions[1].Action);
        Assert.Equal("Hello!", turn.Actions[1].Data?.GetValue<string>());
    }

    [Fact]
    public void ParsesContinueTrue()
    {
        var turn = Parser.Parse("<think>more to do</think><continue>true</continue>");

        Assert.True(turn.Continue);
    }

    [Fact]
    public void PlainThinkCarriesItsTextDirectly()
    {
        var think = Assert.Single(Parser.Parse("<think>open reasoning</think>").Actions);

        Assert.Equal(ModelAction.Think, think.Action);
        Assert.Equal("open reasoning", think.Data?.GetValue<string>()); // a bare string, no private flag
    }

    [Fact]
    public void PrivateThinkIsFlaggedAndKeepsItsText()
    {
        var think = Assert.Single(Parser.Parse("<think private>hidden reasoning</think>").Actions);

        Assert.Equal(ModelAction.Think, think.Action);
        Assert.Equal("hidden reasoning", think.Data?["text"]?.GetValue<string>());
        Assert.True(think.Data?["private"]?.GetValue<bool>());
    }

    [Fact]
    public void RespondPreservesMarkdownAndQuotesUnescaped()
    {
        var body = "# Heading\n\nText with \"quotes\" and a `code` span.";
        var turn = Parser.Parse($"<respond>{body}</respond>");

        Assert.Equal(body, Assert.Single(turn.Actions).Data?.GetValue<string>());
    }

    [Fact]
    public void ContextTagBecomesManageContextCommandArray()
    {
        var turn = Parser.Parse("""
            <context>
            update(id=42, weight=0.9)
            remove(id=7)
            </context>
            """);

        var action = Assert.Single(turn.Actions);
        Assert.Equal(ModelAction.ManageContext, action.Action);

        var commands = Assert.IsType<JsonArray>(action.Data);
        Assert.Equal(2, commands.Count);
        Assert.Equal(42, commands[0]!["update"]!["id"]!.GetValue<long>());
        Assert.Equal(7, commands[1]!["remove"]!["id"]!.GetValue<long>());
    }

    [Fact]
    public void ActionsTagBecomesExecuteActions()
    {
        var turn = Parser.Parse("""<actions>schedule(name="standup", scheduled_for=2026-06-08T09:00Z)</actions>""");

        var action = Assert.Single(turn.Actions);
        Assert.Equal(ModelAction.ExecuteActions, action.Action);
        var commands = Assert.IsType<JsonArray>(action.Data);
        Assert.Equal("standup", commands[0]!["schedule"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void MultilineContentInCommandSurvivesViaTripleQuotes()
    {
        // Built by concatenation: the command body itself contains triple-quotes, which
        // would collide with a C# raw-string delimiter.
        var q = "\"\"\"";
        var input =
            "<context>\n" +
            $"remember(content={q}Line one\nLine two with \"quotes{q}, importance=0.8)\n" +
            "</context>";

        var turn = Parser.Parse(input);

        var add = Assert.IsType<JsonArray>(turn.Actions.Single().Data)[0]!["remember"]!;
        Assert.Equal("Line one\nLine two with \"quotes", add["content"]!.GetValue<string>());
        Assert.Equal(0.8, add["importance"]!.GetValue<double>());
    }

    [Fact]
    public void FullMultiActionTurn()
    {
        var turn = Parser.Parse("""
            <think>Greet, then bump a fragment.</think>
            <context>
            update(id=3, weight=1.0)
            </context>
            <respond>Hey there!</respond>
            <continue>false</continue>
            """);

        Assert.Equal(
            [ModelAction.Think, ModelAction.ManageContext, ModelAction.RespondToUser],
            turn.Actions.Select(a => a.Action));
        Assert.False(turn.Continue);
    }

    [Fact]
    public void UnknownTagsAreIgnored()
    {
        var turn = Parser.Parse("<scratch>noise</scratch><respond>hi</respond>");

        Assert.Equal(ModelAction.RespondToUser, Assert.Single(turn.Actions).Action);
    }

    [Fact]
    public void NoRecognisedTagsFailsParse()
    {
        var turn = Parser.Parse("just plain prose with no tags");

        Assert.False(turn.ParsedSuccessfully);
        Assert.Empty(turn.Actions);
    }
}
