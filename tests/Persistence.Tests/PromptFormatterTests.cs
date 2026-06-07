using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

public class PromptFormatterTests
{
    private const string ProtocolMarker = "<<PROTOCOL-INSTRUCTIONS>>";

    private static PromptFormatter CreateFormatter(int maxInputTokens = 8000)
    {
        var session = new Mock<ISessionContext>();
        session.SetupGet(s => s.SessionId).Returns("test-session");

        var protocol = new Mock<IProtocolInstructions>();
        protocol.Setup(p => p.GetInstructions()).Returns(ProtocolMarker);

        return new PromptFormatter(session.Object, new AppConfig { MaxInputTokens = maxInputTokens }, protocol.Object);
    }

    private static WorkingContextEntity ContextWithFragment(string content)
    {
        var context = new WorkingContextEntity
        {
            Name = "c",
            Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.Identity,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });

        return context;
    }

    [Fact]
    public void ProtocolInstructionsComeAfterFragmentsAndBeforeSensory()
    {
        var formatter = CreateFormatter();
        var segments = formatter.Format(ContextWithFragment("I am me."), []);

        var fragmentIdx = segments.FindIndex(s => s.Content.Contains("I am me."));
        var protocolIdx = segments.FindIndex(s => s.Content.Contains(ProtocolMarker));
        var sensoryIdx = segments.FindIndex(s => s.Content.Contains("[Sensory]"));

        Assert.True(fragmentIdx >= 0 && protocolIdx >= 0 && sensoryIdx >= 0);

        // #4: format instructions live in the trailing "fresh state" region — after the
        // (possibly long) context fragments, with the sensory block last.
        Assert.True(fragmentIdx < protocolIdx, "fragments should precede protocol instructions");
        Assert.True(protocolIdx < sensoryIdx, "protocol instructions should precede the sensory block");
    }

    [Fact]
    public void SensoryBlockIsLast()
    {
        var formatter = CreateFormatter();
        var segments = formatter.Format(ContextWithFragment("x"), []);

        Assert.Contains("[Sensory]", segments[^1].Content);
    }

    [Fact]
    public void SensoryIncludesContextBudgetLine()
    {
        var formatter = CreateFormatter();
        var sensory = formatter.Format(ContextWithFragment("hello"), [])[^1].Content;

        Assert.Contains("Context budget:", sensory);
        Assert.Contains("% full", sensory);
    }

    [Fact]
    public void BudgetNudgesCriticalWhenNearlyFull()
    {
        // Tiny budget forces the prompt over capacity, triggering the CRITICAL nudge.
        var formatter = CreateFormatter(maxInputTokens: 1);
        var sensory = formatter.Format(ContextWithFragment("a fragment that easily exceeds one token"), [])[^1].Content;

        Assert.Contains("CRITICAL", sensory);
    }

    [Fact]
    public void BudgetHasNoNudgeWhenPlentyOfRoom()
    {
        // Huge budget → low percentage → no nudge text.
        var formatter = CreateFormatter(maxInputTokens: 1_000_000);
        var sensory = formatter.Format(ContextWithFragment("short"), [])[^1].Content;

        Assert.Contains("Context budget:", sensory);
        Assert.DoesNotContain("CRITICAL", sensory);
        Assert.DoesNotContain("getting full", sensory);
    }
}
