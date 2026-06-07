using Persistence.Utilities;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class FunctionCallParserTests
{
    private static JsonObject SingleCall(string input)
    {
        var calls = FunctionCallParser.Parse(input);
        var obj = Assert.IsType<JsonObject>(calls.Single());
        return obj;
    }

    private static (string Name, JsonObject Fields) Unwrap(JsonObject call)
    {
        var prop = call.Single();
        return (prop.Key, Assert.IsType<JsonObject>(prop.Value));
    }

    [Fact]
    public void ParsesScalarNamedArgs()
    {
        var (name, fields) = Unwrap(SingleCall("update(id=42, weight=0.9)"));

        Assert.Equal("update", name);
        Assert.Equal(42, fields["id"]!.GetValue<long>());
        Assert.Equal(0.9, fields["weight"]!.GetValue<double>());
    }

    [Fact]
    public void ParsesBoolAndQuotedString()
    {
        var (_, fields) = Unwrap(SingleCall("""add(is_protected=true, summary="a note")"""));

        Assert.True(fields["is_protected"]!.GetValue<bool>());
        Assert.Equal("a note", fields["summary"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesArrayArg()
    {
        var (_, fields) = Unwrap(SingleCall("""tag(id=1, tags=["a/b", "c/d"])"""));

        var tags = Assert.IsType<JsonArray>(fields["tags"]);
        Assert.Equal(["a/b", "c/d"], tags.Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public void TripleQuotedPreservesMultilineAndQuotesUnescaped()
    {
        var input = "add(content=\"\"\"\nLine one\nLine \"two\" with quotes\n\"\"\", importance=0.8)";
        var (_, fields) = Unwrap(SingleCall(input));

        Assert.Equal("Line one\nLine \"two\" with quotes", fields["content"]!.GetValue<string>());
        Assert.Equal(0.8, fields["importance"]!.GetValue<double>());
    }

    [Fact]
    public void QuotedStringHonorsEscapes()
    {
        var (_, fields) = Unwrap(SingleCall("""x(s="line1\nline2 \"q\" \\ end")"""));

        Assert.Equal("line1\nline2 \"q\" \\ end", fields["s"]!.GetValue<string>());
    }

    [Fact]
    public void BarewordBecomesStringWhenNonNumeric()
    {
        var (_, fields) = Unwrap(SingleCall("schedule(scheduled_for=2026-06-08T09:00Z)"));

        Assert.Equal("2026-06-08T09:00Z", fields["scheduled_for"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesMultipleCallsAcrossLines()
    {
        var calls = FunctionCallParser.Parse("update(id=1, weight=0.5)\nremove(id=2)");

        Assert.Equal(2, calls.Count);
        Assert.Equal("update", ((JsonObject)calls[0]!).Single().Key);
        Assert.Equal("remove", ((JsonObject)calls[1]!).Single().Key);
    }

    [Fact]
    public void NoArgCallParses()
    {
        var (name, fields) = Unwrap(SingleCall("list()"));

        Assert.Equal("list", name);
        Assert.Empty(fields);
    }

    [Fact]
    public void EmptyInputYieldsNoCalls()
    {
        Assert.Empty(FunctionCallParser.Parse("   \n  "));
    }

    [Fact]
    public void NegativeAndNullValues()
    {
        var (_, fields) = Unwrap(SingleCall("x(a=-3, b=null)"));

        Assert.Equal(-3, fields["a"]!.GetValue<long>());
        Assert.Null(fields["b"]);
    }

    [Fact]
    public void UnterminatedCallThrows()
    {
        Assert.Throws<FormatException>(() => FunctionCallParser.Parse("update(id=42"));
    }

    [Fact]
    public void TripleQuotedContainingCommasAndParens()
    {
        // Content with delimiters that would break a naive split must survive intact.
        var input = "remember(content=\"\"\"func(a, b) => { return [1, 2]; }\"\"\")";
        var (_, fields) = Unwrap(SingleCall(input));

        Assert.Equal("func(a, b) => { return [1, 2]; }", fields["content"]!.GetValue<string>());
    }
}
