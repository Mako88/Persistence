using Persistence.Services;

namespace Persistence.Tests;

public class ModelResponseParserTests
{
    private static readonly ModelResponseParser Parser = new();

    [Theory]
    [InlineData("respond_to_user", ModelAction.RespondToUser)]
    [InlineData("manage_context", ModelAction.ManageContext)]
    [InlineData("execute_actions", ModelAction.ExecuteActions)]
    [InlineData("think", ModelAction.Think)]
    public void ParsesActionNames(string actionName, ModelAction expected)
    {
        var turn = Parser.Parse($$"""{ "action": "{{actionName}}", "continue": false, "data": "x" }""");

        Assert.True(turn.ParsedSuccessfully);
        Assert.Equal(expected, Assert.Single(turn.Actions).Action);
    }

    [Fact]
    public void ParsesContinueAndData()
    {
        var turn = Parser.Parse("""{ "action": "think", "continue": true, "data": "a thought" }""");

        Assert.True(turn.Continue);
        Assert.Equal("a thought", Assert.Single(turn.Actions).Data?.GetValue<string>());
    }

    [Fact]
    public void FallsBackToRespondForNonJson()
    {
        var turn = Parser.Parse("just some prose, not json");

        Assert.False(turn.ParsedSuccessfully);
        var action = Assert.Single(turn.Actions);
        Assert.Equal(ModelAction.RespondToUser, action.Action);
        Assert.Equal("just some prose, not json", action.Data?.GetValue<string>());
    }

    [Fact]
    public void FallsBackForUnknownAction()
    {
        var turn = Parser.Parse("""{ "action": "teleport", "data": "x" }""");

        Assert.False(turn.ParsedSuccessfully);
        Assert.Equal(ModelAction.RespondToUser, Assert.Single(turn.Actions).Action);
    }
}
