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
    public void NumbersAreReadableAsFloatIntAndLong()
    {
        // Command fields read numeric values as various CLR types (e.g. importance is float).
        // Parsed numbers must support all of them, like JsonNode.Parse does — a CLR-typed
        // JsonValue<double> would throw on GetValue<float>().
        var (_, fields) = Unwrap(SingleCall("add(importance=0.7, count=3)"));

        Assert.Equal(0.7f, fields["importance"]!.GetValue<float>());
        Assert.Equal(0.7, fields["importance"]!.GetValue<double>());
        Assert.Equal(3, fields["count"]!.GetValue<int>());
        Assert.Equal(3L, fields["count"]!.GetValue<long>());
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
    public void TripleQuotedTakesBackslashNLiterally()
    {
        // The trap the protocol instructions now warn about: triple-quoted content is literal, so a
        // peer that writes "\n" expecting a newline gets two characters stored in its memory instead.
        // Pinned because the instructions promise exactly this, and a change here would silently make
        // the prompt a lie.
        var (_, fields) = Unwrap(SingleCall("add(content=\"\"\"line one\\nline two\"\"\")"));

        Assert.Equal(@"line one\nline two", fields["content"]!.GetValue<string>());
        Assert.DoesNotContain('\n', fields["content"]!.GetValue<string>());
    }

    [Fact]
    public void QuotedStringHonorsEscapes()
    {
        // ...whereas a single-quoted string does process them — the other half of what the
        // instructions tell a peer.
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
    public void UnterminatedCallBecomesErrorCommand()
    {
        // Resilient parsing: a malformed call surfaces as an __error__ command rather than throwing,
        // so the rest of the turn (and the peer's other tags) survive.
        var call = SingleCall("update(id=42");

        Assert.Equal(FunctionCallParser.ErrorCommandName, call.Single().Key);
    }

    [Fact]
    public void MalformedCallDoesNotKillSiblings()
    {
        // One bad call in the middle must not discard the valid calls around it.
        var calls = FunctionCallParser.Parse("add(content=\"ok\")\nbad(@=2)\nremove(id=3)");

        Assert.Equal(3, calls.Count);
        Assert.Equal("add", ((JsonObject)calls[0]!).Single().Key);
        Assert.Equal(FunctionCallParser.ErrorCommandName, ((JsonObject)calls[1]!).Single().Key);
        Assert.Equal("remove", ((JsonObject)calls[2]!).Single().Key);
    }

    [Fact]
    public void ColonInsteadOfEqualsGivesActionableMessage()
    {
        var call = SingleCall("update(id: 42)");
        var (name, fields) = Unwrap(call);

        Assert.Equal(FunctionCallParser.ErrorCommandName, name);
        // The message should steer toward name=value, and echo the offending text.
        Assert.Contains("name=value", fields["message"]!.GetValue<string>());
        Assert.Equal("update(id: 42)", fields["text"]!.GetValue<string>());
    }

    [Fact]
    public void ErrorSnippetIsCappedAndSingleLine()
    {
        var longArg = new string('x', 200);
        // Include a newline so the single-line collapse is actually exercised, not assumed.
        var call = SingleCall($"add(content={longArg}\nmore=stuff"); // unterminated bareword call (no close paren)
        var (_, fields) = Unwrap(call);

        var text = fields["text"]!.GetValue<string>();
        Assert.True(text.Length <= 81, $"snippet should be capped, was {text.Length}");
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
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
