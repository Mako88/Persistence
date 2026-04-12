namespace Persistence.Prompt;

public class TokenBudget
{
    private readonly int _maxTokens;
    private int _used;

    public TokenBudget(int maxTokens)
    {
        _maxTokens = maxTokens;
    }

    public int Remaining => _maxTokens - _used;
    public int Used => _used;

    public bool CanFit(int tokens) => _used + tokens <= _maxTokens;

    public bool TryConsume(int tokens)
    {
        if (!CanFit(tokens)) return false;
        _used += tokens;
        return true;
    }

    public void ForceConsume(int tokens) => _used += tokens;

    public static int Estimate(string text) => text.Length / 4;
}
