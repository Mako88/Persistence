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

    /// <summary>Whether the guard is on at all — a negative depth means "no limit".</summary>
    public bool RelayDepthEnforced => MaxRelayDepth >= 0;

    /// <summary>
    /// How the guards read in the sensory block, so the peer can see the limits it's operating under
    /// rather than discovering them by hitting one.
    /// </summary>
    public string Describe()
    {
        var relay = RelayDepthEnforced
            ? $"peer→peer relay stops after {MaxRelayDepth} hop(s) without a human turn"
            : "peer→peer relay depth unlimited";

        var fan = AutoFan
            ? "open room (peers hear each other automatically)"
            : "no auto-fan (a human relays a peer's message onward)";

        return $"Room guards: {relay}; {fan}. These are adjustable — say so if they're in your way.";
    }
}
