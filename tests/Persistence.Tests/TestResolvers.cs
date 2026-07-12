using Moq;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Test helpers for the model-client indirection. The turn resolves its client through
/// <see cref="IModelClientResolver"/> (so a runtime set_model switch takes effect next turn); tests that
/// only care about the client itself wrap their mock so the handler always receives it.
/// </summary>
internal static class TestResolvers
{
    public static IModelClientResolver For(IModelClient client)
    {
        var resolver = new Mock<IModelClientResolver>();
        resolver.Setup(r => r.Resolve()).Returns(client);
        return resolver.Object;
    }
}
