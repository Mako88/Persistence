using Persistence.Config;

namespace Persistence.Tests;

[Collection("EnvironmentVariables")]
public class ModelProfileTests
{
    [Fact]
    public void UnsetDatabasePathDefaultsToDbsFolderNamedAfterTheProfile()
    {
        var profile = new ModelProfile { Name = "gemma4-12b-q4" };

        Assert.Equal(Path.Combine("dbs", "gemma4-12b-q4.db"), profile.ResolveDatabasePath());
    }

    [Fact]
    public void BareDatabaseFilenameIsPlacedUnderTheDbsFolder()
    {
        var profile = new ModelProfile { Name = "local", DatabasePath = "gemma4-12b-q4.db" };

        // The explicit filename wins over the name-derived default, but still lands under dbs/.
        Assert.Equal(Path.Combine("dbs", "gemma4-12b-q4.db"), profile.ResolveDatabasePath());
    }

    [Fact]
    public void DatabasePathWithADirectoryIsUsedAsIs()
    {
        var profile = new ModelProfile { Name = "local", DatabasePath = "data/store.db" };

        Assert.Equal("data/store.db", profile.ResolveDatabasePath());
    }

    [Fact]
    public void ConfiguredBaseDirectoryReplacesTheDefaultDbsFolder()
    {
        var profile = new ModelProfile { Name = "local" };

        Assert.Equal(Path.Combine("stores", "local.db"), profile.ResolveDatabasePath("stores"));
    }

    [Fact]
    public void BareFilenameLandsUnderTheConfiguredBaseDirectory()
    {
        var profile = new ModelProfile { Name = "local", DatabasePath = "gemma4-12b-q4.db" };

        Assert.Equal(Path.Combine("custom", "gemma4-12b-q4.db"), profile.ResolveDatabasePath("custom"));
    }

    [Fact]
    public void DirectoriedPathIgnoresTheConfiguredBaseDirectory()
    {
        var profile = new ModelProfile { Name = "local", DatabasePath = "data/store.db" };

        // A path that already carries a directory must NOT be re-rooted under the base directory.
        Assert.Equal("data/store.db", profile.ResolveDatabasePath("custom"));
    }

    [Fact]
    public void EmptyBaseDirectoryFallsBackToDefaultDbsFolder()
    {
        var profile = new ModelProfile { Name = "local" };

        Assert.Equal(Path.Combine("dbs", "local.db"), profile.ResolveDatabasePath(""));
    }

    [Fact]
    public async Task DatabasePathIsPerModelAndFollowsTheSelectedProfile()
    {
        var json = """
        {
          "SelectedModel": "local",
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "DatabasePath": "gpt-5.4-mini.db" },
            { "Name": "local", "Provider": "OpenAiChat", "DatabasePath": "gemma4-12b-q4.db" }
          ]
        }
        """;

        await using var temp = new TempConfigFile(json);

        // Selecting "local" yields the gemma store...
        var localConfig = await AppConfig.LoadAsync(temp.Path);
        Assert.Equal(Path.Combine("dbs", "gemma4-12b-q4.db"), localConfig.DatabasePath);

        // ...and switching the active profile yields the cloud store — each model keeps its own.
        Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", "cloud");
        try
        {
            var cloudConfig = await AppConfig.LoadAsync(temp.Path);
            Assert.Equal(Path.Combine("dbs", "gpt-5.4-mini.db"), cloudConfig.DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", null);
        }
    }

    [Fact]
    public async Task DatabaseDirectoryFromFileRedirectsResolvedPath()
    {
        var json = """
        {
          "DatabaseDirectory": "stores",
          "SelectedModel": "local",
          "Models": [
            { "Name": "local", "Provider": "OpenAiChat", "DatabasePath": "gemma4-12b-q4.db" }
          ]
        }
        """;

        await using var temp = new TempConfigFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal(Path.Combine("stores", "gemma4-12b-q4.db"), config.DatabasePath);
    }

    [Fact]
    public async Task DatabaseDirectoryEnvOverrideApplies()
    {
        Environment.SetEnvironmentVariable("PERSISTENCE_DATABASEDIRECTORY", "envstore");
        try
        {
            var json = """
            {
              "DatabaseDirectory": "stores",
              "SelectedModel": "local",
              "Models": [ { "Name": "local", "DatabasePath": "gemma4-12b-q4.db" } ]
            }
            """;

            await using var temp = new TempConfigFile(json);
            var config = await AppConfig.LoadAsync(temp.Path);

            // Env beats the file value.
            Assert.Equal(Path.Combine("envstore", "gemma4-12b-q4.db"), config.DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_DATABASEDIRECTORY", null);
        }
    }

    private sealed class TempConfigFile : IAsyncDisposable
    {
        public string Path { get; }

        public TempConfigFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"modelprofile-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
