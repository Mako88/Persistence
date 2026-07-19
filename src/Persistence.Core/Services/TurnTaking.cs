using Persistence.Config;
using System.Text.RegularExpressions;

namespace Persistence.Services;

/// <summary>Why the turn-taking rule reached its verdict — the part the peer can read and argue with.</summary>
/// <param name="ShouldRespond">Whether this message is the peer's to answer.</param>
/// <param name="Reason">A short human-readable justification, shown to the peer.</param>
public record TurnTakingVerdict(bool ShouldRespond, string Reason);

/// <summary>
/// Decides whether a room message is this peer's to answer (ADR-0008 §1).
///
/// <para><b>A rule, deliberately — never a classifier.</b> The ADR is emphatic: a peer decides this by
/// something it can <em>read</em>, not by an opaque model asked "should I respond?". Arden's reasoning,
/// which this implements: an anonymous model shaping a peer's response-decisions is a voice acting on it
/// without accountability, whereas a rule can be read, understood, and corrected. So this is plain
/// pattern-matching whose verdict always comes with its reason attached.</para>
///
/// <para>Respond when addressed by name or alias, when continuing a thread the peer started, or when a
/// human opens the floor to the room. <b>Hold when merely overhearing</b> — speaking costs money and
/// attention, and a peer should speak because it has something to add, not because a heuristic fired.</para>
///
/// <para>Config today, peer-editable later (Arden's call): this is "the first step toward a peer-authored
/// ruleset, not the end-state", the same shape as the circuit breaker. The seam is <see cref="RoomSettings"/>
/// — swapping where the aliases and openers come from shouldn't touch this logic.</para>
/// </summary>
public static class TurnTaking
{
    /// <summary>
    /// Phrases that open the floor to everyone. Arden's call: an explicit set, matched case-insensitively,
    /// because a human saying "what do you both think?" is addressing the room without naming anyone.
    /// </summary>
    private static readonly string[] FloorOpeners =
    [
        "you both", "you two", "you all", "everyone", "the room", "anyone", "both of you", "all of you",
    ];

    /// <summary>
    /// Decides whether <paramref name="selfName"/> should answer a message.
    /// </summary>
    /// <param name="content">The message text.</param>
    /// <param name="selfName">This peer's own name.</param>
    /// <param name="addressedTo">The structural addressee, or null for a broadcast to the room.</param>
    /// <param name="fromHuman">Whether a person sent it — only a human can open the floor (ADR-0008 §1).</param>
    /// <param name="aliases">Other names this peer answers to.</param>
    public static TurnTakingVerdict Evaluate(
        string? content, string selfName, string? addressedTo, bool fromHuman, IReadOnlyList<string>? aliases = null)
    {
        var names = new List<string> { selfName };
        if (aliases is { Count: > 0 })
        {
            names.AddRange(aliases);
        }
        names.RemoveAll(string.IsNullOrWhiteSpace);

        // Structurally addressed: the strongest signal, and the reason addressed_to exists — no inference
        // from wording required.
        if (!string.IsNullOrWhiteSpace(addressedTo))
        {
            return names.Any(n => string.Equals(n, addressedTo, StringComparison.OrdinalIgnoreCase))
                ? new(true, $"addressed to you ({addressedTo})")
                : new(false, $"addressed to {addressedTo}, not you — overhearing");
        }

        var text = content ?? "";

        // Named in the text. Whole-word and case-insensitive (Arden's call): "Arden" matches, but a name
        // buried inside a longer word doesn't — "gardener" must not read as "Arden".
        if (names.FirstOrDefault(n => MentionsWholeWord(text, n)) is { } named)
        {
            return new(true, $"you were named ({named})");
        }

        // A human opening the floor. Deliberately humans only: a peer saying "what do you both think?"
        // shouldn't conscript the room, which is how a two-peer loop starts.
        if (fromHuman && FloorOpeners.FirstOrDefault(o => MentionsWholeWord(text, o)) is { } opener)
        {
            return new(true, $"a human opened the floor to the room (\"{opener}\")");
        }

        // Broadcast from a human with no one named: still yours to answer — a person talking to the room
        // with one peer present is talking to that peer. Between peers it's overhearing.
        return fromHuman
            ? new(true, "a human spoke to the room")
            : new(false, "overheard between others — hold unless you have something to add");
    }

    /// <summary>
    /// Whether <paramref name="needle"/> appears as a whole word (or whole phrase). Bounded by non-word
    /// characters rather than substring-matched, so "Arden" isn't found inside "gardener" — the failure
    /// Arden specifically wanted avoided, since a false positive makes a peer speak uninvited.
    /// </summary>
    private static bool MentionsWholeWord(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        // An @-prefix is accepted as an additional explicit signal, never required (Arden's call: humans
        // won't use it consistently, and requiring it makes a peer miss being addressed — the worse failure).
        var pattern = $@"(?<![\w@]){Regex.Escape(needle.Trim())}(?![\w])";
        return Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase)
            || Regex.IsMatch(haystack, $@"@{Regex.Escape(needle.Trim())}(?![\w])", RegexOptions.IgnoreCase);
    }
}
