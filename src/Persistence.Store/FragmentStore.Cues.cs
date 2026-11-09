using Persistence.Data.Entities;
using System.Text.RegularExpressions;

namespace Persistence.Store;

/// <summary>
/// Cues: recall by intent rather than by association. A cue is a tag <c>cue/&lt;moment&gt;/&lt;name&gt;</c> whose
/// description is a pattern; the fragments tagged with it come in when the pattern matches at its moment:
/// <c>prompt</c> (the human's words: "receiving" from his dictation means the receipting service), <c>tool</c>
/// (a tool call about to run, as <c>Tool: {input}</c>: Python inside PowerShell), or <c>stop</c> (the peer's
/// last message as a turn ends: a promise of work with nothing started). A habit from training does not
/// yield to a fragment read hours ago in the core; it yields to a reminder at the moment it acts.
/// </summary>
internal sealed partial class FragmentStore
{
    public const string CueTag = "cue";

    public static readonly IReadOnlyList<string> Moments = ["prompt", "tool", "stop"];

    /// <summary>Makes or changes a cue: the tag <c>cue/&lt;moment&gt;/&lt;name&gt;</c> with <paramref name="pattern"/> as its description.</summary>
    public async Task<string> SetCueAsync(string path, string pattern)
    {
        var (moment, name) = CuePath(path);
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException e)
        {
            throw new UsageException($"--when '{pattern}' is not a pattern: {e.Message}");
        }

        var tag = (await TagsAsync([$"{CueTag}/{moment}/{name}"])).Single();
        tag.Description = pattern;
        await tags.SaveAsync(tag);
        return $"{CueTag}/{moment}/{name}";
    }

    /// <summary>Removes a cue: its tag goes, its fragments stay (untagged by it) for the writer to archive or keep.</summary>
    public async Task<string> RemoveCueAsync(string path)
    {
        var (moment, name) = CuePath(path);
        var cue = await FindTagAsync($"{CueTag}/{moment}/{name}") ?? throw new RefusedException($"There is no cue {CueTag}/{moment}/{name}.");
        var kept = (await fragments.GetByTagAsync(cue.Id)).Select(f => $"#{f.Id}").ToList();
        await tags.DeleteTreeAsync(cue.Id);
        return $"{CueTag}/{moment}/{name} is removed" + (kept.Count > 0 ? $"; its fragments stay: {string.Join(", ", kept)} (archive what no longer serves)." : ".");
    }

    /// <summary>Each cue with its pattern and the fragments it brings.</summary>
    public async Task<string> CueListAsync()
    {
        var lines = new List<string>();
        foreach (var (moment, cue) in await CuesAsync())
        {
            var brings = string.Join(", ", (await fragments.GetByTagAsync(cue.Id)).Where(f => f.Status == ContextFragmentStatus.Active).Select(f => $"#{f.Id}"));
            lines.Add($"{CueTag}/{moment}/{cue.Name} when /{cue.Description}/: " + (brings.Length > 0 ? brings : "(nothing yet: tag a fragment with it)"));
        }

        return lines.Count > 0 ? string.Join('\n', lines) : "(no cues yet: `cue set <moment>/<name> --when <pattern>`)";
    }

    /// <summary>The fragments of every <paramref name="moment"/> cue whose pattern <paramref name="text"/> matches, less <paramref name="exclude"/>.</summary>
    public async Task<IReadOnlyList<ContextFragmentEntity>> CuedAsync(string moment, string text, IReadOnlySet<long> exclude)
    {
        if (!Moments.Contains(moment))
        {
            throw new UsageException($"'{moment}' is not a moment; one of {string.Join(", ", Moments)}.");
        }

        var ids = new List<long>();
        foreach (var (_, cue) in (await CuesAsync()).Where(c => c.Moment == moment))
        {
            bool matched;
            try
            {
                matched = Regex.IsMatch(text, cue.Description!, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException)
            {
                matched = false;
            }

            if (matched)
            {
                ids.AddRange((await fragments.GetByTagAsync(cue.Id))
                    .Where(f => f.Status == ContextFragmentStatus.Active && !exclude.Contains(f.Id) && !ids.Contains(f.Id))
                    .OrderByDescending(f => f.Importance).Select(f => f.Id));
            }
        }

        return await HydratedAsync(ids);
    }

    /// <summary>Every cue that has a pattern, with its moment.</summary>
    private async Task<IReadOnlyList<(string Moment, TagEntity Cue)>> CuesAsync()
    {
        var found = new List<(string, TagEntity)>();
        if (await FindTagAsync(CueTag) is not { } root)
        {
            return found;
        }

        foreach (var moment in await tags.GetChildrenAsync(root.Id))
        {
            found.AddRange((await tags.GetChildrenAsync(moment.Id)).Where(c => c.Description is { Length: > 0 }).Select(c => (moment.Name, c)));
        }

        return found;
    }

    private static (string Moment, string Name) CuePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is [CueTag, ..])
        {
            parts = parts[1..];
        }

        return parts is [var moment, var name] && Moments.Contains(moment)
            ? (moment, name)
            : throw new UsageException($"A cue is <moment>/<name>, the moment one of {string.Join(", ", Moments)}; '{path}' is not.");
    }
}
