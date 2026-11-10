using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Persistence.Store;

/// <summary>
/// The store kept open in a process that stays up. A hook that started the CLI paid 1.7 seconds before
/// any work, on every message: the runtime, the container and the database opened each time. Here they
/// open once, and a call is one line of JSON over a local socket each way:
/// <c>{"args": [...], "stdin": "..."}</c> in, <c>{"code": 0, "out": "...", "err": "..."}</c> back.
///
/// <para>
/// Commands run one at a time, each with its own console, so their output and their stdin are their own.
/// The server ends itself when the published CLI (<c>PERSISTENCE_STORE_EXE</c>) changes, so a new build is
/// running from the next call on, and when asked for a store other than the one it holds.
/// </para>
/// </summary>
internal static class Server
{
    public const int DefaultPort = 8095;

    private static readonly TimeSpan RequestArrives = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serves on a unix socket (<paramref name="socketPath"/>) when one is given, and then answers only callers whose
    /// user is in <paramref name="allowed"/>: the kernel says who connected (SO_PEERCRED), which a TCP port cannot.
    /// On TCP every local user's process could run any command on any store, accept included (Arden, 2026-11-10).
    /// Both at once while a remote machine's tunnel still forwards to the port: <paramref name="port"/> null serves the socket only.
    /// </summary>
    public static async Task<int> ServeAsync(
        FragmentStore store, string database, int? port, Func<CommandLine, FragmentStore, Task<int>> run,
        string? socketPath = null, IReadOnlySet<uint>? allowed = null)
    {
        var published = Environment.GetEnvironmentVariable("PERSISTENCE_STORE_EXE") is { Length: > 0 } exe && File.Exists(exe) ? exe : null;
        var built = published is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(published);
        var listeners = new List<Socket>();
        if (socketPath is not null)
        {
            listeners.Add(ListenUnix(socketPath));
        }

        if (port is { } tcp || socketPath is null)
        {
            listeners.Add(ListenTcp(port ?? DefaultPort));
        }

        var (stdout, stderr, stdin) = (Console.Out, Console.Error, Console.In);
        if (socketPath is not null)
        {
            stderr.WriteLine($"persistence-store serving {database} on {socketPath}, to users {string.Join(",", allowed ?? new HashSet<uint>())}");
        }

        if (port is not null || socketPath is null)
        {
            stderr.WriteLine($"persistence-store serving {database} on 127.0.0.1:{port ?? DefaultPort}, with no check of who calls");
        }

        // One accept waiting per listener; the calls themselves still run one at a time.
        var waiting = listeners.ToDictionary(l => l, l => l.AcceptAsync());
        try
        {
            while (true)
            {
                var done = await Task.WhenAny(waiting.Values);
                var from = waiting.First(w => w.Value == done).Key;
                waiting[from] = from.AcceptAsync();
                await AnswerAsync(await done);
            }
        }
        catch (EndOfServe)
        {
            return 0;
        }
        finally
        {
            foreach (var l in listeners)
            {
                l.Dispose();
            }
        }

        async Task AnswerAsync(Socket accepted)
        {
            await using var stream = new NetworkStream(accepted, ownsSocket: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            if (accepted.AddressFamily == AddressFamily.Unix && PeerUser(accepted) is var caller && (caller is null || allowed is null || !allowed.Contains(caller.Value)))
            {
                stderr.WriteLine($"refused a caller: user {caller?.ToString() ?? "unknown"} is not one this store answers");
                try
                {
                    await writer.WriteLineAsync(Reply(2, "", $"refused: user {caller?.ToString() ?? "unknown"} may not use this store"));
                }
                catch (IOException)
                {
                }

                return;
            }

            string reply;
            try
            {
                // Commands run one at a time, so a caller that connects and never finishes its line would
                // hold every caller behind it: its line has a few seconds to arrive.
                using var arrive = new CancellationTokenSource(RequestArrives);
                var request = JsonSerializer.Deserialize<Request>(await reader.ReadLineAsync(arrive.Token) ?? "{}") ?? new Request();
                var line = CommandLine.Parse(request.Args ?? []);
                if (line.Get("db") is { } asked && !string.Equals(Path.GetFullPath(asked), database, StringComparison.OrdinalIgnoreCase))
                {
                    reply = Reply(1, "", $"this server holds {database}, not {asked}");
                }
                else
                {
                    var (output, errors) = (new StringWriter(), new StringWriter());
                    Console.SetOut(output);
                    Console.SetError(errors);
                    Console.SetIn(new StringReader(request.Stdin ?? ""));
                    int code;
                    try
                    {
                        code = await run(line, store);
                    }
                    catch (UsageException e)
                    {
                        errors.WriteLine(e.Message);
                        code = 1;
                    }
                    catch (RefusedException e)
                    {
                        errors.WriteLine(e.Message);
                        code = 2;
                    }
                    finally
                    {
                        Console.SetOut(stdout);
                        Console.SetError(stderr);
                        Console.SetIn(stdin);
                    }

                    reply = Reply(code, output.ToString(), errors.ToString());
                }
            }
            catch (Exception e)
            {
                reply = Reply(3, "", $"the server could not run that: {e.Message}");
            }

            try
            {
                await writer.WriteLineAsync(reply);
            }
            catch (IOException)
            {
                // The caller gave up waiting (a hook has a deadline) and closed its end. This write can throw
                // outside any handler, where it would end the server and every later call would pay for a fresh start.
                stderr.WriteLine("a caller left before its reply: dropped");
            }

            if (published is not null && File.Exists(published) && File.GetLastWriteTimeUtc(published) != built)
            {
                stderr.WriteLine("the published CLI changed: ending, so the next call starts the new one");
                throw new EndOfServe();
            }
        }
    }

    private sealed class EndOfServe : Exception;

    private static Socket ListenTcp(int port)
    {
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
        tcp.Listen(16);
        return tcp;
    }

    private static Socket ListenUnix(string socketPath)
    {

        // A socket left by a server that ended is in the way of the bind. One that answers is a live server: not ours to take.
        if (File.Exists(socketPath))
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(socketPath));
                throw new InvalidOperationException($"another server already answers on {socketPath}");
            }
            catch (SocketException) when (new FileInfo(socketPath).Length == 0)
            {
                File.Delete(socketPath);
            }
        }

        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        unix.Bind(new UnixDomainSocketEndPoint(socketPath));
        // The group may connect (the caller's user is still checked); the folder around it is the owner's.
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        unix.Listen(16);
        return unix;
    }

    /// <summary>The user id of the process on the other end of a unix socket, from the kernel (SO_PEERCRED); null if it cannot say.</summary>
    internal static uint? PeerUser(Socket socket)
    {
        if (socket.AddressFamily != AddressFamily.Unix || !OperatingSystem.IsLinux())
        {
            return null;
        }

        var credentials = new byte[12];  // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        try
        {
            return socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials) == 12 ? BitConverter.ToUInt32(credentials, 4) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>The user this process runs as.</summary>
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint CurrentUser();

    private static string Reply(int code, string output, string errors) =>
        JsonSerializer.Serialize(new { code, @out = output, err = errors });

    private sealed class Request
    {
        [System.Text.Json.Serialization.JsonPropertyName("args")]
        public List<string>? Args { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("stdin")]
        public string? Stdin { get; set; }
    }
}
