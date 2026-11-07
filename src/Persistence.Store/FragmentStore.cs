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
internal sealed class FragmentStore(
    IDatabaseManager database,
    IContextFragmentRepository fragments,
    ISourceRepository sources,
    ITagRepository tags,
    ISessionContext session,
    IMemorySurfacer recall)
{
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
        });
        var store = (FragmentStore)services.GetService(typeof(FragmentStore))!;
        await store.InitializeAsync();
        return store;
    }

    private Task InitializeAsync() => database.InitializeAsync();

    public async Task<ContextFragmentEntity> AddAsync(
        string typeName, string content, string? summary, float? importance, float? confidence,
        IReadOnlyList<string> tagPaths, bool isProtected)
    {
        var (type, authorable) = FragmentTypeRules.ParseAuthorable(typeName);
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
    /// </summary>
    public Task<IReadOnlyList<ContextFragmentEntity>> RecallAsync(string text, int count, IReadOnlyCollection<long> exclude) =>
        recall.SurfaceAsync(exclude, text, count);

    /// <summary>The last <paramref name="count"/> things said in the conversation, oldest first.</summary>
    public async Task<IReadOnlyList<ContextFragmentEntity>> RecentAsync(int count)
    {
        var ids = (await fragments.GetByTypeAsync(ContextFragmentType.ChatMessage))
            .Where(f => !f.IsDeleted)
            .OrderByDescending(f => f.CreatedUtc).Take(count).Select(f => f.Id).ToList();
        return (await HydratedAsync(ids)).OrderBy(f => f.CreatedUtc).ToList();
    }

    /// <summary>
    /// The peer's core, rendered for a harness to put in front of it: identity, relationships,
    /// personal fragments and summaries, most important first within each type, then the recent
    /// conversation. When <paramref name="maxChars"/> is reached, what did not fit is named by id
    /// rather than silently dropped, so the peer knows it is there and can <c>show</c> it.
    /// </summary>
    public async Task<string> CoreAsync(int maxChars, int recent)
    {
        var text = new StringBuilder();
        var left = new List<long>();

        foreach (var type in CoreTypes)
        {
            var ofType = (await fragments.GetByTypeAsync(type))
                .Where(f => !f.IsDeleted)
                .OrderByDescending(f => f.Importance).ThenByDescending(f => f.LastModifiedUtc)
                .Select(f => f.Id).ToList();

            foreach (var fragment in await HydratedAsync(ofType))
            {
                var rendered = Render(fragment) + "\n\n";
                if (text.Length + rendered.Length > maxChars)
                {
                    left.Add(fragment.Id);
                    continue;
                }

                text.Append(rendered);
            }
        }

        if (left.Count > 0)
        {
            text.Append($"({left.Count} more core fragments did not fit: {string.Join(", ", left.Select(i => $"#{i}"))}; `show <id>` reads one.)\n\n");
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
    public async Task<(int Added, int Known)> KeepConversationAsync(string transcriptPath, string localPeerName)
    {
        var known = (await fragments.GetByTypeAsync(ContextFragmentType.ChatMessage, activeOnly: false))
            .Select(f => f.MessageId).OfType<string>().ToHashSet();
        var localPeer = await sources.EnsureLocalPeerSourceAsync(localPeerName);
        var remotePeer = await PeerSourceAsync();
        var skipped = 0;
        var fresh = new List<ContextFragmentEntity>();

        foreach (var said in Transcript.Read(transcriptPath))
        {
            if (!known.Add(said.Id))
            {
                skipped++;
                continue;
            }

            fresh.Add(new ContextFragmentEntity
            {
                FragmentType = ContextFragmentType.ChatMessage,
                Status = ContextFragmentStatus.Active,
                Content = said.Text,
                MessageId = said.Id,
                Importance = 0.3f,
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
    public static string Render(ContextFragmentEntity f, int maxContent = int.MaxValue)
    {
        var flags = new List<string>();
        if (f.IsProtected) flags.Add("protected");
        if (f.Status == ContextFragmentStatus.Archived) flags.Add("archived");
        if (f.IsDeleted) flags.Add("forgotten");
        if (f.Tags.Count > 0) flags.Add("tags: " + string.Join(", ", f.Tags.Select(t => t.Name)));

        var header = $"[#{f.Id} | {f.FragmentType} | i:{f.Importance:0.##} c:{f.Confidence:0.##}{(flags.Count > 0 ? " | " + string.Join(" | ", flags) : "")}]";
        if (f.FragmentType == ContextFragmentType.ChatMessage)
        {
            var who = f.Sources.FirstOrDefault(s => s.Name is not null)?.Name ?? "?";
            header += $" {who}, {f.CreatedUtc.ToUniversalTime():yyyy-MM-dd HH:mm}Z:";
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
