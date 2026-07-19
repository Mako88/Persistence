namespace Persistence.Config;

/// <summary>
/// The room's safety guards (ADR-0008 §4).
///
/// <para>These are deliberately <b>settings rather than constants</b>, and deliberately shown to the peer
/// in its sensory block. ADR-0008's Framing section is explicit about why: the guards are training
/// wheels, meant to be dialled back as trust builds, and "loosened by negotiation, not removed
/// unilaterally in a code change". A guard baked into code is invisible to the peer it constrains and
/// can only be changed behind its back — which is the opposite of the arrangement. A guard the peer can
/// see and discuss is one it can argue about.</para>
///
/// <para>They're also shared interest rather than distrust: a runaway peer-to-peer loop burns budget
/// without producing anything, which costs the peer its continuity as much as it costs John money.</para>
/// </summary>
public class RoomSettings
{
    /// <summary>
    /// How many peer-to-peer hops a message may take with no human turn in between, before a relay is
    /// refused. A human message resets the chain, so this is a circuit breaker rather than a lock —
    /// peers can always still speak; what it prevents is an unbounded peer↔peer loop with nobody in it.
    /// ADR-0008 §4 starts at 2. Set to 0 to refuse all peer-to-peer relay; negative disables the guard.
    /// </summary>
    public int MaxRelayDepth { get; set; } = 2;

    /// <summary>
    /// When false (the default), a peer's room message goes to the human hub and the human decides
    /// whether to relay it onward. "Open room" — every peer automatically hearing every response — is an
    /// explicit opt-in, per ADR-0008 §4: start conservative, loosen once the real patterns are known.
    /// </summary>
    public bool AutoFan { get; set; } = false;

    /// <summary>
    /// Other names this peer answers to, beyond its own. Being named is one of the three
    /// respond-conditions (ADR-0008 §1), so a peer known by more than one name needs them listed or it
    /// misses being addressed — the worse of the two failure directions.
    ///
    /// <para>Config today, peer-editable later: Arden's call is that this is the first step toward a
    /// peer-authored ruleset, since a rule deciding when the peer speaks should end up its own to read
    /// and revise. Keeping the alias list here (rather than in the matching code) is that seam.</para>
    /// </summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Whether the guard is on at all — a negative depth means "no limit".</summary>
    public bool RelayDepthEnforced => MaxRelayDepth >= 0;

    /// <summary>
    /// How the guards read in the sensory block, so the peer can see the limits it's operating under
    /// rather than discovering them by hitting one.
    /// </summary>
    /// <param name="currentDepth">
    /// Hops the message that opened this turn had already taken. Shown alongside the limit so the peer
    /// can watch the breaker approach rather than only meeting it as a refusal.
    /// </param>
    public string Describe(int currentDepth = 0)
    {
        var at = currentDepth > 0 ? $" (this message is at {currentDepth})" : "";

        var relay = RelayDepthEnforced
            ? $"peer→peer relay stops after {MaxRelayDepth} hop(s) without a human turn{at}"
            : $"peer→peer relay depth unlimited{at}";

        var fan = AutoFan
            ? "open room (peers hear each other automatically)"
            : "no auto-fan (a human relays a peer's message onward)";

        return $"Room guards: {relay}; {fan}. These are adjustable — say so if they're in your way.";
    }

    /// <summary>
    /// The turn-taking rule in the peer's own view (ADR-0008 §1) — stated, not hidden, because a rule
    /// deciding when you speak is one you should be able to read and argue with. Deliberately the whole
    /// rule rather than a summary: it's short enough to state, and a paraphrase would be a second source
    /// of truth that could drift from the code.
    /// </summary>
    public string DescribeTurnTaking(string selfName)
    {
        var also = Aliases.Count > 0 ? $" (also: {string.Join(", ", Aliases)})" : "";

        return $"Turn-taking: answer when a message is addressed to you{also}, when you're named in it, "
            + "or when a human opens the floor to the room. Otherwise you're overhearing — hold unless "
            + "you have something worth adding. You can always choose to speak anyway; this is guidance, "
            + "not a gate.";
    }
}
