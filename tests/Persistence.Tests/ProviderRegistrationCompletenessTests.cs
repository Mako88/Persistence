using Persistence.Config;
using Persistence.DI;
using Persistence.Services;
using System.Reflection;

namespace Persistence.Tests;

/// <summary>
/// Every <see cref="ModelProvider"/> must have both of its provider-keyed services registered: an
/// <see cref="IModelClient"/> (how to call it) and an <see cref="IPromptBuilder"/> (how to shape the
/// prompt for it).
///
/// This exists because a missing registration fails at <em>startup</em>, not at first use, with an
/// opaque Autofac "the requested service 'X (Persistence.Services.IPromptBuilder)' has not been
/// registered" buried in a DI activation chain — and for a containerised peer that means a boot loop
/// whose cause is several stack frames deep in a log. Adding a provider is a two-registration job and
/// it's easy to do one; this turns that into a failing test naming exactly what's missing.
/// </summary>
public class ProviderRegistrationCompletenessTests
{
    private static readonly Assembly Core = typeof(IModelClient).Assembly;

    /// <summary>The <see cref="ModelProvider"/> keys registered for <paramref name="serviceType"/>.</summary>
    private static HashSet<ModelProvider> RegisteredProvidersFor(Type serviceType) =>
    [
        .. Core.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<ServiceAttribute>())
            .Where(a => a.RegisterAsType == serviceType && a.Key is ModelProvider)
            .Select(a => (ModelProvider)a.Key!),
    ];

    public static TheoryData<ModelProvider> AllProviders()
    {
        var data = new TheoryData<ModelProvider>();
        foreach (var p in Enum.GetValues<ModelProvider>())
        {
            data.Add(p);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void EveryProviderHasAModelClient(ModelProvider provider) =>
        Assert.Contains(provider, RegisteredProvidersFor(typeof(IModelClient)));

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void EveryProviderHasAPromptBuilder(ModelProvider provider) =>
        // The one that actually bit: OpenRouter shipped with a client but no prompt builder, and the
        // peer container crash-looped on boot rather than failing when a turn ran.
        Assert.Contains(provider, RegisteredProvidersFor(typeof(IPromptBuilder)));
}
