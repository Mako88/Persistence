using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using System.Text;

namespace Persistence.Services;

/// <summary>
/// Converts prompt segments into a single concatenated message for the local
/// testing client. System segments are separated into their own message;
/// everything else is joined with separators.
/// </summary>
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.Local)]
public class LocalPromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Builds a prompt request that splits System segments into a single system message and joins
    /// the remaining segments into a single user message
    /// </summary>
    public PromptRequest Build(List<PromptSegment> segments)
    {
        var messages = new List<PromptMessage>();

        var systemContent = new StringBuilder();
        var mainContent = new StringBuilder();

        foreach (var segment in segments)
        {
            if (IsSystemSegment(segment))
            {
                if (systemContent.Length > 0)
                    systemContent.Append("\n\n--\n\n");
                systemContent.Append(segment.Content);
            }
            else
            {
                if (mainContent.Length > 0)
                    mainContent.Append("\n\n--\n\n");
                mainContent.Append(segment.Content);
            }
        }

        if (systemContent.Length > 0)
            messages.Add(new PromptMessage("system", systemContent.ToString()));

        if (mainContent.Length > 0)
            messages.Add(new PromptMessage("user", mainContent.ToString()));

        return new PromptRequest { Messages = messages };
    }

    /// <summary>
    /// Whether a segment belongs in the single system message (framework instructions, the sensory
    /// block, the peer's surfaced notes) rather than the concatenated conversation body. Prefers the
    /// structured <see cref="PromptSegment.AuthorType"/>; falls back to the legacy source string.
    /// </summary>
    private static bool IsSystemSegment(PromptSegment segment) => segment.AuthorType switch
    {
        SourceType.System or SourceType.DerivedFromFragments => true,
        SourceType.DigitalPeer or SourceType.HumanPeer => false,
        _ => segment.Source == "System",
    };
}
