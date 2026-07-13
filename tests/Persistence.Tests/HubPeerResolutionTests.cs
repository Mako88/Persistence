using Persistence.Config;
using Persistence.Console;

namespace Persistence.Tests;

/// <summary>
/// The hub's peer-list resolution (ADR-0007 Phase 2b): CLI <c>--peer</c> endpoints win, else the config's
/// <see cref="AppConfig.HubPeers"/>, else the default single local server.
/// </summary>
public class HubPeerResolutionTests
{
    [Fact]
    public void CliPeersWinOverConfig()
    {
        var config = new AppConfig { HubPeers = [new HubPeerProfile { Name = "Ember", BaseUrl = "http://e" }] };
        var cli = new[] { new PeerEndpoint("Arden", "http://a", "John") };

        var resolved = ClientConsoleHost.ResolvePeers(cli, "John", config);

        Assert.Same(cli, resolved);
    }

    [Fact]
    public void ConfigHubPeersUsedWhenNoCliPeers()
    {
        var config = new AppConfig
        {
            HubPeers =
            [
                new HubPeerProfile { Name = "Arden", BaseUrl = "http://a", LocalPeer = "John" },
                new HubPeerProfile { Name = "Ember", BaseUrl = "http://e" },   // no LocalPeer → inherits --as
            ],
        };

        var resolved = ClientConsoleHost.ResolvePeers([], cliLocalPeer: "John", config);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("Arden", resolved[0].Name);
        Assert.Equal("John", resolved[1].LocalPeer);   // inherited the CLI --as
    }

    [Fact]
    public void ConfigPeersWithNoUrlAreSkipped()
    {
        var config = new AppConfig
        {
            HubPeers =
            [
                new HubPeerProfile { Name = "Arden", BaseUrl = "http://a" },
                new HubPeerProfile { Name = "Broken", BaseUrl = "" },   // dropped
            ],
        };

        var resolved = ClientConsoleHost.ResolvePeers([], null, config);

        Assert.Single(resolved);
        Assert.Equal("Arden", resolved[0].Name);
    }

    [Fact]
    public void FallsBackToDefaultLocalServerWhenNothingConfigured()
    {
        var resolved = ClientConsoleHost.ResolvePeers([], null, new AppConfig());

        Assert.Single(resolved);
        Assert.Null(resolved[0].Name);   // unnamed → the 1:1 single-peer path
        Assert.Equal("http://localhost:5000", resolved[0].BaseUrl);
    }
}
