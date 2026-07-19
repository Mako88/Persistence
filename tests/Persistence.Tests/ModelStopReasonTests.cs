using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Reading the provider's stop reason. The pipeline acts on exactly one question — "was this output cut
/// off?" — and getting that wrong in either direction is costly: a missed truncation is silent (the peer
/// believes it finished), and a false one would nag on every ordinary turn.
/// </summary>
public class ModelStopReasonTests
{
    [Theory]
    [InlineData("max_tokens")]   // Anthropic
    [InlineData("length")]       // OpenAI family
    [InlineData("MAX_TOKENS")]   // casing shouldn't decide whether a peer is told it was cut off
    public void TruncationIsRecognisedAcrossProviderSpellings(string stopReason) =>
        Assert.True(ModelStopReason.IsTruncation(stopReason));

    [Theory]
    [InlineData("end_turn")]     // Anthropic: finished normally
    [InlineData("stop")]         // OpenAI family: finished normally
    [InlineData("tool_use")]
    [InlineData("refusal")]
    [InlineData(null)]           // provider reported none (local / out-of-band clients)
    [InlineData("")]
    [InlineData("something_new") ] // an unfamiliar value must not be read as truncation
    public void EverythingElseIsNotTruncation(string? stopReason) =>
        Assert.False(ModelStopReason.IsTruncation(stopReason));

    [Fact]
    public void TheTruncationNoticeNamesTheSettingAndTheConsequence()
    {
        var note = ModelStopReason.DescribeTruncation(32000);

        // Whoever reads this — a human or the peer it happened to — should be able to act on it without
        // first having to learn the pipeline: what was lost, which knob caused it, what to do.
        Assert.Contains("32,000", note);
        Assert.Contains("MaxOutputTokens", note);
        Assert.Contains("incomplete", note);
    }
}
