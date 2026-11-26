namespace Persistence.Tests;

/// <summary>
/// Close's exit codes through the real CLI (#96 point 5, call (d)): a malformed command exits 1 whatever the store
/// holds; a well-formed one the store refuses exits 2. In-process tests pin the exception classes; this pins that
/// Program.cs maps them, so a refusal can't quietly become a usage error or the reverse.
/// </summary>
public sealed class CloseCliTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("close-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private Task<(int Code, string Out, string Err)> CliAsync(params string[] args) => StoreCli.RunAsync(dir, args);

    [Fact]
    public async Task Malformed_close_and_reopen_exit_1_and_store_refusals_exit_2()
    {
        // Mutation: throw RefusedException for the empty reason (or Usage for a missing ended-by) → the codes swap.
        var plain = await CliAsync("add", "Personal", "--content", "PR waits on X.");
        Assert.True(plain.Code == 0, plain.Err);
        var state = await CliAsync("add", "Personal", "--content", "Waiting on X.", "--state");
        Assert.True(state.Code == 0, state.Err);

        async Task Exits(int code, string expect, params string[] args)
        {
            var run = await CliAsync(args);
            Assert.True(run.Code == code, $"{string.Join(' ', args)}: exit {run.Code}, wanted {code}; stderr: {run.Err}");
            Assert.Contains(expect, run.Err);
        }

        await Exits(1, "--reason", "close", "1", "--ended-by", "msg:1", "--reason", " ");
        await Exits(1, "isn't an ended-by", "close", "1", "--ended-by", "pr:95", "--reason", "X happened");
        await Exits(1, "isn't an ended-by", "close", "999999", "--ended-by", "pr:95", "--reason", "X happened");
        await Exits(1, "--reason", "reopen", "1", "--reason", "");

        await Exits(2, "--ended-by", "close", "1", "--reason", "X happened");
        await Exits(2, "no fragment", "close", "999999", "--ended-by", "msg:1", "--reason", "X happened");
        await Exits(2, "isn't closed", "reopen", "2", "--reason", "it holds again");

        Assert.Equal(0, (await CliAsync("close", "2", "--reason", "X happened")).Code);
        await Exits(2, "already closed", "close", "2", "--reason", "again");
    }
}
