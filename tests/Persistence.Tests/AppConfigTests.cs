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
        Assert.Equal("high", config.ReasoningEffort);
        Assert.Equal("local", config.Provider);
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
