namespace Persistence.Tests;

/// <summary>
/// Close PR B through the real CLI: the close that closes a fact's last source says what it drafted, the drafts list,
/// and `concept end-draft` maps a malformed command to exit 1 and a store refusal to exit 2.
/// </summary>
public sealed class EndDraftCliTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("enddraft-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private Task<(int Code, string Out, string Err)> CliAsync(params string[] args) => StoreCli.RunAsync(dir, args);

    private async Task<string> OkAsync(params string[] args)
    {
        var run = await CliAsync(args);
        Assert.True(run.Code == 0, $"{string.Join(' ', args)}: exit {run.Code}; stderr: {run.Err}");
        return run.Out;
    }

    private async Task Exits(int code, string expect, params string[] args)
    {
        var run = await CliAsync(args);
        Assert.True(run.Code == code, $"{string.Join(' ', args)}: exit {run.Code}, wanted {code}; stderr: {run.Err}");
        Assert.Contains(expect, run.Err);
    }

    [Fact]
    public async Task Close_drafts_lists_and_end_draft_decides_with_exit_codes()
    {
        // Mutation: drop the drafted line after `close`, the end-drafts/end-draft cases, or the approve/drop flags → red.
        await OkAsync("add", "Personal", "--content", "PR 99 waits on review.", "--state");
        await OkAsync("concept", "add", "Glider", "--fact", "waits on PR 99", "--source", "1", "--kind", "thing");
        var closed = await OkAsync("close", "1", "--ended-by", "commit:Persistence@abcdef1", "--reason", "PR 99 merged");
        Assert.Contains("Drafted the end of fact #1 (end draft #1)", closed);
        Assert.Contains("end draft #1", await OkAsync("concept", "end-drafts"));

        await Exits(1, "exactly one of --approve or --drop", "concept", "end-draft", "1", "--reason", "why");
        await Exits(1, "--reason", "concept", "end-draft", "1", "--approve");
        await Exits(1, "not a draft id", "concept", "end-draft", "x", "--approve", "--reason", "why");
        await Exits(2, "no end draft #9", "concept", "end-draft", "9", "--approve", "--reason", "why");

        Assert.Contains("Fact #1 ended", await OkAsync("concept", "end-draft", "1", "--approve", "--reason", "the wait ended"));
        await Exits(2, "not pending", "concept", "end-draft", "1", "--drop", "--reason", "too late");
        Assert.Contains("No end drafts pending.", await OkAsync("concept", "end-drafts"));
    }
}
