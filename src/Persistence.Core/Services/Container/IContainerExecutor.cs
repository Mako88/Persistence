namespace Persistence.Services.Container;

/// <summary>
/// The outcome of a <c>shell</c> command. <see cref="Allowed"/> is false (with a
/// <see cref="RejectionReason"/>) when the allowlist blocked it before anything ran; otherwise
/// <see cref="Output"/> carries the combined, capped output.
/// </summary>
public readonly record struct ContainerExecResult(
    bool Allowed, string? RejectionReason, string Output, bool TimedOut, bool Truncated, int ExitCode);

/// <summary>
/// The outcome of a <c>read_file</c>. <see cref="Found"/> is false when the path doesn't exist or
/// isn't a regular file (with a <see cref="Error"/>); otherwise <see cref="Content"/> is the requested
/// line window, <see cref="TotalLines"/> is the file's full line count, and <see cref="FirstLine"/>/
/// <see cref="LastLine"/> are the 1-based bounds actually returned (so the peer can page).
/// </summary>
public readonly record struct ContainerReadResult(
    bool Found, string Content, int TotalLines, int FirstLine, int LastLine,
    bool Truncated, bool TimedOut, string? Error);

/// <summary>
/// Runs a command line inside the peer's sandboxed container "computer". Enforces the deny-by-default
/// allowlist, persists the working directory across calls, and shells out via <c>docker exec</c>.
/// </summary>
public interface IContainerExecutor
{
    Task<ContainerExecResult> ExecuteAsync(string commandLine, CancellationToken ct);

    /// <summary>
    /// Reads a window of <paramref name="limit"/> lines from <paramref name="path"/> starting at
    /// 0-based line <paramref name="offset"/>, resolving relative paths against the persisted working
    /// directory. A structured, allowlist-exempt op (the handler builds it, not the peer), so the
    /// program set doesn't gate it.
    /// </summary>
    Task<ContainerReadResult> ReadFileAsync(string path, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> (creating parent directories),
    /// overwriting or, when <paramref name="append"/> is true, appending. Relative paths resolve
    /// against the persisted working directory. Content is base64-carried into the container, so no
    /// quoting/escaping of it is needed. Allowlist-exempt (handler-built).
    /// </summary>
    Task<ContainerExecResult> WriteFileAsync(string path, string content, bool append, CancellationToken ct);

    /// <summary>
    /// Returns the last <paramref name="lines"/> log lines of a container (host-side
    /// <c>docker logs</c>), so the peer can troubleshoot its own computer.
    /// </summary>
    Task<string> GetLogsAsync(string containerName, int lines, CancellationToken ct);
}
