using Persistence.Config;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Converts prompt segments into role-mapped messages (OpenAI Responses API shape).
/// System segments become developer messages (the reasoning-model convention).
/// Chat segments map source names to user/assistant roles. All other segments
/// become user messages. Adjacent messages with the same role are collapsed
/// into a single message.
///
/// Also serves the <see cref="ModelProvider.LocalClaude"/> peer, which consumes the same
/// role-labelled message structure.
/// </summary>
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.OpenAI)]
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.LocalClaude)]
public class OpenAiPromptBuilder : IPromptBuilder
{
    public PromptRequest Build(List<PromptSegment> segments)
    {
        var messages = new List<PromptMessage>();

        foreach (var segment in segments)
        {
            var role = MapRole(segment);

            if (messages.Count > 0 && messages[^1].Role == role)
            {
                var last = messages[^1];
                messages[^1] = last with { Content = last.Content + "\n\n--\n\n" + segment.Content };
            }
            else
            {
                messages.Add(new PromptMessage(role, segment.Content));
            }
        }

        return new PromptRequest { Messages = messages };
    }

    private static string MapRole(PromptSegment segment) => segment.Source switch
    {
        "System" => "developer",
        "Remote Peer" => "assistant",
        _ => "user",
    };
}
