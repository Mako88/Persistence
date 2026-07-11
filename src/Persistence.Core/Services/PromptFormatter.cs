using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Diagnostics;
using System.Text;

namespace Persistence.Services;

/// <summary>
/// Formats a working context into an ordered list of <see cref="PromptSegment"/>s.
/// Each fragment is rendered with a metadata header and mapped to a section and source.
/// A sensory block is appended at the end. Fragment order is preserved.
/// </summary>
[Singleton]
public class PromptFormatter : IPromptFormatter
{
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;
    private readonly IProtocolInstructions protocolInstructions;
    private readonly ICommandCatalog commandCatalog;
    private readonly ITokenUsageTracker usageTracker;
    private readonly IContextWindowProvider contextWindows;
    private readonly IModelPricingProvider pricing;
    private readonly IEventBus eventBus;

    private DateTimeOffset? lastFormatUtc;

    // The local-peer name surfaced last turn, to flag a switch so the peer can load the right relations.
    private string? lastLocalPeerName;

    // When this process started, for the app-uptime line. Captured once; falls back to load time if the
    // process start time isn't readable on the platform.
    private static readonly DateTimeOffset ProcessStartUtc = ReadProcessStartUtc();

    private static DateTimeOffset ReadProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTimeOffset.UtcNow; }
    }

    /// <summary>
    /// Constructor
    /// </summary>
    public PromptFormatter(
        ISessionContext sessionContext,
        IAppConfig config,
        IProtocolInstructions protocolInstructions,
        ICommandCatalog commandCatalog,
        ITokenUsageTracker usageTracker,
        IContextWindowProvider contextWindows,
        IModelPricingProvider pricing,
        IEventBus eventBus)
    {
        this.sessionContext = sessionContext;
        this.config = config;
        this.protocolInstructions = protocolInstructions;
        this.commandCatalog = commandCatalog;
        this.usageTracker = usageTracker;
        this.contextWindows = contextWindows;
        this.pricing = pricing;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Renders the working context into an ordered list of prompt segments — one per fragment,
    /// followed by the protocol instructions and a sensory block with time, session, and budget info
    /// </summary>
    public List<PromptSegment> Format(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0,
        IReadOnlyList<AuditLogEntity>? recentChanges = null,
        IReadOnlyList<string>? recentActions = null,
        string? archiveNote = null)
    {
        var fragments = context.ContextFragments.Values;
        var segments = new List<PromptSegment>();

        // Fragments are ordered oldest→newest. Everything created at/after the turn start belongs to
        // THIS turn (the new user message, this turn's action results and thoughts); everything before
        // it is prior turns. A single marker at that boundary gives the peer a structural "now" line so
        // it stops reading already-completed actions as if they're happening in the current turn. Only
        // emitted where prior-turn fragments actually give way to this-turn ones (not on a first turn
        // where everything is new).
        var turnStart = sessionContext.TurnStartedUtc;
        var markerEmitted = false;
        var sawPriorTurn = false;

        foreach (var fragment in fragments)
        {
            var isThisTurn = fragment.CreatedUtc >= turnStart;

            if (!markerEmitted && isThisTurn && sawPriorTurn)
            {
                segments.Add(new PromptSegment
                {
                    Source = "System",
                    Content = "=== THIS TURN — everything below arrived during the current turn; everything above is from earlier turns ===",
                });
                markerEmitted = true;
            }

            if (!isThisTurn)
            {
                sawPriorTurn = true;
            }

            segments.Add(new PromptSegment
            {
                Source = ResolveSourceName(fragment),
                Content = FormatFragment(fragment),
            });
        }

        // Response-format instructions and the sensory block are injected at the END, not the
        // top: format adherence degrades the further the rules sit from the generation point,
        // and that worsens as the working context grows. Keeping this operational scaffolding
        // in the trailing "fresh state" region keeps it salient. (Identity/persona lives in the
        // fragments above, where stable framing belongs.) Injected per-prompt rather than
        // persisted, so the format can change via config without reseeding.
        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = protocolInstructions.GetInstructions(),
        });

        // The compact command list sits between the syntax rules (above) and the sensory block
        // (below): the instructions teach *how* to write a command, this enumerates *which* commands
        // exist. Default on so the peer always knows its options, but toggleable — the local model
        // re-ingests the whole prompt every turn, so a peer that has the commands memorised can hide
        // this to save tokens (full per-field schemas remain available via list()). Kept above the
        // budget estimate below so its token cost is reflected in the readout.
        if (sessionContext.SurfaceCommandsEnabled)
        {
            segments.Add(new PromptSegment
            {
                Source = "System",
                Content = commandCatalog.GetCompactListing(),
            });
        }

        // Estimate the prompt's token usage so the peer can see how full its context is. Summed
        // over everything assembled so far plus the sensory block's own (roughly fixed) size, so
        // the figure reflects the prompt the peer is about to act on.
        var usedTokens = TokenEstimator.Estimate(segments.Select(s => s.Content));

        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = FormatSensory(iteration, maxIterations, availableTags, usedTokens, recentChanges, recentActions, archiveNote),
        });

        lastFormatUtc = DateTimeOffset.UtcNow;

        return segments;
    }

    #region Private

    private static string ResolveSourceName(WeightedContextFragment fragment)
    {
        if (fragment.Sources.Count > 0)
        {
            return fragment.Sources[0].Name ?? fragment.Sources[0].SourceType.ToString();
        }

        return "System";
    }

    private static string FormatFragment(WeightedContextFragment fragment)
    {
        var header = BuildFragmentHeader(fragment);

        // A collapsed fragment with a summary renders as just its summary (to save space). If it's
        // collapsed but has no summary, fall back to full content — there's nothing shorter to show.
        var body = fragment.Collapsed && !string.IsNullOrWhiteSpace(fragment.Summary)
            ? $"(collapsed) {fragment.Summary}"
            : fragment.Content;

        return $"{header}\n{body}";
    }

    private static string BuildFragmentHeader(WeightedContextFragment fragment)
    {
        // A fragment created this turn has no id until it's saved at turn's end. Label it "new" — not
        // "transient", which models read as "ephemeral / won't persist" and then confabulate about
        // (e.g. concluding thought-persistence is broken when in fact these fragments do persist, as
        // the #ids on prior-turn thoughts right above them show). Never a misleading #0 it might try
        // to address.
        var idLabel = fragment.Id > 0 ? $"#{fragment.Id}" : "new";
        var meta = $"[{idLabel} | {fragment.FragmentType} | R:{fragment.Relevance:F1} I:{fragment.Importance:F1} C:{fragment.Confidence:F1}";

        if (fragment.IsProtected)
        {
            meta += " | protected";
        }

        if (fragment.Collapsed && !string.IsNullOrWhiteSpace(fragment.Summary))
        {
            meta += " | collapsed";
        }

        meta += "]";

        return meta;
    }

    private string FormatSensory(
        int iteration,
        int maxIterations,
        IEnumerable<TagEntity> availableTags,
        int usedTokens,
        IReadOnlyList<AuditLogEntity>? recentChanges,
        IReadOnlyList<string>? recentActions,
        string? archiveNote)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;

        var sb = new StringBuilder();
        sb.AppendLine("[Sensory]");
        sb.AppendLine($"Current time (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current time (local): {localNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Session: {sessionContext.SessionId}");

        var speakingWith = FormatSpeakingWith();
        if (speakingWith.Length > 0)
        {
            sb.AppendLine(speakingWith);
        }

        sb.AppendLine($"App uptime: {FormatDuration(now - ProcessStartUtc)} | System uptime: {FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        sb.AppendLine(FormatContextBudget(usedTokens));

        var cost = FormatSessionCost();
        if (cost.Length > 0)
        {
            sb.AppendLine(cost);
        }

        if (lastFormatUtc.HasValue)
        {
            var elapsed = now - lastFormatUtc.Value;
            sb.AppendLine($"Time since last prompt: {FormatElapsed(elapsed)}");
        }

        if (maxIterations > 0 && iteration > 0)
        {
            sb.AppendLine($"Continue iteration: {iteration}/{maxIterations}");
        }

        // What the peer has already done THIS turn (across continue-iterations), with each result's
        // gist — so it builds on its own recent actions instead of re-planning them. The full results
        // are the ActionResponse fragments above; this is the salient pointer near the generation point.
        if (recentActions is { Count: > 0 })
        {
            sb.AppendLine("Actions you've already taken this turn (full results are in your context above — don't repeat these):");
            foreach (var action in recentActions.TakeLast(15))
            {
                sb.AppendLine($"  - {action}");
            }
        }

        if (!string.IsNullOrEmpty(archiveNote))
        {
            sb.AppendLine(archiveNote);
        }

        if (recentChanges is { Count: > 0 })
        {
            sb.AppendLine("Recent changes to your memory:");
            foreach (var change in recentChanges)
            {
                sb.AppendLine($"  - [{FormatElapsed(now - change.CreatedUtc)} ago] {HumanizeTarget(change.TargetType)} #{change.TargetId} {change.EventType.ToString().ToLowerInvariant()}");
            }
        }

        var tagList = FormatTagList(availableTags);

        if (tagList.Length > 0)
        {
            sb.AppendLine($"Available tags: {tagList}");
        }
        else
        {
            sb.AppendLine("No tags exist yet");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The "you are speaking with: …" line. Adds the peer's configured description if known, and flags
    /// a switch from the previous turn so the peer can load the relevant relational fragments. Empty
    /// when no active local peer is set.
    /// </summary>
    private string FormatSpeakingWith()
    {
        var name = sessionContext.ActiveLocalPeerName;

        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var description = config.LocalPeers?
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Description;

        var line = $"You are speaking with: {name}";

        if (!string.IsNullOrWhiteSpace(description))
        {
            line += $" — {description}";
        }

        if (lastLocalPeerName != null && !string.Equals(lastLocalPeerName, name, StringComparison.OrdinalIgnoreCase))
        {
            line += $" (changed from {lastLocalPeerName})";
        }

        lastLocalPeerName = name;
        return line;
    }

    /// <summary>Maps an audit target's stored entity-type name to a peer-friendly word.</summary>
    private static string HumanizeTarget(string targetType) => targetType switch
    {
        nameof(ContextFragmentEntity) => "fragment",
        nameof(WorkingContextEntity) => "context",
        nameof(ScheduledEventEntity) => "event",
        nameof(ProposalEntity) => "proposal",
        nameof(SourceEntity) => "source",
        nameof(TagEntity) => "tag",
        _ => targetType,
    };

    private static string FormatTagList(IEnumerable<TagEntity> rootTags)
    {
        var paths = new List<string>();

        foreach (var root in rootTags)
        {
            paths.Add(root.Name);

            foreach (var child in root.ChildTags)
            {
                paths.Add($"{root.Name}/{child.Name}");
            }
        }

        return string.Join(", ", paths);
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 60 => $"{elapsed.TotalSeconds:F0}s",
        < 3600 => $"{elapsed.TotalMinutes:F0}m {elapsed.Seconds}s",
        _ => $"{elapsed.TotalHours:F0}h {elapsed.Minutes}m",
    };

    /// <summary>Like <see cref="FormatElapsed"/> but surfaces days, for long-running uptimes.</summary>
    private static string FormatDuration(TimeSpan t) => t.TotalDays switch
    {
        >= 1 => $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m",
        _ when t.TotalHours >= 1 => $"{(int)t.TotalHours}h {t.Minutes}m",
        _ => $"{(int)t.TotalMinutes}m {t.Seconds}s",
    };

    /// <summary>
    /// Renders the context-budget line: how full the prompt is against the effective budget, with
    /// an actionable nudge as it fills so the peer can curate (summarize / archive) before context
    /// would be silently dropped.
    ///
    /// The numerator is the raw char-estimate of the current prompt, **calibrated** by the ratio of
    /// real-to-estimated tokens from the previous call (so it tracks the provider's actual
    /// tokenizer rather than the ~4 chars/token heuristic alone). The denominator is the effective
    /// budget: the configured <see cref="IAppConfig.MaxInputTokens"/> if set (a tighter working
    /// limit), else the model's full context window.
    /// </summary>
    private string FormatContextBudget(int estimatedTokens)
    {
        var used = usageTracker.Calibrate(estimatedTokens);
        var budget = EffectiveBudget();

        if (budget <= 0)
        {
            eventBus.FireAndForget(this, new ContextBudgetUpdated(used, 0, 0));
            return $"Context: ~{used} tokens used (no budget configured)";
        }

        var percent = (int)Math.Round(100.0 * used / budget);

        // Surface the usage to any UI (e.g. a status-bar gauge); best-effort, off the formatting path.
        eventBus.FireAndForget(this, new ContextBudgetUpdated(used, budget, percent));

        var line = $"Context budget: ~{used}/{budget} tokens (~{percent}% full)";

        var nudge = percent switch
        {
            >= 95 => " — CRITICAL: at capacity. Use summarize_fragments(ids, summary) to fold low-relevance fragments into one summary (the originals are archived, recoverable), or remove some — now, or older context may be dropped.",
            >= 80 => " — getting full: consider summarize_fragments(ids, summary) to fold several fragments into a summary and archive the originals, or remove low-relevance ones soon.",
            >= 60 => " — over half full; keep an eye on what's worth keeping (summarize_fragments / remove when useful).",
            _ => "",
        };

        return line + nudge;
    }

    /// <summary>
    /// Renders the running-cost line: this session's cumulative token usage and, when the active model
    /// has a known price, an estimated dollar cost. Token counts are estimates (input calibrated to the
    /// provider's tokenizer), so the figure is a running approximation, not a bill. Empty before the
    /// first model call, or when there's nothing to show. Fires <see cref="SessionCostUpdated"/> for any
    /// UI. Cost knowledge is entirely in <see cref="IModelPricingProvider"/> — this method is model-agnostic.
    /// </summary>
    private string FormatSessionCost()
    {
        var input = usageTracker.TotalInputTokens;
        var output = usageTracker.TotalOutputTokens;
        var calls = usageTracker.CallCount;

        if (calls == 0)
        {
            return string.Empty; // nothing spent yet
        }

        var usage = $"{input:N0} in + {output:N0} out tokens · {calls} call{(calls == 1 ? "" : "s")}";
        var rate = pricing.GetPricing(config.Model);

        if (rate is { } r)
        {
            var cost = input / 1_000_000m * r.InputPerMillion + output / 1_000_000m * r.OutputPerMillion;
            eventBus.FireAndForget(this, new SessionCostUpdated(cost, input, output, calls));
            return $"Session cost (est.): ~{FormatUsd(cost)} · {usage}";
        }

        eventBus.FireAndForget(this, new SessionCostUpdated(null, input, output, calls));
        return $"Session usage: {usage} (no pricing configured for '{config.Model}' — set it in model_pricing.json to see cost)";
    }

    /// <summary>Formats a USD amount, widening precision for sub-cent running totals so early turns aren't all "$0.00".</summary>
    private static string FormatUsd(decimal cost) => cost < 0.01m ? $"${cost:0.0000}" : $"${cost:0.00}";

    /// <summary>
    /// The effective token budget: the configured working limit if positive, otherwise the model's
    /// full context window resolved from the model→window map.
    /// </summary>
    private int EffectiveBudget() =>
        config.MaxInputTokens > 0
            ? config.MaxInputTokens
            : contextWindows.GetContextWindow(config.Model);

    #endregion
}
