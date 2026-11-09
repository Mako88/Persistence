using Persistence.Data.Entities;
using System.Text;

namespace Persistence.Store;

/// <summary>
/// Groups of fragments, by tag. A tag is a group, its child tags are its subgroups, and a fragment can be in
/// as many as it belongs to without being copied. A group's head is the fragment tagged with the group and
/// <see cref="HeadTag"/>: the few lines to hold in mind about it (how a legacy system hangs together), with
/// the details one <c>show</c> away. A loaded group comes into the core as its head, a line for each
/// subgroup, and a line for each member; a group is loaded for the project in hand (<c>project/&lt;name&gt;</c>),
/// by hand for the session (its working note carries the group's tag), and recall brings an unloaded group's
/// head beside a member it surfaces, so touching one detail brings the frame it sits in.
/// </summary>
internal sealed partial class FragmentStore
{
    public const string HeadTag = "head";

    /// <summary>How many members a loaded group lists by line; the rest are named by id.</summary>
    private const int MembersListed = 25;

    /// <summary>A view of the tags for one call: each one's path, its parent, and the heads of groups that have one.</summary>
    private sealed record Groups(
        IReadOnlyDictionary<long, string> Paths,
        IReadOnlyDictionary<long, long?> Parents,
        IReadOnlyDictionary<long, ContextFragmentEntity> Heads)
    {
        public IReadOnlySet<long> HeadIds { get; } = Heads.Values.Select(h => h.Id).ToHashSet();

        /// <summary>The tag and every tag above it.</summary>
        public IEnumerable<long> Lineage(long tag)
        {
            for (long? at = tag; at is { } id; at = Parents.GetValueOrDefault(id))
            {
                yield return id;
            }
        }
    }

    private async Task<Groups> GroupsAsync(bool heads = true)
    {
        var all = (await tags.GetAllAsync()).ToDictionary(t => t.Id);
        var paths = new Dictionary<long, string>();
        string PathOf(TagEntity t) => paths.TryGetValue(t.Id, out var known) ? known
            : paths[t.Id] = t.ParentTagId is { } p && all.TryGetValue(p, out var parent) ? PathOf(parent) + "/" + t.Name : t.Name;
        foreach (var tag in all.Values)
        {
            PathOf(tag);
        }

        var found = new Dictionary<long, ContextFragmentEntity>();
        if (heads && all.Values.FirstOrDefault(t => t.Name == HeadTag && t.ParentTagId is null) is { } head)
        {
            foreach (var fragment in (await fragments.GetByTagAsync(head.Id))
                .Where(f => !f.IsDeleted && f.Status == ContextFragmentStatus.Active).OrderBy(f => f.LastModifiedUtc))
            {
                foreach (var tag in (await HydratedAsync([fragment.Id])).Single().Tags.Where(t => t.Id != head.Id))
                {
                    found[tag.Id] = fragment; // the newest head of a group wins
                }
            }
        }

        return new Groups(paths, all.Values.ToDictionary(t => t.Id, t => t.ParentTagId), found);
    }

    /// <summary>
    /// The groups loaded now: the project's when it has a head (without one its members are the core's own
    /// already), then those this session's working note carries.
    /// </summary>
    private async Task<IReadOnlyList<long>> LoadedAsync(string? project, Groups groups)
    {
        var loaded = new List<long>();
        if (project is { Length: > 0 } && await FindTagAsync($"{ProjectTag}/{project}") is { } own && groups.Heads.ContainsKey(own.Id))
        {
            loaded.Add(own.Id);
        }

        if (await NoteAsync(null) is { } note && (Session is null || note.SessionId == Session))
        {
            loaded.AddRange(note.Tags.Select(t => t.Id).Where(id => !loaded.Contains(id)));
        }

        return loaded;
    }

    /// <summary>Writes a group's head, replacing the one it has; the group's tag is made if it is new.</summary>
    public async Task<ContextFragmentEntity> SetHeadAsync(string path, string content, string? summary)
    {
        var group = (await TagsAsync([path])).Single();
        if ((await GroupsAsync()).Heads.GetValueOrDefault(group.Id) is { } old)
        {
            return await EditAsync(old.Id, null, content, summary, null, null, []);
        }

        return await AddAsync("Summary", content, summary, 0.7f, 0.8f, [path, HeadTag], false);
    }

    /// <summary>Loads a group for this session, or puts it away: the session's working note carries its tag.</summary>
    public async Task<string> LoadAsync(string path, bool load)
    {
        var group = await FindTagAsync(path) ?? throw new RefusedException($"There is no tag '{path}'; `context set {path}` makes the group.");
        if (Session is null)
        {
            throw new UsageException("Loading a group is for a session: pass --session, or run it from the session's shell.");
        }

        var note = await NoteAsync(null);
        if (note is null || note.SessionId != Session)
        {
            // A session with no note of its own yet starts one from the note it reads, so loading a group
            // does not cost it the thread it picked up.
            note = await NoteAsync(note?.Content ?? "");
        }

        var had = note!.Tags.Any(t => t.Id == group.Id);
        if (had == load)
        {
            return load ? $"{path} was loaded already." : $"{path} was not loaded.";
        }

        note.Tags = load ? [.. note.Tags, group] : note.Tags.Where(t => t.Id != group.Id).ToList();
        await fragments.SaveAsync(note);
        return load ? $"{path} is loaded for this session: the core shows it from the next one." : $"{path} is put away.";
    }

    /// <summary>One line for each group that has a head or is loaded, marked when loaded.</summary>
    public async Task<string> GroupListAsync(string? project)
    {
        var groups = await GroupsAsync();
        var loaded = (await LoadedAsync(project, groups)).ToHashSet();
        var shown = groups.Heads.Keys.Union(loaded).OrderBy(id => groups.Paths[id], StringComparer.OrdinalIgnoreCase).ToList();
        if (shown.Count == 0)
        {
            return "(no groups yet: `context set <tag/path> --content <head>` makes one)";
        }

        return string.Join('\n', shown.Select(id => $"{(loaded.Contains(id) ? "* " : "  ")}{groups.Paths[id]}"
            + (groups.Heads.TryGetValue(id, out var h) ? $" (head #{h.Id}): {FirstLine(h, 140)}" : " (no head)")));
    }

    /// <summary>A group as the core shows it, loaded or not: <c>context show</c>.</summary>
    public async Task<string> GroupShowAsync(string path)
    {
        var group = await FindTagAsync(path) ?? throw new RefusedException($"There is no tag '{path}'.");
        return await RenderGroupAsync(group.Id, await GroupsAsync(), [], int.MaxValue, loaded: false);
    }

    /// <summary>
    /// A group in the core's form: its head in full, a line for each subgroup, then a line for each member
    /// not already in front of the peer (<paramref name="shown"/>, which it adds to), within <paramref name="room"/>.
    /// </summary>
    private async Task<string> RenderGroupAsync(long id, Groups groups, HashSet<long> shown, int room, bool loaded)
    {
        var path = groups.Paths[id];
        var text = new StringBuilder($"## Group {path}"
            + (loaded ? $" (loaded; `context unload {path}` puts it away)" : "") + "\n\n");
        if (groups.Heads.TryGetValue(id, out var head))
        {
            text.Append(Render(head) + "\n\n");
            shown.Add(head.Id);
        }

        var lines = new List<string>();
        foreach (var child in groups.Parents.Where(p => p.Value == id).Select(p => p.Key).OrderBy(c => groups.Paths[c]))
        {
            var count = (await fragments.GetByTagAsync(child)).Count(f => f.Status == ContextFragmentStatus.Active);
            lines.Add($"- subgroup {groups.Paths[child]} ({count} fragments)"
                + (groups.Heads.TryGetValue(child, out var h) ? $": {FirstLine(h, 160)}" : ""));
        }

        var members = (await fragments.GetByTagAsync(id))
            .Where(f => f.Status == ContextFragmentStatus.Active && !shown.Contains(f.Id) && !groups.HeadIds.Contains(f.Id)
                && f.FragmentType is not (ContextFragmentType.WorkingNote or ContextFragmentType.ChatMessage))
            .OrderByDescending(f => f.Importance).ThenByDescending(f => f.LastModifiedUtc).ToList();
        foreach (var f in members.Take(MembersListed))
        {
            lines.Add($"- #{f.Id} ({f.FragmentType}, i:{f.Importance:0.##}): {FirstLine(f, 160)}");
            shown.Add(f.Id);
        }

        if (members.Count > MembersListed)
        {
            lines.Add($"- ({members.Count - MembersListed} more: {string.Join(", ", members.Skip(MembersListed).Select(f => $"#{f.Id}"))})");
        }

        if (lines.Count == 0 && head is null)
        {
            return "";
        }

        foreach (var line in lines)
        {
            if (text.Length + line.Length + 1 > room)
            {
                text.Append($"- (the rest did not fit; `context show {path}` lists it)\n");
                break;
            }

            text.Append(line + '\n');
        }

        return text.Append('\n').ToString();
    }

    /// <summary>The heads of the groups <paramref name="found"/> belong to (theirs or above) that are neither loaded nor already in front.</summary>
    private async Task<IReadOnlyList<ContextFragmentEntity>> FramesAsync(
        IReadOnlyList<ContextFragmentEntity> found, IReadOnlySet<long> skip, string? project, int limit = 2)
    {
        var groups = await GroupsAsync();
        if (groups.Heads.Count == 0)
        {
            return [];
        }

        var loaded = (await LoadedAsync(project, groups)).ToHashSet();
        var frames = new List<ContextFragmentEntity>();
        foreach (var tag in found.SelectMany(f => f.Tags.Select(t => (f.Id, t.Id))))
        {
            foreach (var group in groups.Lineage(tag.Item2))
            {
                if (groups.Heads.TryGetValue(group, out var head) && head.Id != tag.Item1 && !loaded.Contains(group)
                    && !skip.Contains(head.Id) && frames.All(h => h.Id != head.Id) && frames.Count < limit)
                {
                    frames.Add(head);
                }
            }
        }

        return frames;
    }

    private static string FirstLine(ContextFragmentEntity f, int max)
    {
        var line = (f.Summary is { Length: > 0 } s ? s : f.Content).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return line.Length <= max ? line : line[..max] + "…";
    }
}
