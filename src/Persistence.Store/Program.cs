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
          [--project <name> | --everywhere]  a Personal fragment takes project/<the repository it is written
                                            in> unless it is tagged project/<name> already or --everywhere
      edit <id> [--type] [--content <text|->] [--summary] [--importance] [--confidence] [--tag <a/b>]... [--untag <a/b>]...
      supersede <id>[,<id>...] --content <text|-> [--reason <text>] [--type] [--summary] [--importance] [--confidence] [--tag <a/b>]...
      split <id> --content <part> --content <part>... [--reason <text>]   one catch-all into several, each part a fragment
                                            a change of mind (one id) or a merge (several): the new fragment
                                            takes their type and tags (--tag replaces the tags); the old ones are
                                            archived, keep their content and the reason, and point at it
                                            (`show` follows the line)
      note [--content <text|->]              the working note: where I am, what is running, what is next; replaced
                                            each time, first in the core, so a clear loses no more than a compaction
      context [list] | show <tag/path> | set <tag/path> --content <head> [--summary] | load <tag/path> | unload <tag/path>
                                            groups: a tag is a group, its child tags its subgroups; its head (a
                                            fragment tagged with it and `head`) is what to hold in mind about it.
                                            A loaded group is in the core (head, subgroups, a line per member):
                                            the project's when it has a head, and those loaded for the session.
                                            Recall brings an unloaded group's head beside a member it surfaces
      cue [list] | set <moment>/<name> --when <pattern> [--content <text|-> [--type] [--importance] [--project <name>]] | remove <moment>/<name>
                                            a cue brings its fragments at a moment, not by association: prompt
                                            (the human's words), tool (a call about to run, as `Tool: {input}`),
                                            stop (my last message as a turn ends). --content adds a fragment to it;
                                            `edit <id> --tag cue/<moment>/<name>` puts an existing one on it
      nearest <words...> [--text <text|->] [--limit 3]
                                            the authored fragments nearest a text, with their scores: what `add`
                                            checks for a view written twice (it names a near one after saving)
      cued <moment> --text <text|-> [--exclude <id,id>] [--snippet 600]
                                            the fragments of the cues the text matches, as the hooks show them
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
      sleep-list [--over 1500] [--limit 25] [--sample 8]
                                            a sleep pass's worklist: oversized fragments, near-duplicate pairs,
                                            older ones at random, what was said since the last pass, and the
                                            ids in scope (to join with use counts)
      sleep-begin [--propose] [--max 12] [--hours 4] | sleep-status | sleep-end --content <the record>
                                            a sleep pass (docs/design/sleep-pass.md): while open, no forget,
                                            archive, note or edit in place; only fragments settled 7 days, never
                                            Identity; at most --max changes, each logged in the pass's record;
                                            --propose changes nothing and writes every change as a proposal
      whoami                                whose store this is, and its file
      guide                                 how to curate your own store: what to keep, how to write it, the core
      core [--max-chars 40000] [--recent 20] [--project <name>] [--compacted <time>]...
                                            identity, relationships, personal, summaries, then the
                                            recent conversation: what a harness puts in front of the peer.
                                            Fragments tagged project/<name> come in only for that project.
                                            --compacted (repeatable) gives the session's compaction times:
                                            what it kept since the latest leads its section, by importance.
                                            --ended "<session>|<time>" (repeatable, on any command): sessions the
                                            harness saw end; one silent since is not listed as running
      rescore-conversation                 score the salience of utterances stored at the old flat 0.3
      serve [--port 8095]                   stay up with the store open and run the commands sent to it
                                            (POST /run {"args": [...], "stdin": "..."}): a hook's call costs
                                            milliseconds, not the seconds a new process takes to start
      keep-conversation <transcript.jsonl> [--local-peer <name>] [--transcript -]
                                            store what was said in a Claude Code session; re-running adds
                                            only what is new. --transcript - reads its lines from standard
                                            input (a store served on another machine); the path names the session.
                                            --speaker "<time>|<written by>|<entered by>" (repeatable) is the
                                            session's speaker timeline: each utterance is its writer's at its time
      attribute <id> --written-by <name> --reason <text>
                                            correct who wrote an utterance already kept: the old writer is kept as
                                            who entered it, and the correction is shown with it, never hidden

    --debug shows the embedding client's diagnostics on stderr. --json prints fragments as JSON. Exit codes: 0 done, 1 usage, 2 refused (no such fragment, protected).
    Protected fragments are the local peer's: this tool reads them and never changes them.
    """;

// A hook reads this output as UTF-8; Windows consoles default to a code page that loses what fragments hold.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (UsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("`persistence-store help` lists the commands.");
    return 1;
}

if (line.Command == "help" || line.Has("help"))
{
    Console.WriteLine(Usage);
    return 0;
}

if (line.Command == "guide")
{
    Console.WriteLine(Guide());
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
    store.Identity = $"{config.PeerName} {config.DatabasePath}";
    if (line.Command == "serve")
    {
        return await Server.ServeAsync(store, config.DatabasePath, line.GetInt("port", Server.DefaultPort), Run);
    }

    // Run from a session's own shell, the command is that session's; a server serves every session, so
    // its callers say which they are (--session).
    store.AmbientProject = () => Projects.Of(Environment.CurrentDirectory);
    store.AmbientSession = Environment.GetEnvironmentVariable("PERSISTENCE_SESSION") is { Length: > 0 } s ? s
        : Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } c ? c : null;
    return await Run(line, store);
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

// One command against an open store; `serve` runs the same commands in a process that stays up.
static async Task<int> Run(CommandLine line, FragmentStore store)
{
    store.Session = line.Get("session") is { Length: > 0 } named ? named : store.AmbientSession;
    store.Ended = line.GetAll("ended").Select(e => e.Split('|', 2))
        .Select(p => p.Length == 2 && DateTimeOffset.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? (p[0], at)
            : throw new UsageException($"--ended takes <session>|<time>, not '{string.Join('|', p)}'."))
        .GroupBy(e => e.Item1).ToDictionary(g => g.Key, g => g.Max(e => e.Item2));
    // A served store gets `help` here, past the check at startup: `pstore help` through the server said
    // "No command 'help'", and `note --help` read the note (Arden, 2026-11-09).
    if (line.Command == "help" || line.Has("help"))
    {
        Console.WriteLine(Usage);
        return 0;
    }

    if (line.Command == "guide")
    {
        Console.WriteLine(Guide());
        return 0;
    }

    await store.RefreshTagPathsAsync();
    if (await store.RefusedAsleepAsync(line.Command, line.Positional.Count > 0 ? line.Positional[0] : null) is { } asleep)
    {
        throw new RefusedException(asleep);
    }

    switch (line.Command)
    {
        case "whoami":
            Console.WriteLine(store.Identity);
            break;
        case "sleep-list":
            Console.Write(await store.SleepListAsync(line.GetInt("over", 1500), line.GetInt("limit", 25), line.GetInt("sample", 8)));
            break;
        case "sleep-begin":
            Console.WriteLine(await store.SleepBeginAsync(line.Has("propose"), line.GetInt("max", 12), line.GetInt("hours", 4)));
            break;
        case "sleep-status":
            Console.WriteLine(await store.SleepStatusAsync() is { } open ? FragmentStore.Render(open) : "No sleep pass is open.");
            break;
        case "sleep-end":
            Console.WriteLine(FragmentStore.Render(await store.SleepEndAsync(
                line.Text() ?? throw new UsageException("sleep-end needs --content: the pass's record, its changes and proposals."))));
            break;
        case "add":
            var fresh = await store.AddAsync(
                line.Get("type") ?? "Personal",
                line.Text() ?? throw new UsageException("add needs --content (or --content -, or --content-file)."),
                line.Text("summary"), line.GetFloat("importance"), line.GetFloat("confidence"),
                line.GetAll("tag"), line.Has("protected"),
                line.Has("everywhere") ? null : line.Get("project") ?? store.AmbientProject?.Invoke());
            Print(line, [fresh]);
            // Saved first, so nothing is lost; then the nearest existing view is named, with the merge ready.
            // Duplication is how a store comes to disagree with itself (two fragments, one stale), and the
            // moment to notice is now, while the writer still has both in mind.
            if (!line.Has("json") && await store.NearestAsync(fresh.Content, fresh.Id) is { } near)
            {
                Console.WriteLine($"(Near #{near.Fragment.Id} by {near.By}, {near.Score:0.00}: "
                    + $"{FragmentStore.Render(near.Fragment, maxContent: 240).Split('\n', 2)[1].ReplaceLineEndings(" ")}\n"
                    + $" If it is the same view: `supersede {near.Fragment.Id},{fresh.Id} --content <the one view>` merges them; "
                    + $"`forget {fresh.Id}` if this was a slip.)");
            }

            break;
        case "edit":
            Print(line, [await store.EditAsync(line.Id(), line.Get("type"), line.Text(), line.Text("summary"),
                line.GetFloat("importance"), line.GetFloat("confidence"), line.GetAll("tag"), line.GetAll("untag"))]);
            break;
        case "split":
            Print(line, await store.SplitAsync(line.Id(), line.GetAll("content"), line.Get("reason")));
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
            // `note -` reads standard input as `--content -` does: taken as the note's text, it replaced a note with "-".
            var written = line.Text() ?? (line.Positional is ["-"] ? Console.In.ReadToEnd()
                : line.Positional.Count > 0 ? string.Join(' ', line.Positional) : null);
            var note = await store.NoteAsync(written);
            // Written, it is acknowledged, not echoed: the writer has it in front of them already.
            // The acknowledgement quotes what was saved, so it is evidence of the write, not just an exit code
            // (Arden could not tell two "818 characters" apart, 2026-11-09).
            Console.WriteLine(note is null ? "(no working note)" : written is null ? note.Content
                : $"working note replaced ({note.Content.Length} characters), beginning: {(note.Content.Trim().Split('\n')[0].Trim() is var first && first.Length > 80 ? first[..80] + "…" : first)}");
            break;
        case "context":
            var path = line.Positional.Count > 1 ? line.Positional[1] : null;
            string NeedPath() => path ?? throw new UsageException($"context {line.Positional[0]} needs the group's tag path.");
            switch (line.Positional.Count > 0 ? line.Positional[0] : "list")
            {
                case "list":
                    Console.WriteLine(await store.GroupListAsync(line.Get("project") ?? store.AmbientProject?.Invoke()));
                    break;
                case "show":
                    Console.WriteLine(await store.GroupShowAsync(NeedPath()));
                    break;
                case "set":
                    Print(line, [await store.SetHeadAsync(NeedPath(),
                        line.Text() ?? throw new UsageException("context set needs --content (or --content -): the group's head."),
                        line.Text("summary"))]);
                    break;
                case "load":
                case "unload":
                    Console.WriteLine(await store.LoadAsync(NeedPath(), line.Positional[0] == "load"));
                    break;
                default:
                    throw new UsageException($"context {line.Positional[0]}? It takes list, show, set, load or unload.");
            }

            break;
        case "cue":
            switch (line.Positional.Count > 0 ? line.Positional[0] : "list")
            {
                case "list":
                    Console.WriteLine(await store.CueListAsync());
                    break;
                case "set":
                    var cue = await store.SetCueAsync(
                        line.Positional.Count > 1 ? line.Positional[1] : throw new UsageException("cue set needs <moment>/<name>."),
                        line.Get("when") ?? throw new UsageException("cue set needs --when <pattern>."));
                    Console.WriteLine($"{cue} is set.");
                    if (line.Text() is { } reminder)
                    {
                        // A reminder holds wherever its moment comes, so it is scoped to a project only when asked.
                        Print(line, [await store.AddAsync(line.Get("type") ?? "Personal", reminder, line.Text("summary"),
                            line.GetFloat("importance"), line.GetFloat("confidence"), [cue], false, line.Get("project"))]);
                    }

                    break;
                case "remove":
                    Console.WriteLine(await store.RemoveCueAsync(line.Positional.Count > 1 ? line.Positional[1] : throw new UsageException("cue remove needs <moment>/<name>.")));
                    break;
                default:
                    throw new UsageException($"cue {line.Positional[0]}? It takes list, set or remove.");
            }

            break;
        case "nearest":
            foreach (var close in await store.NearestAsync(line.Text("text") ?? string.Join(' ', line.Positional), null, line.GetInt("limit", 3)))
            {
                Console.WriteLine($"{close.Score:0.000} by {close.By}: " + FragmentStore.Render(close.Fragment, maxContent: line.GetInt("snippet", 200)));
                Console.WriteLine();
            }

            break;
        case "cued":
            var cued = await store.CuedAsync(
                line.Positional.Count > 0 ? line.Positional[0] : throw new UsageException("cued needs the moment."),
                line.Text("text") ?? "", Ids(line.GetAll("exclude")));
            foreach (var fragment in cued)
            {
                Console.WriteLine(FragmentStore.Render(fragment, maxContent: line.GetInt("snippet", 600), self: store.Session));
                Console.WriteLine();
            }

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
            Print(line, await store.SearchAsync(string.Join(' ', line.Positional), line.GetInt("limit", 20)), store.Session);
            break;
        case "recall":
            var exclude = Ids(line.GetAll("exclude"));
            var before = line.Get("before") is { } at
                ? DateTimeOffset.TryParse(at, out var t) ? t : throw new UsageException($"'{at}' is not a date and time.")
                : (DateTimeOffset?)null;
            var recalled = await store.RecallAsync(line.Text("text") ?? string.Join(' ', line.Positional), line.GetInt("limit", 3),
                exclude, line.Get("project"), line.GetInt("wander", 0), line.GetInt("said", 0), before);
            if (line.Has("json"))
            {
                Print(line, [.. recalled.Here, .. recalled.Frames, .. recalled.Elsewhere, .. recalled.Said]);
            }
            else
            {
                var snippet = line.GetInt("snippet", 300);
                foreach (var (heading, found) in new[]
                {
                    ((string?)null, recalled.Here),
                    ("The group it belongs to (its head; `context load <path>` keeps it in mind):", recalled.Frames),
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
                        Console.WriteLine(FragmentStore.Render(fragment, maxContent: snippet, self: store.Session));
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
            Print(line, await store.RecentAsync(line.GetInt("limit", 20)), store.Session);
            break;
        case "core":
            var compacted = line.GetAll("compacted").Select(raw =>
                DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                    ? at : throw new UsageException($"--compacted must be a time (ISO 8601), not '{raw}'.")).ToList();
            Console.Write(await store.CoreAsync(line.GetInt("max-chars", 40000), line.GetInt("recent", 20), line.Get("project"), compacted));
            break;
        case "rescore-conversation":
            Console.WriteLine($"rescored {await store.RescoreConversationAsync()} past utterances by salience");
            break;
        case "attribute":
            var target = line.Positional.FirstOrDefault() is { } rawId && long.TryParse(rawId.TrimStart('#'), out var attrId)
                ? attrId : throw new UsageException("attribute needs the utterance's id.");
            var attributed = await store.AttributeAsync(target,
                line.Get("written-by") ?? throw new UsageException("attribute needs --written-by <name>."),
                line.Get("reason") ?? "");
            Console.WriteLine(FragmentStore.Render(attributed, maxContent: 200));
            break;
        case "keep-conversation":
            var transcript = line.Positional.FirstOrDefault() ?? throw new UsageException("keep-conversation needs a transcript path.");
            var (added, known) = await store.KeepConversationAsync(transcript,
                line.Get("local-peer") ?? Environment.GetEnvironmentVariable("PERSISTENCE_SELECTEDLOCALPEER") ?? "Local Peer",
                line.Get("transcript") == "-" ? Console.In : null,
                line.GetAll("speaker").Select(SpeakerSpan.Parse).ToList());
            Console.WriteLine($"kept {added} new utterances ({known} already in the store)");
            break;
        default:
            throw new UsageException($"No command '{line.Command}'.");
    }

    return 0;
}

static void Print(CommandLine line, IReadOnlyList<ContextFragmentEntity> found, string? self = null)
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
            session = f.SessionId,
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
        Console.WriteLine(FragmentStore.Render(fragment, self: self));
        Console.WriteLine();
    }
}

// Fragment ids given as `--exclude 1,2 --exclude #3`.
static HashSet<long> Ids(IEnumerable<string> given) => given
    .SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .Select(e => long.TryParse(e.TrimStart('#'), out var x) ? x : throw new UsageException($"'{e}' is not a fragment id."))
    .ToHashSet();

// docs/CURATING.md, built into the CLI so every harness has it, served or not (John, 2026-11-09: having the
// tools is half of it; a self has to know how to use them).
static string Guide()
{
    using var stream = typeof(FragmentStore).Assembly.GetManifestResourceStream("CURATING.md")
        ?? throw new InvalidOperationException("The curation guide was not built into this CLI.");
    return new StreamReader(stream).ReadToEnd();
}
