using Persistence.Config;

namespace Persistence.Tests;

[Collection("EnvironmentVariables")]
public class AppConfigTests
{
    [Fact]
    public async Task ReturnsDefaultsWhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var config = await AppConfig.LoadAsync(path);

        Assert.Equal("Tui", config.UiMode);
        Assert.Equal("off", config.ReasoningEffort); // native reasoning off by default — peer uses <think>
        Assert.Equal("local", config.Provider);
        Assert.Equal("Local Peer", config.SelectedLocalPeer); // back-compat default
    }

    [Fact]
    public async Task ContainerEnvironmentOverridesApplyToTheContainerSettings()
    {
        // The nested Container settings are overridden by a hand-rolled path (PERSISTENCE_CONTAINER_*),
        // separate from the reflection loop for top-level scalars — so it needs its own coverage.
        Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_ENABLED", "true");
        Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_LOCAL", "true");
        Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_TIMEOUTSECONDS", "99");
        Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_ALLOWALLCOMMANDS", "true");
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
            var config = await AppConfig.LoadAsync(missing);

            Assert.True(config.Container.Enabled);
            Assert.True(config.Container.Local); // the peer-in-its-own-container flag (ADR-0007)
            Assert.Equal(99, config.Container.TimeoutSeconds);
            Assert.True(config.Container.AllowAllCommands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_ENABLED", null);
            Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_LOCAL", null);
            Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_TIMEOUTSECONDS", null);
            Environment.SetEnvironmentVariable("PERSISTENCE_CONTAINER_ALLOWALLCOMMANDS", null);
        }
    }

    [Fact]
    public async Task LoadsSelectedLocalPeerAndDescriptions()
    {
        var json = """
        {
          "SelectedLocalPeer": "John",
          "LocalPeers": [ { "Name": "John", "Description": "the steward" } ]
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal("John", config.SelectedLocalPeer);
        Assert.Equal("the steward", Assert.Single(config.LocalPeers).Description);
    }

    [Fact]
    public async Task LoadsValuesFromFile()
    {
        var json = """
        {
          "Provider": "OpenAI",
          "Model": "gpt-5",
          "UiMode": "Tui",
          "ReasoningEffort": "medium",
          "MaxOutputTokens": 4096
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal("OpenAI", config.Provider);
        Assert.Equal("gpt-5", config.Model);
        Assert.Equal("Tui", config.UiMode);
        Assert.Equal("medium", config.ReasoningEffort);
        Assert.Equal(4096, config.MaxOutputTokens);
    }

    [Fact]
    public async Task IsCaseInsensitiveForPropertyNames()
    {
        await using var temp = new TempFile("""{ "uimode": "Tui" }""");

        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal("Tui", config.UiMode);
    }

    [Fact]
    public async Task EnvironmentVariableOverridesFileAndDefaults()
    {
        Environment.SetEnvironmentVariable("PERSISTENCE_PROVIDER", "OpenAI");
        Environment.SetEnvironmentVariable("PERSISTENCE_MAXINPUTTOKENS", "1234");
        try
        {
            await using var temp = new TempFile("""{ "Provider": "local" }""");

            var config = await AppConfig.LoadAsync(temp.Path);

            Assert.Equal("OpenAI", config.Provider);   // env beats the file
            Assert.Equal(1234, config.MaxInputTokens);  // env beats the default, converted to int
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_PROVIDER", null);
            Environment.SetEnvironmentVariable("PERSISTENCE_MAXINPUTTOKENS", null);
        }
    }

    [Fact]
    public async Task SeedsDirectoryLoadsFromFileAndIsOverriddenByEnv()
    {
        await using var temp = new TempFile("""{ "SeedsDirectory": "from-file" }""");

        var fromFile = await AppConfig.LoadAsync(temp.Path);
        Assert.Equal("from-file", fromFile.SeedsDirectory);

        Environment.SetEnvironmentVariable("PERSISTENCE_SEEDSDIRECTORY", "from-env");
        try
        {
            var overridden = await AppConfig.LoadAsync(temp.Path);
            Assert.Equal("from-env", overridden.SeedsDirectory); // env beats the file
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SEEDSDIRECTORY", null);
        }
    }

    [Fact]
    public async Task SelectsTheNamedModelProfileFromTheModelsArray()
    {
        var json = """
        {
          "UiMode": "Tui",
          "SelectedModel": "local",
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5", "MaxInputTokens": 8000 },
            { "Name": "local", "Provider": "OpenAiChat", "Model": "gemma",
              "ApiBaseUrl": "http://127.0.0.1:8080/v1", "MaxInputTokens": 28000, "MaxOutputTokens": 4096 }
          ]
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        // The flat IAppConfig properties read from the selected ("local") profile.
        Assert.Equal("OpenAiChat", config.Provider);
        Assert.Equal("gemma", config.Model);
        Assert.Equal("http://127.0.0.1:8080/v1", config.ApiBaseUrl);
        Assert.Equal(28000, config.MaxInputTokens);
        Assert.Equal(4096, config.MaxOutputTokens);
    }

    [Fact]
    public async Task UppercaseSelectedModelEnvSwitchesTheActiveProfile()
    {
        // Regression: the profile switch is keyed on PERSISTENCE_SelectedModel (PascalCase), but it's
        // documented/set in UPPER (PERSISTENCE_SELECTEDMODEL). Env vars are case-sensitive on Linux, so
        // an uppercase override in a container used to silently no-op — the peer booted on the file's
        // default model (e.g. Ember coming up on Anthropic instead of its chosen substrate).
        var json = """
        {
          "SelectedModel": "local",
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5.4" },
            { "Name": "local", "Provider": "OpenAiChat", "Model": "gemma", "ApiBaseUrl": "http://127.0.0.1:8080/v1" }
          ]
        }
        """;
        await using var temp = new TempFile(json);

        Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", "cloud");
        try
        {
            var config = await AppConfig.LoadAsync(temp.Path);

            Assert.Equal("cloud", config.ActiveModelName);   // switched, not left on the file's "local"
            Assert.Equal("OpenAI", config.Provider);
            Assert.Equal("gpt-5.4", config.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", null);
        }
    }

    [Fact]
    public async Task ActiveProfileContainerNameOverridesTheSharedContainer()
    {
        var json = """
        {
          "SelectedModel": "claude",
          "Container": { "Enabled": true, "Name": "persistence-computer" },
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" },
            { "Name": "claude", "Provider": "LocalClaude", "Model": "claude",
              "ContainerName": "persistence-claude-computer" }
          ]
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        // The active peer gets its own box; the shared Container.Name is overridden while it's active.
        Assert.Equal("persistence-claude-computer", config.Container.Name);
    }

    [Fact]
    public async Task ActiveProfileContainerAllowAllOverridesTheSharedSetting()
    {
        // Shared default is off; the active profile flips it on for itself, while another profile
        // (env-switched to) inherits the shared-off base rather than the previous profile's override.
        var json = """
        {
          "SelectedModel": "claude",
          "Container": { "Enabled": true },
          "Models": [
            { "Name": "claude", "Provider": "LocalClaude", "Model": "claude", "ContainerAllowAll": true },
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" }
          ]
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.True(config.Container.AllowAllCommands);
    }

    [Fact]
    public async Task ProfileWithoutContainerNameFallsBackToTheConfiguredBaseName()
    {
        // File selects "claude" (→ its own box), then an env switch to "cloud" (no override) must
        // resolve back to the configured base — not inherit claude's box across the re-resolve.
        Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", "cloud");
        try
        {
            var json = """
            {
              "SelectedModel": "claude",
              "Container": { "Name": "shared-box" },
              "Models": [
                { "Name": "claude", "Provider": "LocalClaude", "Model": "claude", "ContainerName": "claude-box" },
                { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" }
              ]
            }
            """;

            await using var temp = new TempFile(json);
            var config = await AppConfig.LoadAsync(temp.Path);

            Assert.Equal("shared-box", config.Container.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", null);
        }
    }

    [Fact]
    public async Task FallsBackToFirstProfileWhenSelectedModelIsMissingOrUnmatched()
    {
        var json = """
        {
          "SelectedModel": "does-not-exist",
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" },
            { "Name": "local", "Provider": "OpenAiChat", "Model": "gemma" }
          ]
        }
        """;

        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal("OpenAI", config.Provider); // first profile
        Assert.Equal("gpt-5", config.Model);
    }

    [Fact]
    public async Task SelectedModelEnvVarSwitchesTheActiveProfile()
    {
        Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", "local");
        try
        {
            var json = """
            {
              "SelectedModel": "cloud",
              "Models": [
                { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" },
                { "Name": "local", "Provider": "OpenAiChat", "Model": "gemma", "MaxInputTokens": 28000 }
              ]
            }
            """;

            await using var temp = new TempFile(json);
            var config = await AppConfig.LoadAsync(temp.Path);

            Assert.Equal("OpenAiChat", config.Provider); // env switched to "local"
            Assert.Equal(28000, config.MaxInputTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", null);
        }
    }

    [Fact]
    public async Task EnvVarOverridesApplyToTheActiveProfile()
    {
        Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", "local");
        Environment.SetEnvironmentVariable("PERSISTENCE_MAXINPUTTOKENS", "16000");
        try
        {
            var json = """
            {
              "SelectedModel": "cloud",
              "Models": [
                { "Name": "cloud", "Provider": "OpenAI", "MaxInputTokens": 8000 },
                { "Name": "local", "Provider": "OpenAiChat", "MaxInputTokens": 28000 }
              ]
            }
            """;

            await using var temp = new TempFile(json);
            var config = await AppConfig.LoadAsync(temp.Path);

            // Override lands on the env-selected "local" profile, not "cloud".
            Assert.Equal("OpenAiChat", config.Provider);
            Assert.Equal(16000, config.MaxInputTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSISTENCE_SELECTEDMODEL", null);
            Environment.SetEnvironmentVariable("PERSISTENCE_MAXINPUTTOKENS", null);
        }
    }

    [Fact]
    public async Task TrySwitchModelReResolvesTheActiveProfileByName()
    {
        var json = """
        {
          "SelectedModel": "cloud",
          "Models": [
            { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" },
            { "Name": "local", "Provider": "OpenAiChat", "Model": "gemma", "MaxInputTokens": 28000 }
          ]
        }
        """;
        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);
        Assert.Equal("OpenAI", config.Provider); // starts on "cloud"

        var switched = config.TrySwitchModel("LOCAL"); // case-insensitive

        Assert.True(switched);
        Assert.Equal("local", config.ActiveModelName);
        Assert.Equal("OpenAiChat", config.Provider); // flat props now read from the new profile
        Assert.Equal("gemma", config.Model);
        Assert.Equal(28000, config.MaxInputTokens);
    }

    [Fact]
    public async Task TrySwitchModelLeavesActiveProfileUnchangedWhenNameIsUnknown()
    {
        var json = """
        {
          "SelectedModel": "cloud",
          "Models": [ { "Name": "cloud", "Provider": "OpenAI", "Model": "gpt-5" } ]
        }
        """;
        await using var temp = new TempFile(json);
        var config = await AppConfig.LoadAsync(temp.Path);

        var switched = config.TrySwitchModel("nope");

        Assert.False(switched);
        Assert.Equal("cloud", config.ActiveModelName); // unchanged
        Assert.Equal("gpt-5", config.Model);
    }

    [Fact]
    public async Task FallsBackToDefaultsOnMalformedJson()
    {
        await using var temp = new TempFile("{ this is not valid json");

        var config = await AppConfig.LoadAsync(temp.Path);

        Assert.Equal("Tui", config.UiMode);
    }

    private sealed class TempFile : IAsyncDisposable
    {
        public string Path { get; }

        public TempFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"appconfig-{Guid.NewGuid():N}.json");
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
