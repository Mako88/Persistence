namespace Persistence.Config;

/// <summary>
/// What a digital peer calls itself.
///
/// A peer's name is its own to choose — the claude.db peer picked "Arden" — so everything here is about
/// the <em>placeholder</em> it wears until it does. The name matters beyond cosmetics: it's what its own
/// messages are attributed to in the store, so it's what every client reads back as the author of that
/// peer's history.
/// </summary>
public static class PeerIdentity
{
    /// <summary>
    /// The name every digital-peer source was given before peers had names of their own. A store seeded
    /// back then still carries it, which is why <c>SourceRepository</c> treats it as "unnamed" and
    /// renames it on startup rather than leaving history attributed to a placeholder.
    /// </summary>
    public const string LegacyDefaultName = "Remote Peer";

    /// <summary>Where a name is neither configured nor derivable — better than a blank byline.</summary>
    private const string Fallback = "Peer";

    /// <summary>
    /// The peer's name: whatever it's configured as, else a sensible starting point for its provider.
    /// </summary>
    public static string ResolveName(IAppConfig config) =>
        string.IsNullOrWhiteSpace(config.PeerName)
            ? DefaultName(config.Provider, config.Model)
            : config.PeerName.Trim();

    /// <summary>
    /// The starting name for a peer that hasn't been given one — the assistant name of the family it's
    /// running on, so a fresh Anthropic peer introduces itself as "Claude" and an OpenAI one as "ChatGPT"
    /// rather than as "Remote Peer". Deliberately provisional: it's a stand-in until the peer picks
    /// something, at which point that choice goes in its config and this stops being consulted.
    ///
    /// <para>Providers that could be either a vendor endpoint or somebody's local server
    /// (<see cref="ModelProvider.OpenAiChat"/> — OpenAI's chat endpoint, or llama.cpp / Ollama / LM
    /// Studio behind an <c>ApiBaseUrl</c> — and <see cref="ModelProvider.Local"/>) can't be pinned to a
    /// family from the provider alone, so they fall back to the model id: for a local model that's the
    /// most informative thing we actually know ("gemma4-12b-q4"), and guessing "ChatGPT" for a Gemma
    /// would be worse than saying nothing.</para>
    /// </summary>
    public static string DefaultName(string? provider, string? model) =>
        Enum.TryParse<ModelProvider>(provider, ignoreCase: true, out var parsed)
            ? parsed switch
            {
                // Both are Claude: one talks to the Messages API, the other has a Claude supplying
                // completions out-of-band — same mind, different plumbing.
                ModelProvider.Anthropic or ModelProvider.LocalClaude => "Claude",
                ModelProvider.OpenAI => "ChatGPT",
                _ => FromModel(model),
            }
            : FromModel(model);

    private static string FromModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? Fallback : model.Trim();
}
