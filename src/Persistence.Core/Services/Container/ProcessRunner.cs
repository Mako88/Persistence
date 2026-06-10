using Persistence.DI;
using System.Diagnostics;
using System.Text;

namespace Persistence.Services.Container;

/// <summary>
/// <see cref="IProcessRunner"/> over <see cref="Process"/>. Redirects stdout/stderr, enforces a
/// timeout (killing the whole process tree), and caps each captured stream at the byte limit. The
/// only type in the codebase that launches an external process.
/// </summary>
[Singleton(typeof(IProcessRunner))]
public class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        int maxOutputBytes,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                startInfo.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var truncated = false;

        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        void Append(StringBuilder sb, string? data)
        {
            if (data is null)
            {
                return;
            }

            // +1 for the newline the event strips; keep within the cap and flag the overflow.
            if (sb.Length < maxOutputBytes)
            {
                sb.AppendLine(data);
            }

            if (sb.Length >= maxOutputBytes)
            {
                truncated = true;
            }
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested; // distinguish timeout from caller cancellation
            TryKill(process);

            if (!timedOut)
            {
                throw; // genuine caller cancellation propagates
            }
        }

        var exitCode = timedOut ? -1 : process.ExitCode;

        return new ProcessResult(
            exitCode,
            Cap(stdout.ToString(), maxOutputBytes),
            Cap(stderr.ToString(), maxOutputBytes),
            timedOut,
            truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process already gone or inaccessible — nothing to clean up.
        }
    }

    private static string Cap(string text, int maxBytes) =>
        text.Length <= maxBytes ? text : text[..maxBytes];
}
