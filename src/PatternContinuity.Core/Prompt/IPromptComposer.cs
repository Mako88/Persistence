using Persistence.Services;

namespace Persistence.Prompt
{
    public interface IPromptComposer
    {
        Task<List<ChatMessage>> ComposeAsync(string userMessage, List<ChatMessage> recentConversation, string? activePersonId);
        Task<List<ChatMessage>> ComposeReflectionPromptAsync(string userMessage, string assistantReply, string executionSummary, string? activePersonId);
        Task<List<ChatMessage>> ComposeWakeUpPromptAsync(string reason, List<ChatMessage> recentConversation, string? activePersonId);
    }
}