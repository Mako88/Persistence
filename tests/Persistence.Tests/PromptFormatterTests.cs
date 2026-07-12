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
        bool surfaceCommands = false, IModelPricingProvider? pricing = null)
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

        // Default: no pricing (cost line shows tokens only / is omitted before any call).
        var pricingProvider = pricing ?? new Mock<IModelPricingProvider>().Object;

        return new PromptFormatter(
            session.Object, config, protocol.Object, catalog.Object, tracker ?? new TokenUsageTracker(),
            windows.Object, pricingProvider, new Mock<IEventBus>().Object);
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
            new TokenUsageTracker(), windows.Object, new Mock<IModelPricingProvider>().Object,
            new Mock<IEventBus>().Object);
        return (formatter, session);
    }

    [Fact]
    public void MarksTheBoundaryBetweenPriorTurnsAndThisTurn()
    {
        var (formatter, session) = CreateFormatterWithSession(new AppConfig());
        var turnStart = DateTimeOffset.UtcNow;
        session.TurnStartedUtc = turnStart;

        var context = new WorkingContextEntity
        {
            Name = "c", Summary = "s", CreatedUtc = turnStart, LastModifiedUtc = turnStart,
        };
        // Two prior-turn fragments (before turn start), then two from this turn (at/after it).
        AddFrag(context, "prior action A", turnStart.AddMinutes(-10));
        AddFrag(context, "prior action B", turnStart.AddMinutes(-5));
        AddFrag(context, "this-turn user message", turnStart);
        AddFrag(context, "this-turn action result", turnStart.AddSeconds(2));

        var segments = formatter.Format(context, []);
        var contents = segments.Select(s => s.Content).ToList();

        var markerIdx = contents.FindIndex(c => c.Contains("=== THIS TURN"));
        var priorBIdx = contents.FindIndex(c => c.Contains("prior action B"));
        var thisMsgIdx = contents.FindIndex(c => c.Contains("this-turn user message"));

        Assert.True(markerIdx >= 0, "a THIS TURN marker should be inserted at the boundary");
        // Exactly one marker, sitting between the last prior-turn fragment and the first this-turn one.
        Assert.Single(contents, c => c.Contains("=== THIS TURN"));
        Assert.True(priorBIdx < markerIdx && markerIdx < thisMsgIdx);
    }

    [Fact]
    public void OmitsTheThisTurnMarkerWhenEverythingIsFromThisTurn()
    {
        // A first turn: no prior-turn fragments to delineate from, so no marker.
        var (formatter, session) = CreateFormatterWithSession(new AppConfig());
        var turnStart = DateTimeOffset.UtcNow;
        session.TurnStartedUtc = turnStart;

        var context = new WorkingContextEntity
        {
            Name = "c", Summary = "s", CreatedUtc = turnStart, LastModifiedUtc = turnStart,
        };
        AddFrag(context, "brand new message", turnStart);
        AddFrag(context, "brand new result", turnStart.AddSeconds(1));

        var segments = formatter.Format(context, []);

        Assert.DoesNotContain(segments, s => s.Content.Contains("=== THIS TURN"));
    }

    private static void AddFrag(WorkingContextEntity context, string content, DateTimeOffset createdUtc) =>
        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Importance = 1.0f, Confidence = 1.0f, Relevance = 1.0f,
            CreatedUtc = createdUtc, LastModifiedUtc = createdUtc,
        });

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
    public void SensoryShowsActionsTakenThisTurn()
    {
        var actions = new[]
        {
            "shell(web_search \"x\") → • a result",
            "add(content=…) → Added Personal fragment",
        };

        var sensory = CreateFormatter()
            .Format(ContextWithFragment("hi"), [], recentActions: actions)[^1].Content;

        Assert.Contains("Actions you've already taken this turn", sensory);
        Assert.Contains("web_search", sensory);
    }

    [Fact]
    public void BudgetWarningNamesTheSummarizeTool()
    {
        // A tiny budget makes the prompt read as over-capacity, triggering the nudge.
        var sensory = CreateFormatter(maxInputTokens: 10)
            .Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("summarize_fragments", sensory);
    }

    [Fact]
    public void SessionCostLineShowsDollarsWhenTheModelHasPricing()
    {
        var tracker = new TokenUsageTracker();
        tracker.AddUsage(new ModelUsage(1_000_000, 200_000)); // 1M in + 200k out

        var pricing = new Mock<IModelPricingProvider>();
        pricing.Setup(p => p.GetPricing(It.IsAny<string>())).Returns(new ModelPricing(5m, 25m));

        var sensory = CreateFormatter(tracker: tracker, model: "claude-opus-4-8", pricing: pricing.Object)
            .Format(ContextWithFragment("hi"), [])[^1].Content;

        // 1M×$5/M + 0.2M×$25/M = $5 + $5 = $10.00.
        Assert.Contains("Session cost (est.): ~$10.00", sensory);
        Assert.Contains("1,000,000 in + 200,000 out tokens", sensory);
    }

    [Fact]
    public void SessionCostReflectsPromptCacheReadDiscount()
    {
        var tracker = new TokenUsageTracker();
        // 1M cache-read tokens (billed at 10%) + 100k output; no uncached input.
        tracker.AddUsage(new ModelUsage(0, 100_000, CacheReadTokens: 1_000_000));

        var pricing = new Mock<IModelPricingProvider>();
        pricing.Setup(p => p.GetPricing(It.IsAny<string>())).Returns(new ModelPricing(5m, 25m));

        var sensory = CreateFormatter(tracker: tracker, model: "claude-opus-4-8", pricing: pricing.Object)
            .Format(ContextWithFragment("hi"), [])[^1].Content;

        // 1M×$5/M×0.1 (cache read) + 100k×$25/M (output) = $0.50 + $2.50 = $3.00.
        Assert.Contains("Session cost (est.): ~$3.00", sensory);
        Assert.Contains("(1,000,000 cached)", sensory); // caching is visible
    }

    [Fact]
    public void SessionCostLineShowsTokensOnlyWhenThereIsNoPricing()
    {
        var tracker = new TokenUsageTracker();
        tracker.AddUsage(new ModelUsage(500, 100));

        var pricing = new Mock<IModelPricingProvider>();
        pricing.Setup(p => p.GetPricing(It.IsAny<string>())).Returns((ModelPricing?)null);

        var sensory = CreateFormatter(tracker: tracker, model: "gemma", pricing: pricing.Object)
            .Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.Contains("Session usage: 500 in + 100 out tokens", sensory);
        Assert.Contains("no pricing configured for 'gemma'", sensory);
        Assert.DoesNotContain("$", sensory);
    }

    [Fact]
    public void NoCostLineBeforeAnyModelCall()
    {
        // A fresh tracker (no calls) has nothing to report — the line is omitted, not "$0.00".
        var sensory = CreateFormatter().Format(ContextWithFragment("hi"), [])[^1].Content;

        Assert.DoesNotContain("Session cost", sensory);
        Assert.DoesNotContain("Session usage", sensory);
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
        Assert.Contains("modified fragment #42", sensory); // verb-first, so a "what changed" suffix reads naturally
        Assert.Contains("created proposal #7", sensory);
    }

    [Fact]
    public void RecentChangesShowTheTypeForACreation()
    {
        var changes = new List<AuditLogEntity>
        {
            new()
            {
                SessionId = "s", EventType = AuditEventType.Created, TargetType = nameof(ContextFragmentEntity),
                TargetId = 50, SourceId = 1, CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                NewData = """{"FragmentType":7,"Content":"a value I hold about honesty","Importance":0.5}""",
            },
        };

        var sensory = CreateFormatter().Format(ContextWithFragment("hi"), [], recentChanges: changes)[^1].Content;

        Assert.Contains("created fragment #50", sensory);
        Assert.Contains("(Personal)", sensory); // FragmentType 7 humanized — the type, not the content
        // The created fragment's content isn't echoed here (it's already visible in context, and would
        // otherwise linger in the digest after the fragment is archived).
        Assert.DoesNotContain("a value I hold about honesty", sensory);
    }

    [Fact]
    public void RecentChangesShowFieldDiffsForAModification()
    {
        var changes = new List<AuditLogEntity>
        {
            new()
            {
                SessionId = "s", EventType = AuditEventType.Modified, TargetType = nameof(ContextFragmentEntity),
                TargetId = 42, SourceId = 1, CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                OldData = """{"Importance":0.5,"Confidence":0.5,"Content":"same"}""",
                NewData = """{"Importance":0.9,"Confidence":0.5,"Content":"same"}""",
            },
        };

        var sensory = CreateFormatter().Format(ContextWithFragment("hi"), [], recentChanges: changes)[^1].Content;

        Assert.Contains("modified fragment #42", sensory);
        Assert.Contains("importance 0.5→0.9", sensory); // shows what changed, old→new
    }

    [Fact]
    public void ActionsTakenThisTurnAreNumberedInOrder()
    {
        var actions = new[] { "exec(ls) → notes.txt research/", "read_file(plan.md) → # Plan…" };

        var sensory = CreateFormatter()
            .Format(ContextWithFragment("hi"), [], recentActions: actions)[^1].Content;

        Assert.Contains("1. exec(ls)", sensory);
        Assert.Contains("2. read_file(plan.md)", sensory);
    }

    [Fact]
    public void NoRecentChangesSectionWhenThereAreNone()
    {
        var sensory = CreateFormatter().Format(ContextWithFragment("hi"), [])[^1].Content;
        Assert.DoesNotContain("Recent changes", sensory);
    }

    [Fact]
    public void UnsavedFragmentShowsNewLabelNotZeroIdOrTransient()
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
        Assert.Contains("[new |", joined);       // id-0 fragment is labelled "new" (it persists at turn end)
        Assert.DoesNotContain("[transient", joined); // never "transient" — models read that as "won't persist"
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
