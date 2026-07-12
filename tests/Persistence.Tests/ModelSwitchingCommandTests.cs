using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using Persistence.Services;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// The peer-facing <c>list_models</c> / <c>set_model</c> commands: list the configured profiles and
/// switch which one answers going forward. The switch mutates the shared config in place; the turn's
/// <see cref="IModelClientResolver"/> then picks up the change on the next turn (covered separately).
/// </summary>
public class ModelSwitchingCommandTests
{
    private readonly List<ToolInvoked> published = [];
    private readonly AppConfig config;
    private readonly ManageContextHandler handler;

    public ModelSwitchingCommandTests()
    {
        config = new AppConfig
        {
            SelectedModel = "cloud",
            Models =
            [
                new ModelProfile { Name = "cloud", Provider = "OpenAI", Model = "gpt-5" },
                new ModelProfile { Name = "claude", Provider = "Anthropic", Model = "claude-sonnet-4-6" },
            ],
        };
        config.ResolveActiveModel();

        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });

        handler = new ManageContextHandler(
            new Mock<IWorkingContextRepository>().Object,
            new Mock<IContextFragmentRepository>().Object,
            new Mock<ITagRepository>().Object,
            new Mock<IEntityTagRepository>().Object,
            new Mock<IScheduledEventRepository>().Object,
            new Mock<ISourceRepository>().Object,
            new SessionContext { RemotePeerSourceId = 3 },
            new Mock<IProposalService>().Object,
            new Mock<IProposalRepository>().Object,
            config,
            bus);
    }

    private static WorkingContextEntity Context() =>
        new() { Id = 1, Name = "c", Summary = "s", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };

    private async Task<string> RunAsync(string json)
    {
        published.Clear();
        await handler.HandleAsync(Context(), JsonNode.Parse(json));
        return published.Single().Result;
    }

    [Fact]
    public async Task ListModelsShowsEveryProfileAndMarksTheActiveOne()
    {
        var result = await RunAsync("""{ "list_models": {} }""");

        Assert.Contains("* cloud — OpenAI / gpt-5", result);          // active is starred
        Assert.Contains("claude — Anthropic / claude-sonnet-4-6", result);
        Assert.DoesNotContain("* claude", result);                    // only the active profile is starred
    }

    [Fact]
    public async Task SetModelSwitchesTheActiveProfileAndReportsIt()
    {
        var result = await RunAsync("""{ "set_model": { "name": "claude" } }""");

        Assert.Equal("claude", config.ActiveModelName); // config actually switched
        Assert.Equal("Anthropic", config.Provider);
        Assert.Equal("claude-sonnet-4-6", config.Model);
        Assert.Contains("Switched from 'cloud' to 'claude'", result);
        Assert.Contains("next turn", result);
    }

    [Fact]
    public async Task SetModelIsCaseInsensitiveOnTheProfileName()
    {
        var result = await RunAsync("""{ "set_model": { "name": "CLAUDE" } }""");

        Assert.Equal("claude", config.ActiveModelName);
        Assert.Contains("to 'claude'", result);
    }

    [Fact]
    public async Task SetModelRejectsAnUnknownProfileAndListsTheAvailableOnes()
    {
        var result = await RunAsync("""{ "set_model": { "name": "gpt-9" } }""");

        Assert.Equal("cloud", config.ActiveModelName); // unchanged
        Assert.Contains("no profile named 'gpt-9'", result);
        Assert.Contains("cloud", result);
        Assert.Contains("claude", result); // available options listed
    }

    [Fact]
    public async Task SetModelRequiresAName()
    {
        var result = await RunAsync("""{ "set_model": {} }""");

        Assert.Contains("'name' is required", result);
        Assert.Equal("cloud", config.ActiveModelName);
    }
}
