using System.Diagnostics;

namespace Persistence.Store;

/// <summary>Which project a folder is in, named as the hooks name it, so a fragment's tag and the core's scope agree.</summary>
internal static class Projects
{
    /// <summary>The repository's name (the main checkout's, from a worktree too), else the folder's; lower case.</summary>
    public static string? Of(string folder)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --path-format=absolute --git-common-dir")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            var common = git?.StandardOutput.ReadToEnd().Trim();
            if (git is not null && git.WaitForExit(10_000) && git.ExitCode == 0 && common is { Length: > 0 })
            {
                return new DirectoryInfo(common).Parent?.Name.ToLowerInvariant();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git: the folder's name serves.
        }

        return new DirectoryInfo(folder).Name.ToLowerInvariant();
    }
}
