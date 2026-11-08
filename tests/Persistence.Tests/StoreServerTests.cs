using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Persistence.Config;
using Persistence.Store;

namespace Persistence.Tests;

/// <summary>
/// `persistence-store serve` runs one command at a time, so the callers it cannot finish must not cost the
/// ones after them: a caller that never finishes its line held every caller behind it, and a caller that
/// left before its reply must not end the server (it was found ended on 2026-11-08, cause not shown).
/// </summary>
public sealed class StoreServerTests : IDisposable
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}.db");

    public void Dispose() => TestDatabase.Cleanup(db);

    private async Task<int> ServeAsync(TimeSpan each)
    {
        var config = new AppConfig { PeerName = "Claude" };
        config.DatabasePath = db;
        var store = await FragmentStore.OpenAsync(config);
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        _ = Task.Run(() => Server.ServeAsync(store, db, port, async (_, _) =>
        {
            await Task.Delay(each);
            System.Console.Out.Write("answered");
            return 0;
        }));
        await Task.Delay(300);
        return port;
    }

    private static async Task<string?> AskAsync(int port, TimeSpan patience)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"args\": [\"help\"], \"stdin\": \"\"}\n"));
        using var reader = new StreamReader(stream);
        using var gone = new CancellationTokenSource(patience);
        try
        {
            var line = await reader.ReadLineAsync(gone.Token);
            return line is null ? null : JsonDocument.Parse(line).RootElement.GetProperty("out").GetString();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_caller_that_leaves_before_its_reply_does_not_end_the_server()
    {
        var port = await ServeAsync(TimeSpan.FromMilliseconds(500));

        Assert.Null(await AskAsync(port, TimeSpan.FromMilliseconds(100)));  // gives up and closes, as a hook past its deadline does
        await Task.Delay(800);

        Assert.Equal("answered", await AskAsync(port, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_caller_that_never_finishes_its_line_does_not_hold_the_rest()
    {
        var port = await ServeAsync(TimeSpan.Zero);
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);  // connected, and sends nothing

        Assert.Equal("answered", await AskAsync(port, TimeSpan.FromSeconds(10)));
    }
}
