namespace Persistence.Services;

using Persistence.Config;

/// <summary>
/// The output-token ceiling to send for a given model when a profile doesn't set one.
///
/// <para><b>Why this exists.</b> Every peer config pinned <c>MaxOutputTokens</c> to 32,000 — a number
/// chosen for a small local model and then copied into every profile, including hosted ones that allow
/// four times that. A ceiling that low on a reasoning model is not merely conservative: the model can
/// spend the entire budget reasoning and never reach an answer, so the turn is billed in full and
/// produces nothing (see <see cref="ChatCompletionsProtocol.ExtractContent(System.Text.Json.JsonElement, out string?)"/>).
/// John's rule, and the right one: the small ceiling belongs to local models, and everything else
/// should use what its model actually allows.</para>
///
/// <para><b>Provenance matters here.</b> The Anthropic figures are from the published model reference.
/// The rest are <em>estimates</em> — deliberately set to the general default rather than to invented
/// per-model precision, because a confidently wrong ceiling is worse than an honestly generic one. A
/// profile that sets <c>MaxOutputTokens</c> explicitly always wins, which is how a local model keeps
/// its small ceiling and how any figure here gets corrected without a code change.</para>
///
/// <para><b>The durable fix</b> is to stop maintaining this table at all: Anthropic's Models API
/// reports <c>max_tokens</c> and <c>max_input_tokens</c> per model, and OpenRouter reports the same on
/// its models endpoint — so these could be discovered at startup instead of hardcoded. That's recorded
/// in TODO.md rather than built here.</para>
/// </summary>
public static class ModelOutputLimits
{
    /// <summary>Where a model is unknown, or a provider publishes no figure we can rely on.</summary>
    public const int GeneralDefault = 64000;

    /// <summary>Small local models need a genuinely small ceiling; they have no server-side cap.</summary>
    public const int LocalDefault = 4096;

    /// <summary>
    /// Published Anthropic output caps. Matched on a prefix so dated snapshots
    /// (<c>claude-opus-4-8-20260115</c>) resolve the same as the bare alias.
    /// </summary>
    private static readonly (string Prefix, int MaxOutput)[] AnthropicLimits =
    [
        ("claude-fable-5", 128_000),
        ("claude-mythos-5", 128_000),
        ("claude-opus-4-8", 128_000),
        ("claude-opus-4-7", 128_000),
        ("claude-opus-4-6", 128_000),
        ("claude-sonnet-5", 128_000),
        ("claude-sonnet-4-6", 128_000),
        ("claude-haiku-4-5", 64_000),
    ];

    /// <summary>
    /// The ceiling to use for <paramref name="profile"/>. An explicit, positive
    /// <see cref="ModelProfile.MaxOutputTokens"/> is returned untouched — the config is the override,
    /// not the fallback.
    /// </summary>
    public static int Resolve(ModelProfile profile)
    {
        if (profile.MaxOutputTokens > 0)
        {
            return profile.MaxOutputTokens;
        }

        // The provider enum can't answer "is this local": OpenAiChat serves both a llama.cpp box and
        // OpenAI's own chat endpoint. What actually distinguishes them is where the request goes.
        return IsLocalEndpoint(profile) ? LocalDefault : ResolveFor(ParseProvider(profile.Provider), profile.Model);
    }

    /// <summary>
    /// Whether this profile talks to a model running on this machine (or the host, seen from inside a
    /// container). Local models have no server-side ceiling, so ours is the only bound that exists.
    /// </summary>
    public static bool IsLocalEndpoint(ModelProfile profile)
    {
        if (ParseProvider(profile.Provider) == ModelProvider.Local)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(profile.ApiBaseUrl)
            || !Uri.TryCreate(profile.ApiBaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host is "localhost" or "127.0.0.1" or "::1" or "host.docker.internal";
    }

    /// <summary>
    /// Config stores the provider as a string; an unrecognised one reads as <c>Local</c>, which is the
    /// safe direction — a small ceiling on a misconfigured profile beats a large one.
    /// </summary>
    private static ModelProvider ParseProvider(string? provider) =>
        Enum.TryParse<ModelProvider>(provider, ignoreCase: true, out var parsed) ? parsed : ModelProvider.Local;

    /// <summary>Resolution by provider + model id, split out so it can be tested directly.</summary>
    public static int ResolveFor(ModelProvider provider, string? model)
    {
        var id = (model ?? string.Empty).Trim().ToLowerInvariant();

        // Anthropic figures are published; LocalClaude proxies a hosted Claude, so it inherits them.
        if (provider is ModelProvider.Anthropic or ModelProvider.LocalClaude)
        {
            return AnthropicLimits
                .Where(l => id.StartsWith(l.Prefix, StringComparison.Ordinal))
                .Select(l => l.MaxOutput)
                .DefaultIfEmpty(GeneralDefault)
                .Max();
        }

        // OpenAI and OpenRouter both enforce their own per-model cap server-side, and both publish it
        // via an API we don't call yet. Until then the general default is the honest answer — high
        // enough not to strangle a reasoning model, bounded enough to cap a runaway.
        return GeneralDefault;
    }
}
