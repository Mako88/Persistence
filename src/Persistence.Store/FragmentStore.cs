using Autofac;
using Persistence.Config;
using Persistence.Data;
using Persistence.DI;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Services;
using System.Text;

namespace Persistence.Store;

/// <summary>
/// A peer's store, worked from outside the turn loop: the operations an agent harness (Claude Code)
/// needs to keep its own continuity in Persistence — author and curate fragments, search them, render
/// the core of who the peer is, and keep the conversation it is having. Every write goes through the
/// repositories, so it is audited and reversible exactly as a turn's commands are.
/// </summary>
internal sealed partial class FragmentStore(
    IDatabaseManager database,
    IContextFragmentRepository fragments,
    ISourceRepository sources,
    ITagRepository tags,
    ISessionContext session,
    IMemorySurfacer recall,
    ConversationRecaller conversation,
    IFragmentEmbeddingRepository vectors,
    IEmbeddingClient embeddings)
{
    /// <summary>The parent tag of the projects: a fragment tagged <c>project/&lt;name&gt;</c> belongs to that project.</summary>
    public const string ProjectTag = "project";

    /// <summary>The types that make up the peer's core, in the order they are rendered.</summary>
    public static readonly IReadOnlyList<ContextFragmentType> CoreTypes =
    [
        ContextFragmentType.Identity,
        ContextFragmentType.Relational,
        ContextFragmentType.Personal,
        ContextFragmentType.Summary,
    ];

    /// <summary>
    /// Opens the store <paramref name="config"/> names: wires the repositories as the app does, migrates
    /// the file (creating it if new), and loads the source ids every write is attributed to.
    /// </summary>
    public static async Task<FragmentStore> OpenAsync(IAppConfig config, bool debug = false)
    {
        var services = IoC.RegisterServices(builder =>
        {
            builder.RegisterInstance(config).As<IAppConfig>().SingleInstance();
            builder.RegisterInstance(new QuietDisplay(debug)).As<IDisplayProvider>().SingleInstance();
            builder.RegisterType<FragmentStore>().SingleInstance();
            builder.RegisterType<ConversationRecaller>().SingleInstance();
            builder.RegisterDecorator<RememberingEmbeddingClient, IEmbeddingClient>();
        });
        var store = (FragmentStore)services.GetService(typeof(FragmentStore))!;
        await store.InitializeAsync();
        return store;
    }

    private Task InitializeAsync() => database.InitializeAsync();

    /// <summary>
    /// The harness session this call is made in (a Claude Code session id), or null outside one. Set for
    /// each command: from <c>--session</c>, else, run directly from a session's shell, from its environment
    /// (<see cref="AmbientSession"/>). A server holds no ambient session: it serves every session at once.
    /// </summary>
    public string? Session { get; set; }

    /// <summary>
    /// The project of the process itself (the repository its folder is in), asked only when needed: a
    /// server serves every project, so it has none and its callers say (--project).
    /// </summary>
    public Func<string?>? AmbientProject { get; set; }

    /// <summary>The session of the process itself (CLAUDE_CODE_SESSION_ID), when not serving.</summary>
    public string? AmbientSession { get; set; }

    /// <summary>
    /// How recently another session must have spoken to count as running: its conversation is kept out of
    /// this one's and shown as a line instead. Past it, the session has ended and its words are history.
    /// </summary>
    public static readonly TimeSpan Live = TimeSpan.FromHours(3);

    public async Task<ContextFragmentEntity> AddAsync(
        string typeName, string content, string? summary, float? importance, float? confidence,
        IReadOnlyList<string> tagPaths, bool isProtected, string? project = null)
    {
        var (type, authorable) = FragmentTypeRules.ParseAuthorable(typeName);
        // A personal fragment is about the work in hand far more often than about everything: unscoped, the
        // details of one project crowded every other project's core. It takes the project it was written in
        // unless it names one (or --everywhere passes null here); the writer sees the tag and can change it.
        if (type == ContextFragmentType.Personal && project is { Length: > 0 }
            && !tagPaths.Any(p => p.StartsWith(ProjectTag + "/", StringComparison.OrdinalIgnoreCase)))
        {
            tagPaths = [.. tagPaths, $"{ProjectTag}/{project}"];
        }

        if (!authorable)
        {
            throw new UsageException(
                $"'{typeName}' is not a type a peer authors; use one of {string.Join(", ", FragmentTypeRules.Authorable)}.");
        }

        var fragment = new ContextFragmentEntity
        {
            FragmentType = type,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Summary = summary,
            Importance = importance ?? 0.5f,
            Confidence = confidence ?? 0.5f,
            IsProtected = isProtected,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SessionId = Session,
            Sources = [await PeerSourceAsync()],
            Tags = await TagsAsync(tagPaths),
        };

        await fragments.SaveAsync(fragment);
        return fragment;
    }

    public async Task<ContextFragmentEntity> EditAsync(
        long id, string? typeName, string? content, string? summary, float? importance, float? confidence,
        IReadOnlyList<string> addTags)
    {
        var fragment = await WritableAsync(id);

        if (typeName is not null)
        {
            var (type, authorable) = FragmentTypeRules.ParseAuthorable(typeName);
            if (!authorable)
            {
                throw new UsageException($"'{typeName}' is not a type a peer authors.");
            }

            fragment.FragmentType = type;
        }

        fragment.Content = content ?? fragment.Content;
        fragment.Summary = summary ?? fragment.Summary;
        fragment.Importance = importance ?? fragment.Importance;
        fragment.Confidence = confidence ?? fragment.Confidence;
        foreach (var tag in await TagsAsync(addTags))
        {
            if (fragment.Tags.All(t => t.Id != tag.Id))
            {
                fragment.Tags.Add(tag);
            }
        }

        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
        await fragments.SaveAsync(fragment);
        return fragment;
    }

    /// <summary>
    /// Replaces <paramref name="ids"/> with one new fragment: a change of mind (one id) or a merge (several).
    /// The new one takes the type of the first, every tag the old ones had, and their highest importance,
    /// unless told otherwise; <paramref name="tagPaths"/>, when given, replace the tags rather than add to
    /// them, since a merge's view of what it is about is the new one's to say. The old ones keep their
    /// content, are archived, and point at it, with <paramref name="reason"/> kept on them so the change
    /// can be read later.
    /// </summary>
    public async Task<ContextFragmentEntity> SupersedeAsync(
        IReadOnlyList<long> ids, string content, string? summary, float? importance, float? confidence,
        IReadOnlyList<string> tagPaths, string? reason, string? typeName = null)
    {
        var type = typeName is null ? (ContextFragmentType?)null : FragmentTypeRules.ParseAuthorable(typeName) switch
        {
            (var t, true) => t,
            _ => throw new UsageException($"'{typeName}' is not a type a peer authors."),
        };

        var old = new List<ContextFragmentEntity>();
        foreach (var id in ids)
        {
            var fragment = await WritableAsync(id);
            if (fragment.SupersededById is { } by)
            {
                // Two sessions changing the same view: the second learns of the first here, when it writes,
                // not at a cleanup afterwards.
                var other = (await fragments.GetByIdsAsync([by])).FirstOrDefault()?.SessionId;
                var where = other is not null && other != Session ? $", in another session ({Short(other)})" : "";
                throw new RefusedException($"#{id} was already superseded by #{by}{where}; read it (`show {by}`) and supersede that one instead.");
            }

            if (!FragmentTypeRules.ParseAuthorable(fragment.FragmentType.ToString()).wasAuthorable)
            {
                throw new UsageException($"#{id} is a {fragment.FragmentType}, which a peer does not author.");
            }

            old.Add(fragment);
        }

        var successor = new ContextFragmentEntity
        {
            FragmentType = type ?? old[0].FragmentType,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Summary = summary,
            Importance = importance ?? old.Max(f => f.Importance),
            Confidence = confidence ?? old[0].Confidence,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SessionId = Session,
            Sources = [await PeerSourceAsync()],
            Tags = tagPaths.Count > 0 ? await TagsAsync(tagPaths) : old.SelectMany(f => f.Tags).DistinctBy(t => t.Id).ToList(),
        };
        await fragments.SaveAsync(successor);

        foreach (var fragment in old)
        {
            fragment.SupersededById = successor.Id;
            fragment.Status = ContextFragmentStatus.Archived;
            fragment.Notes = string.IsNullOrWhiteSpace(reason) ? fragment.Notes : reason.Trim();
            fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
            await fragments.SaveAsync(fragment);
        }

        return successor;
    }

    /// <summary>
    /// A fragment with where it stands in its line of views: what it replaced (with why), and what
    /// replaced it, so an old view found by search leads to the current one.
    /// </summary>
    public async Task<string> ShowAsync(long id)
    {
        var fragment = await GetAsync(id);
        var text = new StringBuilder(Render(fragment));
        if (fragment.SupersededById is { } by)
        {
            text.Append($"\n(Superseded by #{by}{(fragment.Notes is { Length: > 0 } why ? $": {why}" : "")}. `show {by}` reads the current view.)");
        }

        foreach (var earlier in (await fragments.GetSupersededByAsync(id)).OrderBy(f => f.CreatedUtc))
        {
            text.Append($"\n(Supersedes #{earlier.Id}, from {earlier.CreatedUtc.ToUniversalTime():yyyy-MM-dd}"
                + $"{(earlier.Notes is { Length: > 0 } why ? $", because: {why}" : "")}.)");
        }

        return text.ToString();
    }

    /// <summary>
    /// The peer's working note: one "where I am, what is running, what is next" fragment, replaced each
    /// time, first in the core. What a summary would hand over at a compaction, kept by the peer as it
    /// works, so a clear (which hands over nothing) loses no more than a compaction does. Null text reads it.
    /// </summary>
    public async Task<ContextFragmentEntity?> NoteAsync(string? text)
    {
        // One note per session: two sessions at once each keep their own place. A session with none yet
        // (a new one after a clear) reads the latest note of a session that has ended, so the thread is
        // picked up; a running session's note is its own, shown to the others as a line.
        var notes = (await fragments.GetByTypeAsync(ContextFragmentType.WorkingNote)).Where(f => !f.IsDeleted)
            .OrderByDescending(f => f.LastModifiedUtc).ToList();
        var own = Session is null ? notes.FirstOrDefault() : notes.FirstOrDefault(f => f.SessionId == Session);
        if (text is null)
        {
            if (own is not null || Session is null)
            {
                return own;
            }

            var live = (await LiveSessionsAsync()).Keys.ToHashSet();
            return notes.FirstOrDefault(f => f.SessionId is null || !live.Contains(f.SessionId));
        }

        var note = own ?? new ContextFragmentEntity
        {
            FragmentType = ContextFragmentType.WorkingNote,
            Status = ContextFragmentStatus.Active,
            Content = "",
            Importance = 1f,
            Confidence = 1f,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SessionId = Session,
            Sources = [await PeerSourceAsync()],
        };
        note.Content = text;
        note.LastModifiedUtc = DateTimeOffset.UtcNow;
        await fragments.SaveAsync(note);
        return note;
    }

    /// <summary>Archives a fragment (out of the core and the recent lists, still searchable) or restores it.</summary>
    public async Task<ContextFragmentEntity> SetStatusAsync(long id, ContextFragmentStatus status)
    {
        var fragment = await WritableAsync(id);
        fragment.Status = status;
        fragment.LastModifiedUtc = DateTimeOffset.UtcNow;
        await fragments.SaveAsync(fragment);
        return fragment;
    }

    /// <summary>Soft-deletes (<c>forget</c>) or restores (<c>unforget</c>) a fragment; nothing is erased.</summary>
    public async Task SetForgottenAsync(long id, bool forgotten, string? reason)
    {
        if (forgotten)
        {
            await WritableAsync(id);
        }

        await fragments.SetDeletedAsync(id, forgotten, reason);
    }

    public async Task<ContextFragmentEntity> GetAsync(long id) =>
        await fragments.GetByIdAsync(id) ?? throw new RefusedException($"There is no fragment #{id}.");

    public async Task<IReadOnlyList<ContextFragmentEntity>> ListAsync(string? typeName, string? tagPath, bool archived, int limit)
    {
        IEnumerable<ContextFragmentEntity> found;

        if (tagPath is not null)
        {
            var tag = await FindTagAsync(tagPath) ?? throw new RefusedException($"There is no tag '{tagPath}'.");
            found = await fragments.GetByTagAsync(tag.Id);
        }
        else
        {
            var types = typeName is null
                ? CoreTypes
                : [Enum.TryParse<ContextFragmentType>(typeName, true, out var t) ? t : throw new UsageException($"No fragment type '{typeName}'.")];
            var all = new List<ContextFragmentEntity>();
            foreach (var type in types)
            {
                all.AddRange(await fragments.GetByTypeAsync(type, activeOnly: !archived));
            }

            found = all;
        }

        var wanted = archived ? ContextFragmentStatus.Archived : ContextFragmentStatus.Active;
        var ids = found.Where(f => !f.IsDeleted && f.Status == wanted)
            .OrderByDescending(f => f.Importance).ThenByDescending(f => f.LastModifiedUtc)
            .Take(limit).Select(f => f.Id).ToList();
        return await HydratedAsync(ids);
    }

    /// <summary>Full-text search over every fragment the store keeps, conversation included.</summary>
    public async Task<IReadOnlyList<ContextFragmentEntity>> SearchAsync(string query, int limit)
    {
        // Each word quoted, so a question with punctuation searches its words instead of failing as FTS syntax.
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => $"\"{w.Replace("\"", "")}\"").Where(w => w.Length > 2);
        var match = string.Join(" OR ", terms);
        return match.Length == 0 ? [] : (await fragments.SearchRelevantAsync(match, limit)).ToList();
    }

    /// <summary>
    /// Persistence's associative recall: the authored fragments most related to <paramref name="text"/>,
    /// by meaning (embeddings, when configured) fused with exact words (BM25), weighted by importance
    /// and confidence, leaving out <paramref name="exclude"/> (what is already in front of the peer).
    /// A fragment tagged for another project than <paramref name="project"/> is left out, as the core leaves it out,
    /// except for up to <paramref name="wander"/> of them that rank among the best overall, returned apart: a memory from elsewhere is
    /// where a new idea can come from, and labelled as such it reads as a stray thought rather than a rule here.
    /// <paramref name="said"/> past utterances come with them (<see cref="ConversationRecaller"/>).
    /// </summary>
    public async Task<Recalled> RecallAsync(
        string text, int count, IReadOnlyCollection<long> exclude, string? project = null,
        int wander = 0, int said = 0, DateTimeOffset? before = null)
    {
        var projects = (await FindTagAsync(ProjectTag))?.Id;
        var skip = exclude.ToHashSet();
        var kept = new List<ContextFragmentEntity>();
        var elsewhere = new List<ContextFragmentEntity>();

        // Asks for a few more than wanted so that dropping other projects' fragments still leaves enough.
        for (var round = 0; round < 3 && kept.Count < count; round++)
        {
            var found = await HydratedAsync((await recall.SurfaceAsync(skip, text, count * 3)).Select(f => f.Id).ToList());
            if (found.Count == 0)
            {
                break;
            }

            for (var rank = 0; rank < found.Count; rank++)
            {
                var fragment = found[rank];
                skip.Add(fragment.Id);
                if (ProjectsOf(fragment, projects).All(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase)))
                {
                    if (kept.Count < count)
                    {
                        kept.Add(fragment);
                    }
                }
                else if (elsewhere.Count < wander && round == 0 && rank < count)
                {
                    // Only one that ranks among the best overall: another project's best match, taken from
                    // however far down, came with every message and was never worth the thought.
                    elsewhere.Add(fragment);
                }
            }
        }

        var utterances = await conversation.RecallAsync(text, said, skip, before, Session);
        return new Recalled(kept, elsewhere, utterances, await FramesAsync(kept, skip, project));
    }

    /// <summary>Embeds up to <paramref name="limit"/> past utterances that have none yet, for conversation recall.</summary>
    public Task<int> EmbedConversationAsync(int limit) => conversation.BackfillAsync(limit);

    /// <summary>What a recall brought back: this project's fragments, a wander from elsewhere, and things said before.</summary>
    public sealed record Recalled(
        IReadOnlyList<ContextFragmentEntity> Here,
        IReadOnlyList<ContextFragmentEntity> Elsewhere,
        IReadOnlyList<ContextFragmentEntity> Said,
        IReadOnlyList<ContextFragmentEntity> Frames);

    /// <summary>The projects a fragment is tagged for (<c>project/&lt;name&gt;</c>); none means it belongs everywhere.</summary>
    private static List<string> ProjectsOf(ContextFragmentEntity fragment, long? projects) =>
        fragment.Tags.Where(t => projects is not null && t.ParentTagId == projects).Select(t => t.Name).ToList();

    /// <summary>The last <paramref name="count"/> things said in the conversation, oldest first.</summary>
    public async Task<IReadOnlyList<ContextFragmentEntity>> RecentAsync(int count)
    {
        // Another session still running is kept out: interleaved, its "tests pass, publishing" read as this
        // session's own work. It is shown as a line in the core instead, and joins the history when it ends.
        var running = (await LiveSessionsAsync()).Keys.ToList();
        var ids = await fragments.GetNewestIdsAsync(ContextFragmentType.ChatMessage, count, running);
        return (await HydratedAsync(ids)).OrderBy(f => f.CreatedUtc).ToList();
    }

    /// <summary>
    /// The other sessions running now (one that spoke within <see cref="Live"/>), each with when it last
    /// spoke. This session is never among them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LiveSessionsAsync()
    {
        if (Session is null)
        {
            return new Dictionary<string, DateTimeOffset>();
        }

        return (await fragments.GetSessionsSinceAsync(ContextFragmentType.ChatMessage, DateTimeOffset.UtcNow - Live))
            .Where(s => s.Key != Session).ToDictionary(s => s.Key, s => s.Value);
    }

    /// <summary>
    /// One line for each other session running now: its working note's opening if it keeps one, else what
    /// the human last asked of it and how it last answered. Enough to know what it is doing and not to
    /// redo it or step on its files; its whole conversation is a <c>search</c> away.
    /// </summary>
    public async Task<IReadOnlyList<string>> OtherSessionsAsync()
    {
        var live = await LiveSessionsAsync();
        if (live.Count == 0)
        {
            return [];
        }

        var notes = (await fragments.GetByTypeAsync(ContextFragmentType.WorkingNote)).Where(f => !f.IsDeleted && f.SessionId is not null)
            .GroupBy(f => f.SessionId!).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.LastModifiedUtc).First());
        var said = await fragments.GetIdsBySessionsAsync(ContextFragmentType.ChatMessage, live.Keys.ToList());
        var bySession = (await HydratedAsync(said)).GroupBy(f => f.SessionId!).ToDictionary(g => g.Key, g => g.OrderBy(f => f.CreatedUtc).ToList());

        static string Clip(string s, int n)
        {
            s = string.Join(' ', s.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return s.Length <= n ? s : s[..n] + "…";
        }

        var lines = new List<string>();
        foreach (var (id, last) in live.OrderByDescending(x => x.Value))
        {
            var head = $"Session {Short(id)}, last spoke {last.ToUniversalTime():HH:mm}Z";
            if (notes.TryGetValue(id, out var note) && note.Content.Length > 0)
            {
                lines.Add($"{head}; its note: {Clip(note.Content, 400)}");
                continue;
            }

            var words = bySession.GetValueOrDefault(id) ?? [];
            static bool ByHuman(ContextFragmentEntity f) => f.Sources.Any(s => s.SourceType == SourceType.HumanPeer);
            var asked = words.LastOrDefault(ByHuman);
            var answered = words.LastOrDefault(f => !ByHuman(f));
            lines.Add($"{head}; asked: {(asked is null ? "(nothing yet)" : Clip(asked.Content, 200))}"
                + (answered is null ? "" : $" Its last reply: {Clip(answered.Content, 300)}"));
        }

        return lines;
    }

    /// <summary>A session's id cut to the eight characters that tell sessions apart in a line.</summary>
    public static string Short(string session) => session.Length > 8 ? session[..8] : session;

    /// <summary>
    /// The peer's core, rendered for a harness to put in front of it: identity, relationships,
    /// personal fragments and summaries, most important first within each type, then the recent
    /// conversation. When <paramref name="maxChars"/> is reached, what did not fit is named by id
    /// rather than silently dropped, so the peer knows it is there and can <c>show</c> it.
    /// </summary>
    public async Task<string> CoreAsync(int maxChars, int recent, string? project = null)
    {
        var text = new StringBuilder();
        var left = new List<long>();
        var elsewhere = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = (await FindTagAsync(ProjectTag))?.Id;
        var groups = await GroupsAsync();
        var shown = new HashSet<long>();
        var loaded = await LoadedAsync(project, groups);
        // A loaded group is what was asked to be held in mind, so it keeps room the core types cannot take.
        var groupRoom = loaded.Count == 0 ? 0 : Math.Min(maxChars / 4, 4000 * loaded.Count);

        if (await NoteAsync(null) is { Content.Length: > 0 } note)
        {
            var whose = Session is null || note.SessionId == Session ? "my working note" : "the working note of an ended session, until I write my own";
            text.Append($"## Where I am ({whose}, written {note.LastModifiedUtc.ToUniversalTime():yyyy-MM-dd HH:mm}Z; "
                + "`note` replaces it)\n\n" + note.Content.Trim() + "\n\n");
        }

        if (await OtherSessionsAsync() is { Count: > 0 } others)
        {
            text.Append("## Other sessions of mine running now (their conversations are kept out of mine below; "
                + "fragments are shared. Before editing files, check none of them is working in the same ones)\n\n");
            foreach (var line in others)
            {
                text.Append($"- {line}\n");
            }

            text.Append('\n');
        }

        foreach (var type in CoreTypes)
        {
            var ofType = (await fragments.GetByTypeAsync(type))
                .Where(f => !f.IsDeleted)
                .OrderByDescending(f => f.Importance).ThenByDescending(f => f.LastModifiedUtc)
                .Select(f => f.Id).ToList();

            foreach (var fragment in await HydratedAsync(ofType))
            {
                // A group's head comes in with its group, loaded, or beside a member recall brings.
                if (groups.HeadIds.Contains(fragment.Id))
                {
                    continue;
                }

                // A fragment tagged project/<name> is in the core only while that project is the one in
                // hand; the rest stay a `list --tag` or a recall away.
                var own = ProjectsOf(fragment, projects);
                if (own.Count > 0 && !own.Contains(project ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    elsewhere.UnionWith(own);
                    continue;
                }

                var rendered = Render(fragment) + "\n\n";
                if (text.Length + rendered.Length > maxChars - groupRoom)
                {
                    left.Add(fragment.Id);
                    continue;
                }

                text.Append(rendered);
                shown.Add(fragment.Id);
            }
        }

        foreach (var group in loaded)
        {
            text.Append(await RenderGroupAsync(group, groups, shown, maxChars - text.Length, loaded: true));
        }

        if (groups.Heads.Keys.Where(g => !loaded.Contains(g)).Select(g => groups.Paths[g]).Order().ToList() is { Count: > 0 } put)
        {
            text.Append($"(Groups not loaded: {string.Join(", ", put)}; `context load <path>` brings one in, `context show <path>` reads it.)\n\n");
        }

        if (left.Count > 0)
        {
            text.Append($"({left.Count} more core fragments did not fit: {string.Join(", ", left.Select(i => $"#{i}"))}; `show <id>` reads one.)\n\n");
        }

        if (elsewhere.Count > 0)
        {
            text.Append($"(Other projects have fragments of their own: {string.Join(", ", elsewhere)}; `core --project <name>` "
                + $"or `list --tag {ProjectTag}/<name>` brings one in.)\n\n");
        }

        // The conversation gets what the core leaves: the human's words and the replies that ended the
        // peer's turns, not the narration in between. Newest kept first; when the room runs out the
        // utterance that did not fit is cut with its id rather than dropped, so the newest always shows
        // and the whole of it is one `show` away.
        var conversation = recent > 0 ? Exchanges(await RecentAsync(recent * 8)).TakeLast(recent).ToList() : [];
        if (conversation.Count > 0)
        {
            const string heading = "## The conversation so far (the most recent of it)\n\n";
            var room = maxChars - text.Length - heading.Length - 200;
            var kept = new List<string>();
            foreach (var fragment in Enumerable.Reverse(conversation))
            {
                var rendered = Render(fragment, maxContent: 2500) + "\n\n";
                if (rendered.Length > room)
                {
                    var header = Render(fragment, maxContent: 0).Length + 60;
                    if (room - header >= 300)
                    {
                        kept.Insert(0, Render(fragment, maxContent: room - header) + "\n\n");
                    }

                    break;
                }

                kept.Insert(0, rendered);
                room -= rendered.Length;
            }

            text.Append(heading);
            if (kept.Count < conversation.Count)
            {
                text.Append($"({conversation.Count - kept.Count} earlier exchanges did not fit; `recent` or `search` reads them.)\n\n");
            }

            foreach (var rendered in kept)
            {
                text.Append(rendered);
            }
        }

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The exchanges in a stretch of conversation: everything the human said, and of the peer's
    /// utterances only the last before the human spoke again (or the very last), the reply that ended
    /// its turn. What it said while working stays in the store, for <c>recent</c> and <c>search</c>.
    /// </summary>
    private static IEnumerable<ContextFragmentEntity> Exchanges(IReadOnlyList<ContextFragmentEntity> said)
    {
        static bool ByHuman(ContextFragmentEntity f) => f.Sources.Any(s => s.SourceType == SourceType.HumanPeer);

        for (var i = 0; i < said.Count; i++)
        {
            if (ByHuman(said[i]) || i == said.Count - 1 || ByHuman(said[i + 1]))
            {
                yield return said[i];
            }
        }
    }

    /// <summary>
    /// Keeps the conversation of a Claude Code session in the store: each thing said becomes a
    /// ChatMessage fragment, attributed to the local peer by name or to the remote peer, dated when
    /// it was said. Its transcript id is the message id, so re-reading a transcript adds only what is new.
    /// </summary>
    /// <summary>
    /// Scores the salience of every utterance still at the old flat importance (stored before
    /// <see cref="Salience"/>); one that has been set to anything else is left as it is. Returns how many changed.
    /// </summary>
    public async Task<int> RescoreConversationAsync()
    {
        var changed = 0;
        var ids = (await fragments.GetByTypeAsync(ContextFragmentType.ChatMessage, activeOnly: false)).Select(f => f.Id).ToList();
        foreach (var f in await HydratedAsync(ids))  // with their sources: whose words they are counts
        {
            if (Math.Abs(f.Importance - Salience.Default) > 1e-6)
            {
                continue;
            }

            var score = Salience.Score(f.Content, f.Sources.Any(x => x.SourceType == SourceType.HumanPeer));
            if (Math.Abs(score - f.Importance) > 1e-6)
            {
                f.Importance = score;
                await fragments.SaveAsync(f);
                changed++;
            }
        }

        return changed;
    }

    public Task<(int Added, int Known)> KeepConversationAsync(string transcriptPath, string localPeerName) =>
        KeepConversationAsync(transcriptPath, localPeerName, null);

    /// <param name="transcriptPath">The transcript, named for its session (<c>&lt;id&gt;.jsonl</c>).</param>
    /// <param name="content">Its lines, when the file is on another machine: the path then only names the session.</param>
    public async Task<(int Added, int Known)> KeepConversationAsync(string transcriptPath, string localPeerName, TextReader? content)
    {
        // The transcript is named for its session (<id>.jsonl).
        var sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        // Ids, not fragments: loading every utterance to learn which were kept cost seconds on each keep.
        var stored = await fragments.GetMessageIdsAsync(ContextFragmentType.ChatMessage);
        var known = stored.Select(f => f.MessageId).ToHashSet();
        var unsessioned = stored.Where(f => f.SessionId is null).GroupBy(f => f.MessageId).ToDictionary(g => g.Key, g => g.First().Id);
        var localPeer = await sources.EnsureLocalPeerSourceAsync(localPeerName);
        var remotePeer = await PeerSourceAsync();
        var skipped = 0;
        var fresh = new List<ContextFragmentEntity>();

        foreach (var said in content is null ? Transcript.Read(transcriptPath) : Transcript.Read(content))
        {
            if (!known.Add(said.Id))
            {
                // Kept before sessions were: re-reading its transcript tells which session said it.
                if (unsessioned.Remove(said.Id, out var oldId) && (await fragments.GetByIdsAsync([oldId])).FirstOrDefault() is { } old)
                {
                    old.SessionId = sessionId;
                    await fragments.SaveAsync(old);
                }

                skipped++;
                continue;
            }

            fresh.Add(new ContextFragmentEntity
            {
                FragmentType = ContextFragmentType.ChatMessage,
                Status = ContextFragmentStatus.Active,
                Content = said.Text,
                MessageId = said.Id,
                SessionId = sessionId,
                Importance = Salience.Score(said.Text, said.ByLocalPeer),
                Confidence = 1f,
                CreatedUtc = said.At,
                LastModifiedUtc = said.At,
                Sources = [said.ByLocalPeer
                    ? new SourceEntity { Id = localPeer, SourceType = SourceType.HumanPeer, Name = localPeerName, CreatedUtc = said.At, LastModifiedUtc = said.At }
                    : remotePeer],
            });
        }

        foreach (var fragment in fresh)
        {
            await fragments.SaveAsync(fragment);
        }

        return (fresh.Count, skipped);
    }

    /// <summary>
    /// A fragment in Persistence's prompt form: <c>[#id | Type | i: c: | flags]</c>, a byline for
    /// conversation, then the content — cut at <paramref name="maxContent"/> characters with the id
    /// to read the rest by, when a budget asks for it.
    /// </summary>
    /// <summary>
    /// Each tag's whole path, by id, as of the command in hand (<see cref="RefreshTagPathsAsync"/>): a fragment
    /// shows <c>work/receiving/db</c>, where its leaf alone (<c>db</c>) could be any group's.
    /// </summary>
    private static IReadOnlyDictionary<long, string> TagPaths = new Dictionary<long, string>();

    /// <summary>Reads the tags' paths afresh, before a command renders anything.</summary>
    public async Task RefreshTagPathsAsync() => TagPaths = (await GroupsAsync(heads: false)).Paths;

    public static string Render(ContextFragmentEntity f, int maxContent = int.MaxValue, string? self = null)
    {
        var flags = new List<string>();
        if (self is not null && f.SessionId is not null && f.SessionId != self) flags.Add($"session {Short(f.SessionId)}");
        if (f.IsProtected) flags.Add("protected");
        if (f.SupersededById is { } by) flags.Add($"superseded by #{by}");
        else if (f.Status == ContextFragmentStatus.Archived) flags.Add("archived");
        if (f.IsDeleted) flags.Add("forgotten");
        if (f.Tags.Count > 0) flags.Add("tags: " + string.Join(", ", f.Tags.Select(t => TagPaths.GetValueOrDefault(t.Id, t.Name))));

        var header = $"[#{f.Id} | {f.FragmentType} | i:{f.Importance:0.##} c:{f.Confidence:0.##}{(flags.Count > 0 ? " | " + string.Join(" | ", flags) : "")}]";
        if (f.FragmentType == ContextFragmentType.ChatMessage)
        {
            var who = f.Sources.FirstOrDefault(s => s.Name is not null)?.Name ?? "?";
            header += $" {who}, {f.CreatedUtc.ToUniversalTime():yyyy-MM-dd HH:mm}Z:";
        }
        else
        {
            // When a view was written, and last changed, is what tells a current belief from an old one.
            var written = f.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd");
            var changed = f.LastModifiedUtc.ToUniversalTime().ToString("yyyy-MM-dd");
            header = header[..^1] + $" | {written}{(changed != written ? $", changed {changed}" : "")}]";
        }

        var content = f.Content.Length <= maxContent
            ? f.Content
            : f.Content[..maxContent] + $"… [#{f.Id} continues: `show {f.Id}`]";
        return $"{header}\n{content}";
    }

    private async Task<ContextFragmentEntity> WritableAsync(long id)
    {
        var fragment = await GetAsync(id);
        return fragment.IsProtected
            ? throw new RefusedException($"#{id} is protected: the local peer set it, and only they change it.")
            : fragment;
    }

    private async Task<SourceEntity> PeerSourceAsync() =>
        (await sources.GetAllAsync()).First(s => s.Id == session.RemotePeerSourceId);

    private async Task<List<TagEntity>> TagsAsync(IReadOnlyList<string> paths)
    {
        var found = new List<TagEntity>();
        foreach (var path in paths)
        {
            var (tag, _) = await tags.GetOrCreateByPathAsync(path);
            if (tag is not null)
            {
                found.Add(tag);
            }
        }

        return found;
    }

    private async Task<TagEntity?> FindTagAsync(string path)
    {
        TagEntity? tag = null;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tag = await tags.GetByNameAsync(name, tag?.Id);
            if (tag is null)
            {
                return null;
            }
        }

        return tag;
    }

    /// <summary>Loads fragments with their sources and tags, in the order of <paramref name="ids"/>.</summary>
    private async Task<IReadOnlyList<ContextFragmentEntity>> HydratedAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var byId = (await fragments.GetByIdsAsync(ids)).ToDictionary(f => f.Id);
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }
}
