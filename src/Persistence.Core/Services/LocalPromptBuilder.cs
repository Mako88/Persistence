using Persistence.Config;
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
            if (segment.Source == "System")
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
}
