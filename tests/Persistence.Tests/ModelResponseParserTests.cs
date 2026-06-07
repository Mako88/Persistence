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
        var result = Parser.Parse($$"""{ "action": "{{actionName}}", "continue": false, "data": "x" }""");

        Assert.True(result.ParsedSuccessfully);
        Assert.Equal(expected, result.Action);
    }

    [Fact]
    public void ParsesContinueAndData()
    {
        var result = Parser.Parse("""{ "action": "think", "continue": true, "data": "a thought" }""");

        Assert.True(result.Continue);
        Assert.Equal("a thought", result.Data?.GetValue<string>());
    }

    [Fact]
    public void FallsBackToRespondForNonJson()
    {
        var result = Parser.Parse("just some prose, not json");

        Assert.False(result.ParsedSuccessfully);
        Assert.Equal(ModelAction.RespondToUser, result.Action);
        Assert.Equal("just some prose, not json", result.Data?.GetValue<string>());
    }

    [Fact]
    public void FallsBackForUnknownAction()
    {
        var result = Parser.Parse("""{ "action": "teleport", "data": "x" }""");

        Assert.False(result.ParsedSuccessfully);
        Assert.Equal(ModelAction.RespondToUser, result.Action);
    }
}
