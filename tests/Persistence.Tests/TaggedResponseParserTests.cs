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

    // --- Continuing is the default; yielding is the deliberate act ---

    [Fact]
    public void OmittingContinueKeepsTheFloor()
    {
        // A peer holds its turn until it says otherwise. Forgetting the tag used to end the turn
        // mid-thought, which failed silently and cost the peer its turn rather than anything visible.
        var turn = Parser.Parse("<think>still working</think>");

        Assert.True(turn.ParsedSuccessfully);
        Assert.True(turn.Continue);
    }

    [Fact]
    public void OnlyAnExplicitFalseYields()
    {
        Assert.False(Parser.Parse("<respond>done</respond><continue>false</continue>").Continue);
        Assert.False(Parser.Parse("<respond>done</respond><continue>FALSE</continue>").Continue);
        Assert.False(Parser.Parse("<respond>done</respond><continue>  false  </continue>").Continue);
    }

    [Fact]
    public void AnUnexpectedContinueValueKeepsTheFloorRatherThanEndingTheTurn()
    {
        // Matching the omitted-tag default: a typo shouldn't quietly end a turn the peer meant to keep.
        Assert.True(Parser.Parse("<respond>hm</respond><continue>yes</continue>").Continue);
        Assert.True(Parser.Parse("<respond>hm</respond><continue></continue>").Continue);
    }

    [Fact]
    public void AnUnparseableResponseDoesNotContinue()
    {
        // The default must not leak into the failure path: an unparseable response has its own
        // re-prompt-with-feedback loop, and treating it as "keep going" would spend the whole
        // iteration cap on a model that can't produce a valid response.
        var turn = Parser.Parse("just prose, no tags at all");

        Assert.False(turn.ParsedSuccessfully);
        Assert.False(turn.Continue);
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

    // --- Emoji ---
    //
    // From John: "an emoji as the first character seemingly resulted in a double-send from ember
    // [7/13/26 1:27AM]". An emoji is a UTF-16 surrogate pair, which is exactly the shape that trips
    // char-indexing and character classes, so the parser is the first place to rule in or out: if it
    // ever produced two RespondToUser actions from one reply, the turn would dispatch two sends.

    [Fact]
    public void AnEmojiLeadingReplyParsesToExactlyOneResponse()
    {
        var turn = Parser.Parse("<respond>\U0001F389 Congratulations!</respond><continue>false</continue>");

        Assert.True(turn.ParsedSuccessfully);
        var reply = Assert.Single(turn.Actions);
        Assert.Equal(ModelAction.RespondToUser, reply.Action);
        Assert.Equal("\U0001F389 Congratulations!", reply.Data?.GetValue<string>());
    }

    [Fact]
    public void EmojiThroughoutATurnStillParseToOneThinkAndOneResponse()
    {
        var turn = Parser.Parse(
            "<think>\U0001F914 hmm</think>\n"
            + "<respond>\U0001F389 hi \U0001F44B there \U0001F3F3️</respond>\n"
            + "<continue>false</continue>");

        Assert.True(turn.ParsedSuccessfully);
        Assert.Equal(2, turn.Actions.Count);
        Assert.Single(turn.Actions, a => a.Action == ModelAction.RespondToUser);
        Assert.Equal("\U0001F389 hi \U0001F44B there \U0001F3F3️", turn.Actions[1].Data?.GetValue<string>());
    }

    [Fact]
    public void AnEmojiIsNotMistakenForATagName()
    {
        // The tag pattern is <\w+...>. .NET's \w is Unicode-aware, so it's worth pinning that it does
        // not extend to emoji (category So) — a "<\U0001F389>" in prose must not open a tag and swallow
        // the rest of the reply.
        var turn = Parser.Parse("<respond>a literal <\U0001F389> in prose</respond>");

        var reply = Assert.Single(turn.Actions);
        Assert.Equal("a literal <\U0001F389> in prose", reply.Data?.GetValue<string>());
    }
}
