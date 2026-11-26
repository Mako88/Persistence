using System.Diagnostics;

namespace Persistence.Tests;

/// <summary>
/// Runs persistence-store.dll as a separate process against <c>&lt;dir&gt;/store.db</c>, with no ambient store or
/// session from the environment and embeddings off: what's checked is what crosses Program.cs, exit code included.
/// </summary>
internal static class StoreCli
{
    public static async Task<(int Code, string Out, string Err)> RunAsync(string dir, params string[] args)
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
