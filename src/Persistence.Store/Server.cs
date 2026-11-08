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

    public static async Task<int> ServeAsync(
        FragmentStore store, string database, int port, Func<CommandLine, FragmentStore, Task<int>> run)
    {
        var published = Environment.GetEnvironmentVariable("PERSISTENCE_STORE_EXE") is { Length: > 0 } exe && File.Exists(exe) ? exe : null;
        var built = published is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(published);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var (stdout, stderr, stdin) = (Console.Out, Console.Error, Console.In);
        stderr.WriteLine($"persistence-store serving {database} on 127.0.0.1:{port}");

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

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
                return 0;
            }
        }
    }

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
