using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
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
    private readonly ITokenUsageTracker usageTracker;
    private readonly IContextWindowProvider contextWindows;

    private DateTimeOffset? lastFormatUtc;

    /// <summary>
    /// Constructor
    /// </summary>
    public PromptFormatter(
        ISessionContext sessionContext,
        IAppConfig config,
        IProtocolInstructions protocolInstructions,
        ITokenUsageTracker usageTracker,
        IContextWindowProvider contextWindows)
    {
        this.sessionContext = sessionContext;
        this.config = config;
        this.protocolInstructions = protocolInstructions;
        this.usageTracker = usageTracker;
        this.contextWindows = contextWindows;
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
        IReadOnlyList<AuditLogEntity>? recentChanges = null)
    {
        var fragments = context.ContextFragments.Values;
        var segments = new List<PromptSegment>();

        foreach (var fragment in fragments)
        {
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

        // Estimate the prompt's token usage so the peer can see how full its context is. Summed
        // over everything assembled so far plus the sensory block's own (roughly fixed) size, so
        // the figure reflects the prompt the peer is about to act on.
        var usedTokens = TokenEstimator.Estimate(segments.Select(s => s.Content));

        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = FormatSensory(iteration, maxIterations, availableTags, usedTokens, recentChanges),
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
        // A not-yet-persisted fragment (e.g. a transient command result) has no usable id; show
        // "transient" rather than a misleading #0 the peer might try to address.
        var idLabel = fragment.Id > 0 ? $"#{fragment.Id}" : "transient";
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
        IReadOnlyList<AuditLogEntity>? recentChanges)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;

        var sb = new StringBuilder();
        sb.AppendLine("[Sensory]");
        sb.AppendLine($"Current time (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current time (local): {localNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Session: {sessionContext.SessionId}");
        sb.AppendLine(FormatContextBudget(usedTokens));

        if (lastFormatUtc.HasValue)
        {
            var elapsed = now - lastFormatUtc.Value;
            sb.AppendLine($"Time since last prompt: {FormatElapsed(elapsed)}");
        }

        if (maxIterations > 0 && iteration > 0)
        {
            sb.AppendLine($"Continue iteration: {iteration}/{maxIterations}");
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
        var used = Calibrate(estimatedTokens);
        var budget = EffectiveBudget();

        if (budget <= 0)
        {
            return $"Context: ~{used} tokens used (no budget configured)";
        }

        var percent = (int)Math.Round(100.0 * used / budget);
        var line = $"Context budget: ~{used}/{budget} tokens (~{percent}% full)";

        var nudge = percent switch
        {
            >= 95 => " — CRITICAL: at capacity. Summarize or archive low-relevance fragments now, or older context may be dropped.",
            >= 80 => " — getting full: consider summarizing or archiving low-relevance fragments soon.",
            >= 60 => " — over half full; keep an eye on what's worth keeping in context.",
            _ => "",
        };

        return line + nudge;
    }

    /// <summary>
    /// Adjusts the raw estimate by the previous call's real:estimated ratio, when available, so the
    /// figure reflects the provider's actual tokenization. Returns the raw estimate on turn one or
    /// when no real usage has been recorded yet.
    /// </summary>
    private int Calibrate(int estimatedTokens)
    {
        if (usageTracker.LastInputTokens is { } real
            && usageTracker.LastEstimatedTokens is { } est
            && est > 0)
        {
            return (int)Math.Round(estimatedTokens * ((double)real / est));
        }

        return estimatedTokens;
    }

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
