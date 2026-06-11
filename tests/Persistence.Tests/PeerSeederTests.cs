using Moq;
using Persistence.Config;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Pure (no-DB) tests for <see cref="PeerSeeder"/>: lenient JSON parsing of a seed array, and how the
/// per-database seed file path is resolved from config (explicit directory vs. the default sibling of
/// the database folder).
/// </summary>
public class PeerSeederTests
{
    private static PeerSeeder SeederFor(AppConfig config) =>
        new(config, new Mock<ITagRepository>().Object, new SessionContext());

    [Fact]
    public void ParsesAnArrayOfSeedsWithAllFields()
    {
        var seeds = PeerSeeder.Parse("""
            [
              { "Type": "Identity", "Content": "I am Test.", "Tags": "identity/core",
                "Importance": 0.9, "Confidence": 0.8, "Relevance": 0.7 }
            ]
            """);

        var seed = Assert.Single(seeds);
        Assert.Equal("Identity", seed.Type);
        Assert.Equal("I am Test.", seed.Content);
        Assert.Equal("identity/core", seed.Tags);
        Assert.Equal(0.9f, seed.Importance);
        Assert.Equal(0.8f, seed.Confidence);
        Assert.Equal(0.7f, seed.Relevance);
    }

    [Fact]
    public void ParseIsCaseInsensitiveAndFillsDefaultsForMissingFields()
    {
        // lower-cased keys, and only content provided — weights fall back to the record defaults.
        var seeds = PeerSeeder.Parse("""[ { "content": "just content" } ]""");

        var seed = Assert.Single(seeds);
        Assert.Equal("just content", seed.Content);
        Assert.Null(seed.Type);
        Assert.Equal(0.5f, seed.Importance);
        Assert.Equal(0.5f, seed.Confidence);
        Assert.Equal(1.0f, seed.Relevance); // default high so a fresh identity surfaces
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{ not an array }")]
    [InlineData("[ {\"Content\": \"unterminated\" ")]
    public void ParseDegradesToEmptyOnBlankOrMalformedInput(string? json)
    {
        // A bad seed file must never crash a boot — it degrades to "no seeds".
        Assert.Empty(PeerSeeder.Parse(json));
    }

    [Fact]
    public void SeedFilePathDefaultsToASeedsFolderBesideTheDatabaseDirectory()
    {
        var dbDir = Path.Combine("root", "dbs");
        var config = new AppConfig { DatabaseDirectory = dbDir, DatabasePath = "claude.db" };

        var expected = Path.Combine("root", "seeds", "claude.json");
        Assert.Equal(expected, SeederFor(config).ResolveSeedFilePath());
    }

    [Fact]
    public void SeedFilePathHonoursAnExplicitSeedsDirectoryAndTheDbName()
    {
        var config = new AppConfig
        {
            DatabaseDirectory = Path.Combine("root", "dbs"),
            DatabasePath = "synth.db",
            SeedsDirectory = Path.Combine("custom", "place"),
        };

        var expected = Path.Combine("custom", "place", "synth.json");
        Assert.Equal(expected, SeederFor(config).ResolveSeedFilePath());
    }
}
