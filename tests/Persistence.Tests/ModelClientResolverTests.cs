using Autofac.Features.Indexed;
using Moq;
using Persistence.Config;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// The resolver picks the model client for the config's currently-active provider and caches it,
/// rebuilding only when the profile-defining settings change — this is what makes a runtime
/// <c>set_model</c> switch take effect on the next turn without churning an HTTP client every turn.
/// </summary>
public class ModelClientResolverTests
{
    private sealed class FakeIndex(Dictionary<ModelProvider, Func<IModelClient>> factories)
        : IIndex<ModelProvider, IModelClient>
    {
        public int Resolutions { get; private set; }

        public bool TryGetValue(ModelProvider key, out IModelClient value)
        {
            if (factories.TryGetValue(key, out var factory))
            {
                Resolutions++;
                value = factory();
                return true;
            }

            value = null!;
            return false;
        }

        public IModelClient this[ModelProvider key] =>
            TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
    }

    private static IModelClient NewClient() => new Mock<IModelClient>().Object;

    [Fact]
    public void ResolvesTheClientForTheActiveProvider()
    {
        var anthropic = NewClient();
        var index = new FakeIndex(new() { [ModelProvider.Anthropic] = () => anthropic });
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-sonnet-4-6" };

        var resolver = new ModelClientResolver(index, config);

        Assert.Same(anthropic, resolver.Resolve());
    }

    [Fact]
    public void CachesAcrossTurnsThenRebuildsWhenTheProviderSwitches()
    {
        var index = new FakeIndex(new()
        {
            [ModelProvider.Anthropic] = NewClient, // a fresh mock per construction, so identity reveals rebuilds
            [ModelProvider.OpenAI] = NewClient,
        });
        var config = new AppConfig { Provider = "Anthropic", Model = "claude" };
        var resolver = new ModelClientResolver(index, config);

        var first = resolver.Resolve();
        var second = resolver.Resolve();
        Assert.Same(first, second);          // reused — no per-turn HTTP client churn
        Assert.Equal(1, index.Resolutions);

        config.Provider = "OpenAI";          // a set_model switch
        var afterSwitch = resolver.Resolve();
        Assert.NotSame(first, afterSwitch);  // rebuilt for the new provider
        Assert.Equal(2, index.Resolutions);
    }

    [Fact]
    public void RebuildsWhenOnlyTheModelChangesWithinTheSameProvider()
    {
        var index = new FakeIndex(new() { [ModelProvider.Anthropic] = NewClient });
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-sonnet-4-6" };
        var resolver = new ModelClientResolver(index, config);

        var sonnet = resolver.Resolve();
        config.Model = "claude-opus-4-8"; // same provider, different model
        var opus = resolver.Resolve();

        Assert.NotSame(sonnet, opus);
        Assert.Equal(2, index.Resolutions);
    }

    [Fact]
    public void ThrowsWhenTheProviderStringIsUnrecognized()
    {
        var resolver = new ModelClientResolver(new FakeIndex(new()), new AppConfig { Provider = "Martian" });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
        Assert.Contains("Martian", ex.Message);
    }

    [Fact]
    public void ThrowsWhenNoClientIsRegisteredForTheProvider()
    {
        // "Anthropic" parses fine, but the index has no client registered for it.
        var resolver = new ModelClientResolver(new FakeIndex(new()), new AppConfig { Provider = "Anthropic" });

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
    }
}
