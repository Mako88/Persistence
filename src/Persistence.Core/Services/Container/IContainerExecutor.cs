namespace Persistence.Services.Container;

/// <summary>
/// The outcome of a <c>shell</c> command. <see cref="Allowed"/> is false (with a
/// <see cref="RejectionReason"/>) when the allowlist blocked it before anything ran; otherwise
/// <see cref="Output"/> carries the combined, capped output.
/// </summary>
public readonly record struct ContainerExecResult(
    bool Allowed, string? RejectionReason, string Output, bool TimedOut, bool Truncated, int ExitCode);

/// <summary>
/// Runs a command line inside the peer's sandboxed container "computer". Enforces the deny-by-default
/// allowlist, persists the working directory across calls, and shells out via <c>docker exec</c>.
/// </summary>
public interface IContainerExecutor
{
    Task<ContainerExecResult> ExecuteAsync(string commandLine, CancellationToken ct);
}
