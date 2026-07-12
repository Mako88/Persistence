namespace Persistence.Config;

/// <summary>
/// Settings for the peer's "computer" — a sandboxed Docker container reached through the
/// <c>shell</c> command. Shared infra (not model-coupled). The <see cref="Allowlist"/> is a
/// deny-by-default *curation* of which programs the peer may invoke, with clear errors for the rest;
/// because it includes interpreters (python/bash), the real security boundary is the container's
/// isolation (non-privileged, dropped capabilities, read-only rootfs except <see cref="WorkingDir"/>,
/// resource limits, no host mounts, egress-only) — see the container assets.
/// </summary>
public class ContainerSettings
{
    /// <summary>Whether the <c>shell</c> command is available. Off until the container is running.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true, the peer's runtime is itself running <em>inside</em> its container (the ADR-0007
    /// body/mind model), so <c>shell</c> commands run as local processes (<c>sh -lc</c>) in this same
    /// container rather than via <c>docker exec</c> into a separate one. The peer inhabits its computer
    /// instead of reaching into a sidecar. Off by default (the legacy host→sidecar model), enabled in the
    /// peer container image via <c>PERSISTENCE_CONTAINER_LOCAL=true</c>.
    /// </summary>
    public bool Local { get; set; } = false;

    /// <summary>Name (or id) of the sidecar container to <c>docker exec</c> into (legacy, non-<see cref="Local"/> mode).</summary>
    public string Name { get; set; } = "persistence-computer";

    /// <summary>Name of the search (SearXNG) container, so the peer can read its logs to troubleshoot search.</summary>
    public string SearchContainerName { get; set; } = "persistence-searxng";

    /// <summary>Optional Docker host (passed as <c>DOCKER_HOST</c>); null uses the default socket/pipe.</summary>
    public string? DockerHost { get; set; }

    /// <summary>Directory the peer starts in and returns to — its persistent workspace ("sense of place").</summary>
    public string WorkingDir { get; set; } = "/work";

    /// <summary>
    /// When true, the <see cref="Allowlist"/> curation is bypassed entirely — any program may run.
    /// The container's own isolation (non-privileged, dropped capabilities, no host mounts,
    /// egress-only) is still the real boundary, so this trades curation for reach without removing the
    /// sandbox. Off by default; a profile can flip it per participant via
    /// <see cref="ModelProfile.ContainerAllowAll"/>, or set <c>PERSISTENCE_CONTAINER_ALLOWALLCOMMANDS</c>.
    /// </summary>
    public bool AllowAllCommands { get; set; } = false;

    /// <summary>
    /// Programs the peer may invoke (deny-by-default). v1 covers web tools, interpreters, and basic
    /// file/navigation utilities — enough for a "lab" while keeping unknown programs rejected. Ignored
    /// when <see cref="AllowAllCommands"/> is true.
    /// </summary>
    public string[] Allowlist { get; set; } =
    [
        // Web
        "web_search", "fetch_url", "agent-browser", "curl",
        // Scripting / interpreters / dev toolchain
        "python", "python3", "pip", "pip3", "node", "npm", "sh", "bash",
        "git", "dotnet", "sudo", "tar",
        // Navigation / files / text
        "cd", "ls", "pwd", "cat", "echo", "mkdir", "mv", "cp", "rm", "touch",
        "grep", "head", "tail", "wc", "find", "sed", "awk", "which", "env",
    ];

    /// <summary>Per-command timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Cap on captured output bytes (token efficiency); excess is truncated with a marker.</summary>
    public int MaxOutputBytes { get; set; } = 8000;
}
