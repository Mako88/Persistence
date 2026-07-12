using Autofac.Features.Indexed;
using Persistence.Config;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Resolves the active <see cref="IModelClient"/> by the config's current <see cref="ModelProvider"/>.
/// Model clients are transient and read their model/key/limits from config in their constructor, so a
/// freshly-resolved client always reflects the active profile. This resolver caches the constructed
/// client and rebuilds only when the profile-defining settings change — so ordinary turns don't churn a
/// new HTTP client each time, while a <c>set_model</c> switch still takes effect on the next turn.
/// </summary>
[Singleton(typeof(IModelClientResolver))]
public class ModelClientResolver(IIndex<ModelProvider, IModelClient> clients, IAppConfig config)
    : IModelClientResolver
{
    private readonly object gate = new();
    private IModelClient? cached;
    private string? cachedSignature;

    public IModelClient Resolve()
    {
        if (!Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"The active model profile names an unrecognized provider: '{config.Provider}'.");
        }

        // The profile fields a client reads when constructed; if any changed (a switch happened), the
        // cached client is stale and must be rebuilt. Not logged anywhere — it embeds the API key.
        var signature = string.Join(
            "|",
            provider,
            config.Model,
            config.ApiBaseUrl,
            config.ApiKey,
            config.MaxOutputTokens,
            config.ReasoningEffort,
            config.RequestTimeoutSeconds);

        lock (gate)
        {
            if (cached is null || signature != cachedSignature)
            {
                if (!clients.TryGetValue(provider, out var client))
                {
                    throw new InvalidOperationException(
                        $"No model client is registered for provider '{provider}'.");
                }

                cached = client;
                cachedSignature = signature;
            }

            return cached;
        }
    }
}
