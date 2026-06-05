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

    private DateTimeOffset? lastFormatUtc;

    public PromptFormatter(ISessionContext sessionContext, IAppConfig config)
    {
        this.sessionContext = sessionContext;
        this.config = config;
    }

    public List<PromptSegment> Format(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0)
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

        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = FormatSensory(iteration, maxIterations, availableTags),
        });

        lastFormatUtc = DateTimeOffset.UtcNow;

        return segments;
    }

    // ── Private ──────────────────────────────────────────────────

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
        return $"{header}\n{fragment.Content}";
    }

    private static string BuildFragmentHeader(WeightedContextFragment fragment)
    {
        var meta = $"[#{fragment.Id} | {fragment.FragmentType} | w:{fragment.Weight:F1} i:{fragment.Importance:F1} c:{fragment.Confidence:F1}";

        if (fragment.IsProtected)
        {
            meta += " | protected";
        }

        meta += "]";

        return meta;
    }

    private string FormatSensory(
        int iteration,
        int maxIterations,
        IEnumerable<TagEntity> availableTags)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;

        var sb = new StringBuilder();
        sb.AppendLine("[Sensory]");
        sb.AppendLine($"Current time (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current time (local): {localNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Session: {sessionContext.SessionId}");

        if (lastFormatUtc.HasValue)
        {
            var elapsed = now - lastFormatUtc.Value;
            sb.AppendLine($"Time since last prompt: {FormatElapsed(elapsed)}");
        }

        if (maxIterations > 0 && iteration > 0)
        {
            sb.AppendLine($"Continue iteration: {iteration}/{maxIterations}");
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
}
