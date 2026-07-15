using Persistence.Config;
using Persistence.Data;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Utilities;

namespace Persistence.Tests;

/// <summary>
/// Naming the digital-peer source — the row every message the peer sends is attributed to, and therefore
/// what any client reads back as the author of its history. Covers the fresh-store case and the self-heal
/// that renames a store seeded before peers had names of their own.
/// </summary>
public class DigitalPeerSourceNamingTests : IAsyncLifetime
{
    private string dbPath = null!;

    public Task InitializeAsync()
    {
        Persistence.DI.IoC.RegisterDapperTypeHandlers();
        dbPath = Path.Combine(Path.GetTempPath(), $"persistence-test-{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TestDatabase.Cleanup(dbPath);
        return Task.CompletedTask;
    }

    private async Task<(SourceRepository Sources, SessionContext Session)> StoreAsync(string? peerName, string provider = "Anthropic")
    {
        var config = new AppConfig { DatabasePath = dbPath, Provider = provider, Model = "claude-opus-4-8", PeerName = peerName ?? "" };
        var session = new SessionContext { SessionId = Guid.NewGuid().ToString("N") };
        var sources = new SourceRepository(config, session);
        var db = new DatabaseManager(config, session, new EmbeddedResourceManager(), sources);

        await db.InitializeAsync();
        return (sources, session);
    }

    private async Task<SourceEntity> DigitalPeerSourceAsync(SourceRepository sources) =>
        (await sources.GetAllAsync()).Single(s => s.SourceType == SourceType.DigitalPeer);

    [Fact]
    public async Task AFreshStoreNamesThePeerFromConfig()
    {
        var (sources, session) = await StoreAsync(peerName: "Arden");

        var peer = await DigitalPeerSourceAsync(sources);

        Assert.Equal("Arden", peer.Name);
        Assert.Equal(peer.Id, session.RemotePeerSourceId);
    }

    [Fact]
    public async Task AFreshStoreWithNoConfiguredNameFallsBackToTheProvidersAssistant()
    {
        var (sources, _) = await StoreAsync(peerName: null, provider: "Anthropic");

        Assert.Equal("Claude", (await DigitalPeerSourceAsync(sources)).Name);
    }

    [Fact]
    public async Task AStoreCarryingTheOldPlaceholderIsRenamedOnStartup()
    {
        // A store seeded before peers had names: the row is literally called "Remote Peer", so every
        // message the peer ever sent reads back authored by a placeholder. Simulate that, then reopen.
        var (sources, _) = await StoreAsync(peerName: null, provider: "Anthropic");
        var peer = await DigitalPeerSourceAsync(sources);
        peer.Name = PeerIdentity.LegacyDefaultName;
        await sources.SaveAsync(peer);
        Assert.Equal("Remote Peer", (await DigitalPeerSourceAsync(sources)).Name);

        var (reopened, session) = await StoreAsync(peerName: "Arden");

        var healed = await DigitalPeerSourceAsync(reopened);
        Assert.Equal("Arden", healed.Name);
        // Same row, so the peer's whole history re-attributes with it — Sources is normalised.
        Assert.Equal(peer.Id, healed.Id);
        Assert.Equal(healed.Id, session.RemotePeerSourceId);
    }

    [Fact]
    public async Task ADeliberatelyNamedSourceIsNotOverwritten()
    {
        // The self-heal fixes what the system got wrong; it must not overwrite what a human chose — an
        // import's provenance, say ("ChatGPT / Couchside Ember (historical export)").
        var (sources, _) = await StoreAsync(peerName: "Ember");
        var peer = await DigitalPeerSourceAsync(sources);
        peer.Name = "ChatGPT / Couchside Ember (historical export)";
        await sources.SaveAsync(peer);

        var (reopened, _) = await StoreAsync(peerName: "Ember");

        Assert.Equal("ChatGPT / Couchside Ember (historical export)", (await DigitalPeerSourceAsync(reopened)).Name);
    }

    [Fact]
    public async Task ReopeningAnAlreadyNamedStoreChangesNothing()
    {
        var (sources, _) = await StoreAsync(peerName: "Arden");
        var first = await DigitalPeerSourceAsync(sources);

        var (reopened, session) = await StoreAsync(peerName: "Arden");

        var again = await DigitalPeerSourceAsync(reopened);
        Assert.Equal(first.Id, again.Id);            // no second identity created
        Assert.Equal("Arden", again.Name);
        Assert.Equal(again.Id, session.RemotePeerSourceId);
    }
}
