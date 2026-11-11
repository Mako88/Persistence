using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Persistence.Tests;

/// <summary>
/// A recall on words alone says so on stderr exactly once, for a script to log (Arden's gap on be7d3dc,
/// msg 1570119107154542869). be7d3dc removed `recall`'s own stderr line because QueryEmbedder.Note already wrote
/// one; nothing pinned that. These run the real CLI, since the doubled line came from Program.cs.
/// </summary>
public sealed class RecallStderrTests : IDisposable
{
    private const string Notice = "Recall ran on exact words alone";
    private readonly string dir = Directory.CreateTempSubdirectory("recall-stderr-").FullName;
    private readonly HttpListener embedder = new();
    private readonly string embedderUrl;

    public RecallStderrTests()
    {
        // A fake OpenAI-compatible embedder: vectors for short texts, HTTP 413 for any text over 60 characters.
        var port = FreePort();
        embedderUrl = $"http://127.0.0.1:{port}/v1";
        embedder.Prefixes.Add($"http://127.0.0.1:{port}/");
        embedder.Start();
        _ = Task.Run(ServeEmbeddingsAsync);
    }

    public void Dispose()
    {
        embedder.Close();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_refused_query_notes_the_fallback_on_stderr_once()
    {
        Assert.Equal(0, (await CliAsync(embedderUrl, "add", "Personal", "--content", "the recall trim setting")).Code);

        var query = "what did we decide about the recall trim setting yesterday afternoon, and why";
        foreach (var json in new[] { false, true })
        {
            var run = await CliAsync(embedderUrl, json ? ["recall", query, "--json"] : ["recall", query]);
            Assert.Equal(0, run.Code);
            Assert.Equal(1, Count(run.Err, Notice));
        }
    }

    [Fact]
    public async Task A_store_with_no_vectors_notes_the_fallback_on_stderr_once()
    {
        // The embedder is unreachable, so the fragment is kept without a vector.
        var nowhere = $"http://127.0.0.1:{FreePort()}/v1";
        Assert.Equal(0, (await CliAsync(nowhere, "add", "Personal", "--content", "the recall trim setting")).Code);

        foreach (var json in new[] { false, true })
        {
            var run = await CliAsync(nowhere, json ? ["recall", "recall trim setting", "--json"] : ["recall", "recall trim setting"]);
            Assert.Equal(0, run.Code);
            Assert.Contains("no memory in this store has a vector", run.Err);
            Assert.Equal(1, Count(run.Err, Notice));
        }
    }

    private async Task<(int Code, string Out, string Err)> CliAsync(string embeddingUrl, params string[] args)
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

        start.Environment["PERSISTENCE_EMBEDDING_ENABLED"] = "true";
        start.Environment["PERSISTENCE_EMBEDDING_APIBASEURL"] = embeddingUrl;
        start.Environment["PERSISTENCE_EMBEDDING_REQUESTTIMEOUTSECONDS"] = "5";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await output, await errors);
    }

    private async Task ServeEmbeddingsAsync()
    {
        while (embedder.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await embedder.GetContextAsync();
            }
            catch (Exception) when (!embedder.IsListening)
            {
                return;
            }

            using var body = JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync());
            var input = body.RootElement.GetProperty("input");
            var texts = input.ValueKind == JsonValueKind.Array ? input.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : [input.GetString() ?? ""];
            byte[] reply;
            if (texts.Any(t => t.Length > 60))
            {
                context.Response.StatusCode = 413;
                reply = Encoding.UTF8.GetBytes("{\"error\": \"input too large\"}");
            }
            else
            {
                var data = texts.Select((t, i) => new { index = i, embedding = Vector(t) });
                reply = JsonSerializer.SerializeToUtf8Bytes(new { data });
            }

            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(reply);
            context.Response.Close();
        }
    }

    private static float[] Vector(string text)
    {
        var vector = new float[8];
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            vector[(word.GetHashCode() & 0x7fffffff) % 8] += 1;
        }

        vector[0] += 0.01f;
        return vector;
    }

    private static int Count(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
