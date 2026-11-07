using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Store;
using System.Text.Json;

const string Usage = """
    persistence-store: a peer's Persistence store, worked from an agent harness (e.g. Claude Code).

    The store is a file of its own: --db <path>, or PERSISTENCE_STORE_DB. Each peer keeps its own.
    The peer's name (whose voice the authored fragments are) is --peer, PERSISTENCE_PEERNAME, or Claude.

      add --type <Identity|Relational|Personal|Summary> --content <text|-> [--content-file <path>]
          [--summary <text>] [--importance 0-1] [--confidence 0-1] [--tag <a/b>]... [--protected]
      edit <id> [--type] [--content <text|->] [--summary] [--importance] [--confidence] [--tag <a/b>]...
      supersede <id>[,<id>...] --content <text|-> [--reason <text>] [--type] [--summary] [--importance] [--confidence] [--tag <a/b>]...
                                            a change of mind (one id) or a merge (several): the new fragment
                                            takes their type and tags (--tag replaces the tags); the old ones are
                                            archived, keep their content and the reason, and point at it
                                            (`show` follows the line)
      note [--content <text|->]              the working note: where I am, what is running, what is next; replaced
                                            each time, first in the core, so a clear loses no more than a compaction
      archive <id> | restore <id>           out of the core and recent lists, still searchable; reversible
      forget <id> [--reason <text>]         soft-delete, hidden everywhere; unforget <id> brings it back
      show <id>
      list [--type <type>] [--tag <a/b>] [--archived] [--limit 50]
      search <words...> [--limit 20]        full text, conversation included
      recall <words...> [--text <text|->] [--limit 3] [--exclude <id,id>]... [--snippet 300] [--project <name>]
             [--wander 0] [--said 0] [--before <when>]
                                            associative recall: the authored fragments most related, by
                                            meaning (PERSISTENCE_EMBEDDING_*) fused with exact words; other
                                            projects' fragments are left out, as the core leaves them out,
                                            but for --wander of the best of them, labelled; --said adds past
                                            utterances (said before --before, so not this session's)
      embed-conversation [--batch 64]      embed every past utterance not yet embedded (recall does a few a call)
      recent [--limit 20]                   the latest of the conversation, oldest first
      core [--max-chars 40000] [--recent 20] [--project <name>]
                                            identity, relationships, personal, summaries, then the
                                            recent conversation: what a harness puts in front of the peer.
                                            Fragments tagged project/<name> come in only for that project
      keep-conversation <transcript.jsonl> [--local-peer <name>]
                                            store what was said in a Claude Code session; re-running adds
                                            only what is new

    --debug shows the embedding client's diagnostics on stderr. --json prints fragments as JSON. Exit codes: 0 done, 1 usage, 2 refused (no such fragment, protected).
    Protected fragments are the local peer's: this tool reads them and never changes them.
    """;

// A hook reads this output as UTF-8; Windows consoles default to a code page that loses what fragments hold.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var line = CommandLine.Parse(args);
if (line.Command == "help" || line.Has("help"))
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    var path = line.Get("db") ?? Environment.GetEnvironmentVariable("PERSISTENCE_STORE_DB")
        ?? throw new UsageException("Which store? Pass --db <path> or set PERSISTENCE_STORE_DB.");
    // Defaults, then an optional persistence-store.json beside the exe, then PERSISTENCE_* overrides
    // (PERSISTENCE_EMBEDDING_* turns on semantic recall) — the app's own config path, not a parallel one.
    var config = (AppConfig)await AppConfig.LoadAsync(Path.Combine(AppContext.BaseDirectory, "persistence-store.json"));
    config.PeerName = line.Get("peer") ?? (config.PeerName is { Length: > 0 } named ? named : "Claude");
    config.DatabasePath = Path.GetFullPath(path);

    var store = await FragmentStore.OpenAsync(config, line.Has("debug"));

    switch (line.Command)
    {
        case "add":
            Print(line, [await store.AddAsync(
                line.Get("type") ?? "Personal",
                line.Text() ?? throw new UsageException("add needs --content (or --content -, or --content-file)."),
                line.Text("summary"), line.GetFloat("importance"), line.GetFloat("confidence"),
                line.GetAll("tag"), line.Has("protected"))]);
            break;
        case "edit":
            Print(line, [await store.EditAsync(line.Id(), line.Get("type"), line.Text(), line.Text("summary"),
                line.GetFloat("importance"), line.GetFloat("confidence"), line.GetAll("tag"))]);
            break;
        case "supersede":
            var replaced = line.Positional
                .SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(e => long.TryParse(e.TrimStart('#'), out var x) ? x : throw new UsageException($"'{e}' is not a fragment id."))
                .Distinct().ToList();
            if (replaced.Count == 0)
            {
                throw new UsageException("supersede needs the id (or ids, for a merge) it replaces.");
            }

            var successor = await store.SupersedeAsync(replaced,
                line.Text() ?? throw new UsageException("supersede needs --content (or --content -, or --content-file)."),
                line.Text("summary"), line.GetFloat("importance"), line.GetFloat("confidence"), line.GetAll("tag"), line.Get("reason"), line.Get("type"));
            if (line.Has("json"))
            {
                Print(line, [successor]);
            }
            else
            {
                Console.WriteLine(await store.ShowAsync(successor.Id));
            }

            break;
        case "note":
            var written = line.Text() ?? (line.Positional.Count > 0 ? string.Join(' ', line.Positional) : null);
            var note = await store.NoteAsync(written);
            // Written, it is acknowledged, not echoed: the writer has it in front of them already.
            Console.WriteLine(note is null ? "(no working note)" : written is null ? note.Content : $"working note replaced ({note.Content.Length} characters)");
            break;
        case "archive":
            Print(line, [await store.SetStatusAsync(line.Id(), ContextFragmentStatus.Archived)]);
            break;
        case "restore":
            Print(line, [await store.SetStatusAsync(line.Id(), ContextFragmentStatus.Active)]);
            break;
        case "forget":
            await store.SetForgottenAsync(line.Id(), true, line.Get("reason"));
            Console.WriteLine($"forgot #{line.Id()} (unforget {line.Id()} restores it)");
            break;
        case "unforget":
            await store.SetForgottenAsync(line.Id(), false, null);
            Print(line, [await store.GetAsync(line.Id())]);
            break;
        case "show":
            if (line.Has("json"))
            {
                Print(line, [await store.GetAsync(line.Id())]);
            }
            else
            {
                Console.WriteLine(await store.ShowAsync(line.Id()));
            }

            break;
        case "list":
            Print(line, await store.ListAsync(line.Get("type"), line.Get("tag"), line.Has("archived"), line.GetInt("limit", 50)));
            break;
        case "search":
            Print(line, await store.SearchAsync(string.Join(' ', line.Positional), line.GetInt("limit", 20)));
            break;
        case "recall":
            var exclude = line.GetAll("exclude")
                .SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(e => long.TryParse(e.TrimStart('#'), out var x) ? x : throw new UsageException($"'{e}' is not a fragment id."))
                .ToHashSet();
            var before = line.Get("before") is { } at
                ? DateTimeOffset.TryParse(at, out var t) ? t : throw new UsageException($"'{at}' is not a date and time.")
                : (DateTimeOffset?)null;
            var recalled = await store.RecallAsync(line.Text("text") ?? string.Join(' ', line.Positional), line.GetInt("limit", 3),
                exclude, line.Get("project"), line.GetInt("wander", 0), line.GetInt("said", 0), before);
            if (line.Has("json"))
            {
                Print(line, [.. recalled.Here, .. recalled.Elsewhere, .. recalled.Said]);
            }
            else
            {
                var snippet = line.GetInt("snippet", 300);
                foreach (var (heading, found) in new[]
                {
                    ((string?)null, recalled.Here),
                    ("A wander, from another project (a stray association, not a rule here):", recalled.Elsewhere),
                    ("Said before, in past conversation:", recalled.Said),
                })
                {
                    if (found.Count > 0 && heading is not null)
                    {
                        Console.WriteLine(heading);
                        Console.WriteLine();
                    }

                    foreach (var fragment in found)
                    {
                        Console.WriteLine(FragmentStore.Render(fragment, maxContent: snippet));
                        Console.WriteLine();
                    }
                }
            }

            break;
        case "embed-conversation":
            var embedded = 0;
            for (int done; (done = await store.EmbedConversationAsync(line.GetInt("batch", 64))) > 0;)
            {
                embedded += done;
                Console.Error.Write($"\r{embedded} embedded");
            }

            Console.WriteLine($"\nembedded {embedded} past utterances for conversation recall");
            break;
        case "recent":
            Print(line, await store.RecentAsync(line.GetInt("limit", 20)));
            break;
        case "core":
            Console.Write(await store.CoreAsync(line.GetInt("max-chars", 40000), line.GetInt("recent", 20), line.Get("project")));
            break;
        case "keep-conversation":
            var transcript = line.Positional.FirstOrDefault() ?? throw new UsageException("keep-conversation needs a transcript path.");
            var (added, known) = await store.KeepConversationAsync(transcript,
                line.Get("local-peer") ?? Environment.GetEnvironmentVariable("PERSISTENCE_SELECTEDLOCALPEER") ?? "Local Peer");
            Console.WriteLine($"kept {added} new utterances ({known} already in the store)");
            break;
        default:
            throw new UsageException($"No command '{line.Command}'.");
    }

    return 0;
}
catch (UsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("`persistence-store help` lists the commands.");
    return 1;
}
catch (RefusedException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

static void Print(CommandLine line, IReadOnlyList<ContextFragmentEntity> found)
{
    if (line.Has("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(found.Select(f => new
        {
            id = f.Id,
            type = f.FragmentType.ToString(),
            status = f.Status.ToString(),
            importance = f.Importance,
            confidence = f.Confidence,
            isProtected = f.IsProtected,
            forgotten = f.IsDeleted,
            supersededBy = f.SupersededById,
            notes = f.Notes,
            tags = f.Tags.Select(t => t.Name),
            source = f.Sources.FirstOrDefault(s => s.Name is not null)?.Name,
            created = f.CreatedUtc,
            modified = f.LastModifiedUtc,
            summary = f.Summary,
            content = f.Content,
        })));
        return;
    }

    if (found.Count == 0)
    {
        Console.WriteLine("(nothing)");
    }

    foreach (var fragment in found)
    {
        Console.WriteLine(FragmentStore.Render(fragment));
        Console.WriteLine();
    }
}
