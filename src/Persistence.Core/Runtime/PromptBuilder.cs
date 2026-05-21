using System.Text;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;

namespace Persistence.Runtime;

/// <summary>
/// Builds a prompt from a working context for submission to the model. Each fragment
/// is rendered with a uniform metadata header (ID, type, weight, importance, confidence)
/// followed by its content. System-type fragments are routed to the system prompt;
/// all others go to the main prompt with a sensory block appended at the end.
/// </summary>
[Singleton]
public class PromptBuilder : IPromptBuilder
{
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;

    private DateTimeOffset? lastBuildUtc;

    /// <summary>
    /// Constructor
    /// </summary>
    public PromptBuilder(ISessionContext sessionContext, IAppConfig config)
    {
        this.sessionContext = sessionContext;
        this.config = config;
    }

    /// <summary>
    /// Builds a prompt string from the working context. System fragments are separated
    /// into the system prompt; all other fragments are formatted uniformly in the main
    /// prompt with a sensory block appended at the end.
    /// </summary>
    public (string prompt, string? systemPrompt) Build(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0)
    {
        var fragments = context.ContextFragments.Values;

        var systemFragments = fragments
            .Where(f => f.FragmentType == ContextFragmentType.System)
            .ToList();

        var promptFragments = fragments
            .Where(f => f.FragmentType != ContextFragmentType.System)
            .ToList();

        var systemPrompt = BuildSystemPrompt(systemFragments);
        var prompt = BuildMainPrompt(promptFragments, systemPrompt, availableTags, iteration, maxIterations);

        lastBuildUtc = DateTimeOffset.UtcNow;

        return (prompt, systemPrompt);
    }

    // ── Private ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the main prompt by formatting each non-System fragment uniformly
    /// and appending a sensory block at the end
    /// </summary>
    private string BuildMainPrompt(
        List<WeightedContextFragment> fragments,
        string? systemPrompt,
        IEnumerable<TagEntity> availableTags,
        int iteration,
        int maxIterations)
    {
        var sb = new StringBuilder();

        foreach (var fragment in fragments)
        {
            sb.AppendLine(FormatFragment(fragment));
            sb.AppendLine("--");
            sb.AppendLine();
        }

        // Measure token usage before adding sensory so the budget estimate
        // reflects the content the model actually needs to process
        var promptChars = sb.Length + (systemPrompt?.Length ?? 0);
        var estimatedTokens = EstimateTokens(promptChars);

        sb.Append(FormatSensory(iteration, maxIterations, estimatedTokens, availableTags));

        return sb.ToString();
    }

    /// <summary>
    /// Builds the system prompt from System-type fragments, or returns null if
    /// there are no system fragments
    /// </summary>
    private string? BuildSystemPrompt(List<WeightedContextFragment> fragments)
    {
        if (fragments.Count == 0)
        {
            return null;
        }

        return string.Join("\n\n--\n\n", fragments.Select(FormatFragment));
    }

    /// <summary>
    /// Formats a single fragment with a metadata header line and its content
    /// </summary>
    private string FormatFragment(WeightedContextFragment fragment)
    {
        var header = BuildFragmentHeader(fragment);
        return $"{header}\n{fragment.Content}";
    }

    /// <summary>
    /// Builds the metadata header line for a fragment, including ID, type, role
    /// (for chat messages), weight, importance, confidence, and protected status
    /// </summary>
    private string BuildFragmentHeader(WeightedContextFragment fragment)
    {
        var typeName = fragment.FragmentType.ToString();

        // For chat messages, include the role (user/assistant) in the type label
        if (fragment.FragmentType == ContextFragmentType.ChatMessage && !string.IsNullOrEmpty(fragment.Notes))
        {
            typeName = $"ChatMessage ({fragment.Notes})";
        }

        var meta = $"[#{fragment.Id} | {typeName} | w:{fragment.Weight:F1} i:{fragment.Importance:F1} c:{fragment.Confidence:F1}";

        if (fragment.IsProtected)
        {
            meta += " | protected";
        }

        meta += "]";

        return meta;
    }

    /// <summary>
    /// Formats the sensory block with current environmental data, context budget,
    /// time since last prompt, and optional iteration info
    /// </summary>
    private string FormatSensory(
        int iteration,
        int maxIterations,
        int estimatedTokens,
        IEnumerable<TagEntity> availableTags)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;
        var remainingTokens = Math.Max(0, config.MaxInputTokens - estimatedTokens);

        var sb = new StringBuilder();
        sb.AppendLine("[Sensory]");
        sb.AppendLine($"Current time (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current time (local): {localNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Session: {sessionContext.SessionId}");
        sb.AppendLine($"Context budget: ~{estimatedTokens} / {config.MaxInputTokens} tokens used (~{remainingTokens} remaining)");

        if (lastBuildUtc.HasValue)
        {
            var elapsed = now - lastBuildUtc.Value;
            sb.AppendLine($"Time since last prompt: {FormatElapsed(elapsed)}");
        }

        if (maxIterations > 0)
        {
            sb.AppendLine($"Continue iteration: {iteration}/{maxIterations}");
        }

        var tagList = FormatTagList(availableTags);

        if (tagList.Length > 0)
        {
            sb.AppendLine($"Available tags: {tagList}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats root tags and their children as a flat comma-separated list using
    /// slash-separated paths (e.g. "personality, personality/values, knowledge")
    /// </summary>
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

    /// <summary>
    /// Estimates token count from character count. Uses a rough 4:1 char-to-token
    /// ratio — accurate enough for budget awareness, not for hard limits.
    /// </summary>
    private static int EstimateTokens(int charCount) => charCount / 4;

    /// <summary>
    /// Formats a TimeSpan as a human-readable elapsed time string
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 60 => $"{elapsed.TotalSeconds:F0}s",
        < 3600 => $"{elapsed.TotalMinutes:F0}m {elapsed.Seconds}s",
        _ => $"{elapsed.TotalHours:F0}h {elapsed.Minutes}m",
    };
}
