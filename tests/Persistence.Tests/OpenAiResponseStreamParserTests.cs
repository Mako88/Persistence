using Persistence.Services.Streaming;

namespace Persistence.Tests;

public class OpenAiResponseStreamParserTests
{
    /// <summary>Yields the given decoded SSE data payloads as an async stream.</summary>
    private static async IAsyncEnumerable<string> Payloads(params string[] payloads)
    {
        foreach (var p in payloads)
        {
            yield return p;
        }

        await Task.CompletedTask;
    }

    private static async Task<List<ModelStreamEvent>> Parse(params string[] payloads)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var e in OpenAiResponseStreamParser.ParseAsync(Payloads(payloads)))
        {
            events.Add(e);
        }
        return events;
    }

    [Fact]
    public async Task MapsOutputTextDelta()
    {
        var e = Assert.Single(await Parse("""{"type":"response.output_text.delta","delta":"Hel"}"""));
        Assert.Equal(ModelStreamEventKind.OutputTextDelta, e.Kind);
        Assert.Equal("Hel", e.Text);
    }

    [Fact]
    public async Task MapsReasoningSummaryDelta()
    {
        var e = Assert.Single(await Parse("""{"type":"response.reasoning_summary_text.delta","delta":"thinking"}"""));
        Assert.Equal(ModelStreamEventKind.ReasoningSummaryDelta, e.Kind);
        Assert.Equal("thinking", e.Text);
    }

    [Fact]
    public async Task MapsCompleted()
    {
        Assert.Equal(ModelStreamEventKind.Completed,
            Assert.Single(await Parse("""{"type":"response.completed"}""")).Kind);
    }

    [Fact]
    public async Task SkipsUnknownEventTypes()
    {
        Assert.Empty(await Parse("""{"type":"response.created"}"""));
    }

    [Fact]
    public async Task SkipsDoneSentinelAndMalformedPayloads()
    {
        Assert.Empty(await Parse("[DONE]", "not json"));
    }

    [Fact]
    public async Task ConcatenatedOutputDeltasReconstructFullText()
    {
        var events = await Parse(
            """{"type":"response.reasoning_summary_text.delta","delta":"hmm"}""",
            """{"type":"response.output_text.delta","delta":"Hello, "}""",
            """{"type":"response.output_text.delta","delta":"world"}""",
            """{"type":"response.completed"}""");

        var text = string.Concat(events
            .Where(e => e.Kind == ModelStreamEventKind.OutputTextDelta)
            .Select(e => e.Text));

        Assert.Equal("Hello, world", text);
        Assert.Equal(ModelStreamEventKind.Completed, events[^1].Kind);
        Assert.Contains(events, e => e.Kind == ModelStreamEventKind.ReasoningSummaryDelta);
    }
}
