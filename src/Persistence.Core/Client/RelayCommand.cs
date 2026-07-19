using Persistence.Contracts;

namespace Persistence.Client;

/// <summary>
/// The lean relay affordance (ADR-0008 §4): <c>/relay &lt;peer&gt;</c> carries the most recent thing the
/// peer you're watching said onward to another peer.
///
/// <para>This is a <b>resolver</b>, not a second implementation of the relay. It answers only "which
/// stored message did the human mean?" and hands that to <see cref="RelayComposer"/>, where the
/// provenance guardrails live. A richer affordance — picking any message out of the conversation rather
/// than only the last — is the intended end-state, and it slots in as a different resolver in front of
/// the same composer rather than a rewrite. That's what makes shipping this one first safe.</para>
/// </summary>
public static class RelayCommand
{
    /// <summary>The verb, matched case-insensitively as the first whitespace-delimited token.</summary>
    public const string Verb = "/relay";

    /// <summary>
    /// Whether this input is a relay command. Checked on the <em>client</em>, unlike the server-side
    /// slash commands: a relay needs the several peer connections and the current selection, which only
    /// the hub has. The server never sees it.
    /// </summary>
    public static bool IsRelay(string? input) =>
        Split(input).Verb.Equals(Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The peer named as the destination, or <see langword="null"/> if none was given (<c>/relay</c> on
    /// its own) — which the caller should answer with usage rather than a guess about who was meant.
    /// </summary>
    public static string? ParseTarget(string? input)
    {
        var rest = Split(input).Remainder;
        return string.IsNullOrWhiteSpace(rest) ? null : rest.Trim();
    }

    /// <summary>
    /// The message a bare <c>/relay</c> acts on: the most recent thing <em>a peer</em> said in this
    /// conversation.
    ///
    /// <para>Skips the human's own messages deliberately. Relaying your own words to another peer isn't a
    /// relay — it's just talking to them, which the ordinary input path already does. So the useful
    /// referent is the last peer utterance, which is nearly always the thing the human just read and
    /// wants to carry onward.</para>
    /// </summary>
    /// <returns>The message, or <see langword="null"/> if the peer hasn't said anything yet.</returns>
    public static ChatHistoryItem? ResolveLastRelayable(IReadOnlyList<ChatHistoryItem>? history)
    {
        if (history is null)
        {
            return null;
        }

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(history[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return history[i];
            }
        }

        return null;
    }

    /// <summary>
    /// A one-line preview of what is about to be carried where, so the human isn't relaying blind — the
    /// thing the lean shape most obviously gives up, recovered without a selection model.
    /// </summary>
    public static string Describe(ChatHistoryItem message, string targetPeer, int newDepth)
    {
        var preview = message.Content.ReplaceLineEndings(" ").Trim();

        if (preview.Length > 160)
        {
            preview = preview[..160] + "…";
        }

        return $"→ Carried {message.Author}'s message to {targetPeer} (hop {newDepth}): \"{preview}\"";
    }

    private static (string Verb, string? Remainder) Split(string? input)
    {
        var parts = (input ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? ("", null) : (parts[0], parts.Length > 1 ? parts[1] : null);
    }
}
