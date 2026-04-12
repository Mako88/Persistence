namespace PatternContinuity.Prompt;

public class PromptBlock
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public int EstimatedTokens => Content.Length / 4; // rough approximation
}
