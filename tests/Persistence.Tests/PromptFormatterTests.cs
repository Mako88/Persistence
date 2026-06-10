using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

public class PromptFormatterTests
{
    private const string ProtocolMarker = "<<PROTOCOL-INSTRUCTIONS>>";
    private const string CommandsMarker = "[Commands] <<COMMAND-CATALOG>>";

    private static PromptFormatter CreateFormatter(
        int maxInputTokens = 8000, ITokenUsageTracker? tracker = null, string model = "local",
        bool surfaceCommands = false)
    {
        var session = new Mock<ISessionContext>();
        session.SetupGet(s => s.SessionId).Returns("test-session");
        session.SetupGet(s => s.SurfaceCommandsEnabled).Returns(surfaceCommands);

        var protocol = new Mock<IProtocolInstructions>();
        protocol.Setup(p => p.GetInstructions()).Returns(ProtocolMarker);

        var catalog = new Mock<ICommandCatalog>();
        catalog.Setup(c => c.GetCompactListing()).Returns(CommandsMarker);

        var windows = new Mock<IContextWindowProvider>();
        windows.Setup(w => w.GetContextWindow(It.IsAny<string>())).Returns(200000);

        var config = new AppConfig { MaxInputTokens = maxInputTokens, Model = model };

        return new PromptFormatter(
            session.Object, config, protocol.Object, catalog.Object, tracker ?? new TokenUsageTracker(),
            windows.Object, new Mock<IEventBus>().Object);
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
            Relevance = 1.0f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });

        return context;
    }

    [Fact]
    public void AppendsCommandSegmentWhenSurfacingEnabled()
    {
        var segments = CreateFormatter(surfaceCommands: true)
            .Format(ContextWithFragment("hi"), []);

        var commandsIdx = segments.FindIndex(s => s.Content.Contains(CommandsMarker));
        var protocolIdx = segments.FindIndex(s => s.Content.Contains(ProtocolMarker));
        var sensoryIdx = segments.FindIndex(s => s.Content.Contains("[Sensory]"));

        Assert.True(commandsIdx >= 0, "command segment should be present when surfacing is enabled");
        // Ordered between the syntax rules and the sensory block (the altitude decision).
        Assert.True(protocolIdx < commandsIdx && commandsIdx < sensoryIdx);
    }

    [Fact]
    public void OmitsCommandSegmentWhenSurfacingDisabled()
    {
        var segments = CreateFormatter(surfaceCommands: false)
            .Format(ContextWithFragment("hi"), []);

        Assert.DoesNotContain(segments, s => s.Content.Contains(CommandsMarker));
        // The other trailing segments are unaffected.
        Assert.Contains(segments, s => s.Content.Contains(ProtocolMarker));
        Assert.Contains(segments, s => s.Content.Contains("[Sensory]"));
    }

    private static (PromptFormatter Formatter, SessionContext Session) CreateFormatterWithSession(AppConfig config)
    {
        var session = new SessionContext { SessionId = "s" };
        var protocol = new Mock<IProtocolInstructions>();
        protocol.Setup(p => p.GetInstructions()).Returns(ProtocolMarker);
        var catalog = new Mock<ICommandCatalog>();
        catalog.Setup(c => c.GetCompactListing()).Returns(CommandsMarker);
        var windows = new Mock<IContextWindowProvider>();
        windows.Setup(w => w.GetContextWindow(It.IsAny<string>())).Returns(200000);

        var formatter = new PromptFormatter(session, config, protocol.Object, catalog.Object,
            new TokenUsageTracker(), windows.Object, new Mock<IEventBus>().Object);
        return (formatter, session);
    }

    [Fact]
    public void SensoryAnnouncesTheActiveLocalPeerWithDescription()
    {
        var config = new AppConfig { LocalPeers = [new LocalPeerProfile { Name = "John", Description = "the steward" }] };
        var (formatter, session) = CreateFormatterWithSession(config);
        session.ActiveLocalPeerName = "John";

        var sensory = formatter.Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("You are speaking with: John — the steward", sensory);
    }

    [Fact]
    public void SensoryFlagsAPeerSwitch()
    {
        var (formatter, session) = CreateFormatterWithSession(new AppConfig());

        session.ActiveLocalPeerName = "John";
        formatter.Format(ContextWithFragment("hi"), []); // establishes John as the last-seen peer

        session.ActiveLocalPeerName = "Claude";
        var sensory = formatter.Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("You are speaking with: Claude", sensory);
        Assert.Contains("(changed from John)", sensory);
    }

    [Fact]
    public void SensoryBlockReportsAppAndSystemUptime()
    {
        var sensory = CreateFormatter()
            .Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("App uptime:", sensory);
        Assert.Contains("System uptime:", sensory);
    }

    [Fact]
    public void RecentChangesAppearInTheSensoryBlockHumanized()
    {
        var changes = new List<AuditLogEntity>
        {
            new() { SessionId = "s", EventType = AuditEventType.Modified, TargetType = nameof(ContextFragmentEntity), TargetId = 42, SourceId = 1, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), LastModifiedUtc = DateTimeOffset.UtcNow },
            new() { SessionId = "s", EventType = AuditEventType.Created, TargetType = nameof(ProposalEntity), TargetId = 7, SourceId = 1, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5), LastModifiedUtc = DateTimeOffset.UtcNow },
        };

        var sensory = CreateFormatter()
            .Format(ContextWithFragment("hi"), [], recentChanges: changes)[^1].Content;

        Assert.Contains("Recent changes to your memory:", sensory);
        Assert.Contains("fragment #42 modified", sensory);
        Assert.Contains("proposal #7 created", sensory);
    }

    [Fact]
    public void NoRecentChangesSectionWhenThereAreNone()
    {
        var sensory = CreateFormatter().Format(ContextWithFragment("hi"), [])[^1].Content;
        Assert.DoesNotContain("Recent changes", sensory);
    }

    [Fact]
    public void TransientFragmentShowsTransientLabelNotZeroId()
    {
        var now = DateTimeOffset.UtcNow;
        var context = new WorkingContextEntity { Name = "c", Summary = "s", CreatedUtc = now, LastModifiedUtc = now };

        context.AddFragment(new WeightedContextFragment
        {
            Id = 42, FragmentType = ContextFragmentType.Identity, Status = ContextFragmentStatus.Active,
            Content = "a persisted note", Importance = 1.0f, Confidence = 1.0f, Relevance = 1.0f,
            CreatedUtc = now, LastModifiedUtc = now,
        });
        context.AddFragment(new WeightedContextFragment
        {
            Id = 0, FragmentType = ContextFragmentType.ActionResponse, Status = ContextFragmentStatus.Active,
            Content = "a transient command result", Importance = 1.0f, Confidence = 1.0f, Relevance = 1.0f,
            CreatedUtc = now, LastModifiedUtc = now,
        });

        var joined = string.Join("\n", CreateFormatter().Format(context, []).Select(s => s.Content));

        Assert.Contains("#42", joined);          // persisted fragment keeps its id
        Assert.Contains("transient", joined);    // id-0 fragment is labelled, not addressable
        Assert.DoesNotContain("#0", joined);     // never a misleading #0
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

    [Fact]
    public void UsesModelContextWindowWhenNoBudgetConfigured()
    {
        // MaxInputTokens = 0 → effective budget falls back to the model window (mocked to 200000).
        var formatter = CreateFormatter(maxInputTokens: 0);
        var sensory = formatter.Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("/200000 tokens", sensory);
    }

    [Fact]
    public void CalibratesEstimateFromRealUsage()
    {
        // Tracker says last turn the real token count was 2x our estimate; the displayed "used"
        // figure should be scaled up accordingly versus an uncalibrated formatter.
        var tracker = new TokenUsageTracker();

        var uncalibrated = CreateFormatter(maxInputTokens: 1_000_000)
            .Format(ContextWithFragment("calibration sample text"), [])[^1].Content;
        var baseUsed = ExtractUsed(uncalibrated);

        tracker.Record(realInputTokens: 2000, estimatedInputTokens: 1000); // ratio 2.0
        var calibrated = CreateFormatter(maxInputTokens: 1_000_000, tracker: tracker)
            .Format(ContextWithFragment("calibration sample text"), [])[^1].Content;
        var calibratedUsed = ExtractUsed(calibrated);

        Assert.Equal(baseUsed * 2, calibratedUsed);
    }

    private static int ExtractUsed(string sensory)
    {
        var m = System.Text.RegularExpressions.Regex.Match(sensory, @"~(\d+)/\d+ tokens");
        Assert.True(m.Success, "budget line not found");
        return int.Parse(m.Groups[1].Value);
    }
}
