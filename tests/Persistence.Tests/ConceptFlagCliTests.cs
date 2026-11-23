using System.Diagnostics;

namespace Persistence.Tests;

/// <summary>
/// `concept add --flag` reaches the store through the real CLI (Arden's blocker on #71, review 5918124562): with
/// Program.cs passing null for the flag, every in-process test stayed green and every unsure fact would have been
/// written unflagged. This runs persistence-store.dll as the extractor does, so the flag must cross Program.cs.
/// </summary>
public sealed class ConceptFlagCliTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("concept-flag-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public async Task Concept_add_with_flag_writes_the_fact_flagged()
    {
        const string reason = "referent unsure: the fragment's reading of 'Ember' was not unanimous";
        Assert.Equal(0, (await CliAsync("add", "Personal", "--content", "Ember reviewed PR #48.")).Code);

        var added = await CliAsync("concept", "add", "Ember (peer)", "--fact", "reviewed PR #48", "--source", "1",
            "--by", "extractor:abc", "--flag", reason);
        Assert.True(added.Code == 0, added.Err);
        Assert.Contains("#1 [flagged: referent unsure] Ember (peer): reviewed PR #48", added.Out);

        // Read back in a separate process, so what's checked is what the store kept, not what add printed.
        var shown = await CliAsync("concept", "show", "Ember (peer)");
        Assert.Contains("#1 [flagged: referent unsure] reviewed PR #48", shown.Out);
        Assert.Contains($"flagged by extractor:abc: {reason}", shown.Out);
        Assert.Contains("Flagged (1):", (await CliAsync("concept", "review")).Out);
    }

    [Fact]
    public async Task Concept_add_without_flag_writes_the_fact_plain()
    {
        Assert.Equal(0, (await CliAsync("add", "Personal", "--content", "Ember reviewed PR #48.")).Code);
        Assert.Equal(0, (await CliAsync("concept", "add", "Ember (peer)", "--fact", "reviewed PR #48", "--source", "1", "--by", "extractor:abc")).Code);

        var shown = await CliAsync("concept", "show", "Ember (peer)");
        Assert.Contains("#1 reviewed PR #48", shown.Out);
        Assert.DoesNotContain("flagged", shown.Out);
    }

    private async Task<(int Code, string Out, string Err)> CliAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "persistence-store.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(Path.Combine(dir, "store.db"));
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("PERSISTENCE_") || k == "CLAUDE_CODE_SESSION_ID").ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["PERSISTENCE_EMBEDDING_ENABLED"] = "false";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await output, await errors);
    }
}
