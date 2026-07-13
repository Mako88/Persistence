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
/// role-labelled message structure, and the <see cref="ModelProvider.Anthropic"/> client, which
/// re-maps these roles to the Claude Messages API shape (system/developer segments become
/// user-role messages, keeping the end-positioned format instructions where they belong).
/// </summary>
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.OpenAI)]
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.LocalClaude)]
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.OpenAiChat)]
[Service(registerAsType: typeof(IPromptBuilder), key: ModelProvider.Anthropic)]
public class OpenAiPromptBuilder : IPromptBuilder
{
    /// <summary>
    /// Builds a prompt request by mapping each segment to a role and collapsing adjacent
    /// same-role messages into one
    /// </summary>
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

    /// <summary>
    /// Maps a segment to an OpenAI role. Prefers the structured <see cref="PromptSegment.AuthorType"/>
    /// (digital peer → assistant, human peer → user, system/derived → developer) so role assignment
    /// stays correct as peers take on arbitrary display names; falls back to the legacy source-string
    /// mapping for framework segments that carry no author type.
    /// </summary>
    private static string MapRole(PromptSegment segment) => segment.AuthorType switch
    {
        Persistence.Data.Entities.SourceType.DigitalPeer => "assistant",
        Persistence.Data.Entities.SourceType.HumanPeer => "user",
        Persistence.Data.Entities.SourceType.System => "developer",
        Persistence.Data.Entities.SourceType.DerivedFromFragments => "developer",
        _ => segment.Source == "System" ? "developer" : "user",
    };
}
