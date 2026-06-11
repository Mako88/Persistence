namespace Persistence.Data;

/// <summary>
/// One identity seed for a peer's brand-new store, loaded from a per-database seed file
/// (<c>{SeedsDirectory}/{dbName}.json</c>, an array of these). Unlike the embedded onboarding text
/// (which becomes protected System fragments), these become <em>authored</em> fragments the peer owns
/// and can freely curate — they're sourced to the remote peer, exactly as if it had written them.
/// </summary>
public record PeerSeed
{
    /// <summary>Authorable fragment type (Identity, Relational, Personal, Summary). Unknown → Personal.</summary>
    public string? Type { get; init; }

    /// <summary>The fragment's content. A seed with blank content is skipped.</summary>
    public string Content { get; init; } = "";

    /// <summary>Optional tag path(s) — a single <c>a/b</c> path, or several comma-separated.</summary>
    public string? Tags { get; init; }

    /// <summary>Importance (0–1). Defaults mid.</summary>
    public float Importance { get; init; } = 0.5f;

    /// <summary>Confidence (0–1). Defaults mid.</summary>
    public float Confidence { get; init; } = 0.5f;

    /// <summary>Relevance to the current prompt (0–1). Defaults high so a fresh identity surfaces.</summary>
    public float Relevance { get; init; } = 1.0f;
}
