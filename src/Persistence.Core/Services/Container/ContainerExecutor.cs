using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Services.Container;

/// <summary>
/// Runs <c>shell</c> commands inside the peer's container via <c>docker exec</c>. Enforces the
/// deny-by-default allowlist (every pipeline/chain segment's program must be permitted), persists the
/// working directory across the otherwise-stateless exec calls, and caps output. The container's own
/// isolation — not this allowlist — is the real security boundary (interpreters are permitted), so
/// shell operators and redirects are allowed; the allowlist curates which entrypoints exist.
/// </summary>
[Singleton(typeof(IContainerExecutor))]
public class ContainerExecutor : IContainerExecutor
{
    // A line the wrapper script emits last so we can recover the cwd after any cd the command did.
    private const string CwdMarker = "__PERSISTENCE_CWD__:";

    private readonly IProcessRunner processRunner;
    private readonly IAppConfig config;
    private readonly ISessionContext session;

    /// <summary>
    /// Constructor
    /// </summary>
    public ContainerExecutor(IProcessRunner processRunner, IAppConfig config, ISessionContext session)
    {
        this.processRunner = processRunner;
        this.config = config;
        this.session = session;
    }

    /// <inheritdoc />
    public async Task<ContainerExecResult> ExecuteAsync(string commandLine, CancellationToken ct)
    {
        var settings = config.Container;

        if (CheckAllowlist(commandLine, settings.Allowlist) is { } rejection)
        {
            return new ContainerExecResult(Allowed: false, rejection, Output: string.Empty,
                TimedOut: false, Truncated: false);
        }

        var cwd = string.IsNullOrEmpty(session.ContainerCwd) ? settings.WorkingDir : session.ContainerCwd;
        var script = BuildScript(commandLine, cwd, settings.WorkingDir);

        var env = settings.DockerHost is { Length: > 0 } host
            ? new Dictionary<string, string> { ["DOCKER_HOST"] = host }
            : null;

        var result = await processRunner.RunAsync(
            "docker",
            ["exec", settings.Name, "sh", "-lc", script],
            settings.TimeoutSeconds,
            settings.MaxOutputBytes,
            env,
            ct);

        var (output, newCwd) = ExtractCwd(result.Stdout);

        if (newCwd is { Length: > 0 })
        {
            session.ContainerCwd = newCwd;
        }

        return new ContainerExecResult(
            Allowed: true,
            RejectionReason: null,
            Output: Combine(output, result.Stderr),
            result.TimedOut,
            result.Truncated);
    }

    /// <summary>
    /// Returns null if every segment's program is allowlisted, otherwise a peer-facing rejection
    /// message naming the offending program and the allowed set.
    /// </summary>
    private static string? CheckAllowlist(string commandLine, string[] allowlist)
    {
        var allowed = new HashSet<string>(allowlist, StringComparer.Ordinal);
        var programs = ShellCommandParser.ExtractProgramNames(commandLine);

        if (programs.Count == 0)
        {
            return "No command given.";
        }

        foreach (var program in programs)
        {
            if (!allowed.Contains(program))
            {
                var sorted = string.Join(", ", allowlist.OrderBy(a => a, StringComparer.Ordinal));
                return $"'{program}' is not permitted in your computer. Allowed: {sorted}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps the command so the shell starts in the persisted cwd (falling back to the working dir if
    /// it has gone away) and reports the final cwd, preserving the command's own exit code.
    /// </summary>
    private static string BuildScript(string commandLine, string cwd, string workingDir) =>
        $"cd {Quote(cwd)} 2>/dev/null || cd {Quote(workingDir)}\n" +
        $"{commandLine}\n" +
        "__rc=$?\n" +
        $"printf '\\n{CwdMarker}%s\\n' \"$(pwd)\"\n" +
        "exit $__rc";

    /// <summary>Strips the trailing cwd-marker line from stdout and returns the recovered cwd.</summary>
    private static (string Output, string? Cwd) ExtractCwd(string stdout)
    {
        var lines = stdout.Split('\n');
        string? cwd = null;
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (line.StartsWith(CwdMarker, StringComparison.Ordinal))
            {
                cwd = line[CwdMarker.Length..].Trim();
            }
            else
            {
                kept.Add(line);
            }
        }

        return (string.Join('\n', kept).TrimEnd('\n'), cwd);
    }

    private static string Combine(string stdout, string stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : (string.IsNullOrEmpty(stdout) ? "" : stdout + "\n") + $"[stderr]\n{stderr.TrimEnd('\n')}";

    /// <summary>Single-quotes a value for safe interpolation into the shell script.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
