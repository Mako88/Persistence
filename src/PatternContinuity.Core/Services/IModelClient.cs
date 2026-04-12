namespace PatternContinuity.Services;

public record ChatMessage(string Role, string Content);

public interface IModelClient
{
    Task<string> CompleteAsync(List<ChatMessage> messages, CancellationToken ct = default);
}
