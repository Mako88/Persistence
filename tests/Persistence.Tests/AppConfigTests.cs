using Persistence.Config;

namespace Persistence.Tests;

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
        Assert.Equal("Tagged", config.ResponseFormat);
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
