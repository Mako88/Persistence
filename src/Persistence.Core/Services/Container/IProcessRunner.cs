namespace Persistence.Services.Container;

/// <summary>
/// The result of running an external process: exit code, captured streams, and whether it was
/// killed for exceeding its timeout or had its output truncated at the byte cap.
/// </summary>
public readonly record struct ProcessResult(
    int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Truncated);

/// <summary>
/// Runs an external process and captures its output. The single seam that touches
/// <see cref="System.Diagnostics.Process"/>, so the container executor can be unit-tested with a
/// mock — no real Docker needed.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> (passed as a list, so no
    /// host shell parses them), capturing stdout/stderr up to <paramref name="maxOutputBytes"/> each
    /// and killing the process tree if it exceeds <paramref name="timeoutSeconds"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        int maxOutputBytes,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct);
}
