using System.Text.RegularExpressions;

namespace Persistence.Store;

/// <summary>
/// How much a thing said matters, felt at the moment it is stored rather than set by hand afterwards
/// (John, 2026-11-08: what happens to a person comes with an importance of its own, and anything that
/// has to be kept up by hand goes stale). Every utterance used to be stored at 0.3, so recall could not
/// tell "Sounds good!" from a decision. This is the first, rule-based form, with no model involved:
/// acknowledgements near zero; a decision, a correction, a request or a question from the human well
/// above. Use (what recall brings back and the reply then draws on) is the signal that refines it later.
/// </summary>
internal static partial class Salience
{
    public const float Default = 0.3f;

    [GeneratedRegex(@"^\W*(ok(ay)?|yes|yep|yeah|sure|cool|great|nice|perfect|awesome|sweet|thanks?( you)?( so much)?|sounds (good|great|perfect)|go (for it|ahead)|got it|lol|haha)\b[\s\W\w]{0,40}$", RegexOptions.IgnoreCase)]
    private static partial Regex Acknowledgement();

    [GeneratedRegex(@"\b(actually|i want|i'd like|i would like|let's|lets|don't|do not|never|always|instead|decid\w*|make sure|remember|important|prefer|rather|wrong|mistake|correct\w*|stop)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Steering();

    public static float Score(string text, bool byHuman)
    {
        var said = text.Trim();
        if (said.Length < 80 && Acknowledgement().IsMatch(said) && !Steering().IsMatch(said))
        {
            return 0.05f;
        }

        var score = said.Length < 80 ? 0.2f : Default;
        if (byHuman)
        {
            score += Math.Min(3, Steering().Matches(said).Count) * 0.1f;
            score += said.Contains('?') ? 0.1f : 0f;
            score += said.Length > 600 ? 0.1f : 0f;
        }

        return Math.Clamp(score, 0.05f, 0.8f);
    }
}
