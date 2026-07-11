using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using System.Text;

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

        if (CheckAllowlist(commandLine, settings.Allowlist, settings.AllowAllCommands) is { } rejection)
        {
            return new ContainerExecResult(Allowed: false, rejection, Output: string.Empty,
                TimedOut: false, Truncated: false, ExitCode: 0);
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
            result.Truncated,
            result.ExitCode);
    }

    // A stderr sentinel the read script emits when the target path isn't a regular file, so we can
    // tell "missing" apart from an empty file (both otherwise produce no stdout).
    private const string NoFileMarker = "__PERSISTENCE_NOFILE__";

    /// <inheritdoc />
    public async Task<ContainerReadResult> ReadFileAsync(string path, int offset, int limit, CancellationToken ct)
    {
        var settings = config.Container;
        var start = offset + 1;             // sed is 1-based; offset is a 0-based line index
        var end = offset + limit;
        var script =
            CdPreamble(settings) +
            $"if [ ! -f {Quote(path)} ]; then echo {NoFileMarker} 1>&2; exit 3; fi\n" +
            $"awk 'END{{print NR}}' {Quote(path)} 1>&2\n" +      // total line count → stderr
            $"sed -n '{start},{end}p' {Quote(path)}";            // the requested window → stdout

        var result = await DockerExecAsync(script, settings, ct);

        if (result.ExitCode == 3 || result.Stderr.Contains(NoFileMarker, StringComparison.Ordinal))
        {
            return new ContainerReadResult(
                Found: false, Content: string.Empty, TotalLines: 0, FirstLine: 0, LastLine: 0,
                Truncated: false, TimedOut: result.TimedOut, Error: $"no such file: {path}");
        }

        var total = ParseLeadingInt(result.Stderr);
        var firstLine = total == 0 ? 0 : Math.Min(start, total);
        var lastLine = Math.Min(end, total);

        return new ContainerReadResult(
            Found: true,
            Content: result.Stdout.TrimEnd('\n'),
            TotalLines: total,
            FirstLine: firstLine,
            LastLine: lastLine,
            Truncated: result.Truncated,
            TimedOut: result.TimedOut,
            Error: null);
    }

    /// <inheritdoc />
    public async Task<ContainerExecResult> WriteFileAsync(string path, string content, bool append, CancellationToken ct)
    {
        var settings = config.Container;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var redirect = append ? ">>" : ">";
        var script =
            CdPreamble(settings) +
            $"mkdir -p \"$(dirname {Quote(path)})\" 2>/dev/null\n" +
            // base64 carries the content verbatim — no quoting/escaping of arbitrary bytes needed.
            $"printf %s '{b64}' | base64 -d {redirect} {Quote(path)}";

        var result = await DockerExecAsync(script, settings, ct);

        return new ContainerExecResult(
            Allowed: true,
            RejectionReason: null,
            Output: Combine(string.Empty, result.Stderr),
            result.TimedOut,
            result.Truncated,
            result.ExitCode);
    }

    /// <summary>Prefix that starts the script in the persisted cwd (falling back to the working dir).</summary>
    private string CdPreamble(ContainerSettings settings)
    {
        var cwd = string.IsNullOrEmpty(session.ContainerCwd) ? settings.WorkingDir : session.ContainerCwd;
        return $"cd {Quote(cwd)} 2>/dev/null || cd {Quote(settings.WorkingDir)}\n";
    }

    /// <summary>Runs a prepared script in the container via <c>docker exec … sh -lc</c>.</summary>
    private Task<ProcessResult> DockerExecAsync(string script, ContainerSettings settings, CancellationToken ct)
    {
        var env = settings.DockerHost is { Length: > 0 } host
            ? new Dictionary<string, string> { ["DOCKER_HOST"] = host }
            : null;

        return processRunner.RunAsync(
            "docker",
            ["exec", settings.Name, "sh", "-lc", script],
            settings.TimeoutSeconds,
            settings.MaxOutputBytes,
            env,
            ct);
    }

    /// <summary>Reads the first integer on the first non-blank line of <paramref name="text"/> (0 if none).</summary>
    private static int ParseLeadingInt(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (int.TryParse(line.Trim(), out var n))
            {
                return n;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public async Task<string> GetLogsAsync(string containerName, int lines, CancellationToken ct)
    {
        var settings = config.Container;

        var env = settings.DockerHost is { Length: > 0 } host
            ? new Dictionary<string, string> { ["DOCKER_HOST"] = host }
            : null;

        var result = await processRunner.RunAsync(
            "docker",
            ["logs", "--tail", lines.ToString(), containerName],
            settings.TimeoutSeconds,
            settings.MaxOutputBytes,
            env,
            ct);

        // `docker logs` interleaves the container's stdout and stderr; many services log to stderr,
        // so merge both plainly (no [stderr] label — for logs it's all just "the logs").
        var logs = (result.Stdout + result.Stderr).Trim();
        if (logs.Length == 0)
        {
            logs = "(no logs)";
        }

        return result.Truncated ? logs + "\n[output truncated]" : logs;
    }

    /// <summary>
    /// Returns null if the command may run, otherwise a peer-facing rejection message. When
    /// <paramref name="allowAll"/> is set the per-program curation is skipped (only the empty-command
    /// guard remains); otherwise every segment's program must be allowlisted.
    /// </summary>
    private static string? CheckAllowlist(string commandLine, string[] allowlist, bool allowAll)
    {
        var programs = ShellCommandParser.ExtractProgramNames(commandLine);

        if (programs.Count == 0)
        {
            return "No command given.";
        }

        if (allowAll)
        {
            return null; // container isolation is the boundary; curation is off for this peer
        }

        var allowed = new HashSet<string>(allowlist, StringComparer.Ordinal);

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
