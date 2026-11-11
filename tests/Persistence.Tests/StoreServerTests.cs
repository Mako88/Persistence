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
        return (await StartAsync(listening => Server.ServeAsync(store, db, 0, async (_, _) =>
        {
            await Task.Delay(each);
            System.Console.Out.Write("answered");
            return 0;
        }, listening: listening)))!.Value;
    }

    /// <summary>
    /// Starts a server and returns once it accepts, with the TCP port it bound (port 0: the kernel picks, so no other
    /// process can take it between choosing and binding). A fixed 300 ms wait here was too short under load.
    /// </summary>
    private static async Task<int?> StartAsync(Func<Action<int?>, Task<int>> serve)
    {
        var ready = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(() => serve(port => ready.TrySetResult(port)));
        var first = await Task.WhenAny(ready.Task, server, Task.Delay(TimeSpan.FromSeconds(10)));
        if (first == server)
        {
            await server;  // its exception, if it threw while starting
            Assert.Fail("the store server ended before it was listening");
        }

        if (first != ready.Task)
        {
            Assert.Fail("the store server was not listening within 10 s");
        }

        return await ready.Task;
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

    private async Task<(string Socket, int? Port)> ServeOnSocketAsync(IReadOnlySet<uint> allowed, int? port = null)
    {
        var config = new AppConfig { PeerName = "Claude" };
        config.DatabasePath = db;
        var store = await FragmentStore.OpenAsync(config);
        var socket = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}.sock");
        var bound = await StartAsync(listening => Server.ServeAsync(store, db, port, async (_, _) =>
        {
            System.Console.Out.Write("answered");
            return 0;
        }, socket, allowed, listening));
        return (socket, bound);
    }

    private static async Task<(int Code, string Out, string Err)> AskSocketAsync(string path, TimeSpan before = default)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await Task.Delay(before);
        await using var stream = new NetworkStream(client);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"args\": [\"help\"], \"stdin\": \"\"}\n"));
        using var reader = new StreamReader(stream);
        var reply = JsonDocument.Parse(await reader.ReadLineAsync() ?? "{}").RootElement;
        return (reply.GetProperty("code").GetInt32(), reply.GetProperty("out").GetString()!, reply.GetProperty("err").GetString()!);
    }

    [Fact]
    public async Task A_caller_whose_user_is_not_allowed_is_refused_on_the_socket()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Every user but this test's own: the caller is "another self's" user, as Arden's sessions are to my store.
        var (socket, _) = await ServeOnSocketAsync(new HashSet<uint> { Server.CurrentUser() + 1 });

        var (code, output, errors) = await AskSocketAsync(socket);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains($"user {Server.CurrentUser()} may not use this store", errors);
    }

    [Fact]
    public async Task A_refused_caller_slow_to_send_its_line_still_reads_why()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Under load a caller's line can arrive after the server has decided to refuse it. The refusal was
        // written and the connection closed at once, so the late line met a closed socket (EPIPE) and the
        // caller never learned why: the flake Arden saw right after a build (2026-11-11).
        var (socket, _) = await ServeOnSocketAsync(new HashSet<uint> { Server.CurrentUser() + 1 });

        var (code, _, errors) = await AskSocketAsync(socket, before: TimeSpan.FromMilliseconds(300));

        Assert.Equal(2, code);
        Assert.Contains("may not use this store", errors);
    }

    [Fact]
    public async Task An_allowed_user_is_answered_on_the_socket_and_the_socket_is_not_open_to_others()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (socket, _) = await ServeOnSocketAsync(new HashSet<uint> { Server.CurrentUser() });

        Assert.Equal((0, "answered", ""), await AskSocketAsync(socket));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(socket) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    [Fact]
    public async Task A_socket_left_by_an_ended_server_is_replaced_and_a_live_one_is_not_taken()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (socket, _) = await ServeOnSocketAsync(new HashSet<uint> { Server.CurrentUser() });
        var config = new AppConfig { PeerName = "Claude", DatabasePath = db };
        var second = Server.ServeAsync(await FragmentStore.OpenAsync(config), db, null, (_, _) => Task.FromResult(0), socket, new HashSet<uint>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(0, (await AskSocketAsync(socket)).Code);
    }

    [Fact]
    public async Task Socket_and_port_together_check_the_socket_and_answer_the_port()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A remote machine's tunnel still forwards to the port while every local caller has moved to the socket.
        var (socket, port) = await ServeOnSocketAsync(new HashSet<uint> { Server.CurrentUser() + 1 }, 0);

        Assert.Equal(2, (await AskSocketAsync(socket)).Code);
        Assert.Equal("answered", await AskAsync(port!.Value, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, (await AskSocketAsync(socket)).Code);
    }
}
