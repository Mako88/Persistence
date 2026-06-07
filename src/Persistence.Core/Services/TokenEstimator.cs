namespace Persistence.Services;

/// <summary>
/// Rough, provider-agnostic token estimate for context-budget awareness. Uses the standard
/// ~4-characters-per-token heuristic — deliberately an estimate, not a real tokenizer: it needs
/// no per-provider tokenizer, has no per-turn lag (it sizes the prompt the peer is about to act
/// on, whereas real usage only comes back after a call), and is accurate enough for "how full am
/// I?" Slightly conservative (rounds up) so the peer trims a little early rather than overshooting.
/// </summary>
public static class TokenEstimator
{
    private const double CharsPerToken = 4.0;

    /// <summary>Estimates the token count of a single string.</summary>
    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>Estimates the combined token count of several strings.</summary>
    public static int Estimate(IEnumerable<string> texts) =>
        texts.Sum(Estimate);
}
