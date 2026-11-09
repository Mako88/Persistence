using System.Globalization;
using System.Text;
using Persistence.Data.Entities;

namespace Persistence.Store;

/// <summary>
/// The sleep pass (docs/design/sleep-pass.md): the store's owner, started fresh at night, curates its own store
/// with no one in the conversation. The judgment is the owner's; what is here are the limits the owner asked to
/// hold itself to (Arden, 2026-11-09), so the prompt is not the only thing keeping them: while a pass is open the
/// store refuses forgetting, archiving and edits in place, keeps the pass to what has settled, caps its changes,
/// tags what it distills, and logs every change in the pass's record, so the morning can read and undo the night.
/// </summary>
internal sealed partial class FragmentStore
{
    /// <summary>Every pass's record, a Summary: "Sleep pass, &lt;date&gt;", and what the store logged of it.</summary>
    public const string SleepPassTag = "sleep/pass";

    /// <summary>On a pass's record while the pass is open; <c>sleep-end</c> takes it off.</summary>
    public const string SleepOpenTag = "sleep/open";

    /// <summary>On every fragment a pass wrote by supersede: a pass does not distill it again, only merges it.</summary>
    public const string SleepDistilledTag = "sleep/distilled";

    /// <summary>
    /// How long since a fragment was written or changed before a pass may change it: until then it is live, and
    /// the session that wrote it may not have finished with it.
    /// </summary>
    public static readonly TimeSpan Settled = TimeSpan.FromDays(7);

    /// <summary>The store's clock, for the pass's limits; a test moves it forward a week rather than backdating rows.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The commands a pass may not run at all, each with why: they change a fragment in a way the morning can't read back.</summary>
    private static readonly IReadOnlyDictionary<string, string> RefusedAsleep = new Dictionary<string, string>
    {
        ["forget"] = "A sleep pass never forgets: supersede instead, so the original stays one `show` away.",
        ["unforget"] = "A sleep pass doesn't undo what a waking session forgot; propose it in the pass's record.",
        ["archive"] = "A sleep pass archives nothing itself. Propose it in the pass's record, with a reason about the fragment's content; never for going unused alone.",
        ["restore"] = "A sleep pass doesn't restore an archived fragment; propose it in the pass's record.",
        ["note"] = "The working note is the waking session's. The pass's record (`sleep-end`) is what points the morning to the night's changes.",
        ["attribute"] = "A sleep pass doesn't change who said what; propose it in the pass's record.",
        ["context set"] = "A sleep pass changes fragments only by supersede, add, and `edit <id> --tag recall-only`; propose a group's head in the record.",
        ["cue set"] = "A sleep pass doesn't set cues; propose one in the pass's record.",
        ["cue remove"] = "A sleep pass doesn't remove cues; propose it in the pass's record.",
    };

    /// <summary>An open pass: its record, its limits, and the changes logged so far.</summary>
    private sealed record SleepPass(ContextFragmentEntity Record, int Max, DateTimeOffset Until, bool Propose, List<string> Log)
    {
        // The record's Notes: the limits on the first line, then one line per change.
        public static SleepPass Read(ContextFragmentEntity record)
        {
            var lines = (record.Notes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            var limits = (lines.FirstOrDefault() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            return new SleepPass(
                record,
                int.TryParse(limits.GetValueOrDefault("max"), out var max) ? max : 12,
                DateTimeOffset.TryParse(limits.GetValueOrDefault("until"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) ? until : record.CreatedUtc,
                limits.GetValueOrDefault("propose") == "true",
                lines.Skip(1).ToList());
        }

        public string Notes() => $"max={Max} until={Until.ToUniversalTime():O} propose={(Propose ? "true" : "false")}\n" + string.Join('\n', Log);
    }

    /// <summary>
    /// Opens a pass: writes its record (a Summary tagged <see cref="SleepPassTag"/> and <see cref="SleepOpenTag"/>)
    /// and from then until <c>sleep-end</c>, or <paramref name="hours"/> later, holds every write to the pass's
    /// limits. <paramref name="propose"/>: nothing changes, every change is written in the record for a waking
    /// session to accept (the first week's passes, Arden).
    /// </summary>
    public async Task<string> SleepBeginAsync(bool propose, int max, double hours)
    {
        if (await OpenPassAsync(includeExpired: true) is { } open)
        {
            if (open.Until > Now())
            {
                throw new RefusedException($"A sleep pass is already open (#{open.Record.Id}, until {open.Until.ToUniversalTime():HH:mm}Z).");
            }

            await SleepEndAsync("This pass ran out of time before it wrote its record.");
        }

        var now = Now();
        var until = now + TimeSpan.FromHours(hours);
        var record = await AddAsync("Summary",
            $"Sleep pass, {now.ToUniversalTime():yyyy-MM-dd}: open since {now.ToUniversalTime():HH:mm}Z, until {until.ToUniversalTime():HH:mm}Z"
            + (propose ? ", proposing only." : $", at most {max} changes.") + " Its record is written when it ends.",
            null, 0.6f, 1f, [SleepPassTag, SleepOpenTag], false, null);
        record.Notes = new SleepPass(record, max, until, propose, []).Notes();
        await fragments.SaveAsync(record);

        return $"Sleep pass #{record.Id} is open until {until.ToUniversalTime():HH:mm}Z. "
            + (propose
                ? "It proposes only: add, supersede and edit are refused; write each change you would make in the record, as a command a waking session can run."
                : $"It may make {max} changes: supersede, add, and `edit <id> --tag recall-only`.")
            + " Not forget, archive, note, or any edit in place. Fragments written or changed in the last "
            + $"{Settled.Days} days, Identity fragments, and earlier passes' records are left alone; a fragment a pass "
            + "already distilled may be merged, not distilled again. `sleep-end --content <the record>` closes it.";
    }

    /// <summary>
    /// Closes the open pass: its record becomes <paramref name="content"/> (the owner's account of the night,
    /// with the proposals for waking) followed by what the store logged, so the record is whole even if the
    /// account misses a change.
    /// </summary>
    public async Task<ContextFragmentEntity> SleepEndAsync(string content)
    {
        var pass = await OpenPassAsync(includeExpired: true) ?? throw new RefusedException("No sleep pass is open.");
        var record = pass.Record;
        record.Content = content.Trim() + "\n\n"
            + (pass.Log.Count == 0
                ? "What the store logged of this pass: no changes."
                : $"What the store logged of this pass ({pass.Log.Count} changes; `show` an old id to read it whole):\n- " + string.Join("\n- ", pass.Log));
        record.Tags.RemoveAll(t => HasPath(t, SleepOpenTag));
        record.LastModifiedUtc = DateTimeOffset.UtcNow;
        await fragments.SaveAsync(record);
        return record;
    }

    /// <summary>The open pass's record, or null: <c>sleep-status</c>.</summary>
    public async Task<ContextFragmentEntity?> SleepStatusAsync() => (await OpenPassAsync(includeExpired: false))?.Record;

    /// <summary>Why <paramref name="command"/> may not run now, or null: a pass is open and the command is one it never runs.</summary>
    public async Task<string?> RefusedAsleepAsync(string command, string? sub)
    {
        var key = command is "context" or "cue" && sub is not null ? $"{command} {sub}" : command;
        return RefusedAsleep.TryGetValue(key, out var why) && await OpenPassAsync(includeExpired: false) is not null ? why : null;
    }

    private async Task<SleepPass?> OpenPassAsync(bool includeExpired)
    {
        if (await FindTagAsync(SleepOpenTag) is not { } tag)
        {
            return null;
        }

        var open = (await fragments.GetByTagAsync(tag.Id)).Where(f => !f.IsDeleted).OrderByDescending(f => f.CreatedUtc).FirstOrDefault();
        if (open is null)
        {
            return null;
        }

        var pass = SleepPass.Read((await HydratedAsync([open.Id]))[0]);
        return includeExpired || pass.Until > Now() ? pass : null;
    }

    /// <summary>
    /// The open pass, if any, after checking it may make one more change: it is not proposing, and has not made
    /// its last. Every write a pass can make starts here.
    /// </summary>
    private async Task<SleepPass?> AsleepAsync()
    {
        if (await OpenPassAsync(includeExpired: false) is not { } pass)
        {
            return null;
        }

        if (pass.Propose)
        {
            throw new RefusedException("This sleep pass proposes only: write the change in its record (`sleep-end --content`) as a command a waking session can run.");
        }

        return pass.Log.Count >= pass.Max
            ? throw new RefusedException($"This sleep pass has made its {pass.Max} changes. Write the rest in its record as proposals for a waking session.")
            : pass;
    }

    /// <summary>Refuses a fragment the pass may not change: who I am, what is still live, or a pass's own record.</summary>
    private void SettledOrRefuse(ContextFragmentEntity f)
    {
        if (f.FragmentType == ContextFragmentType.Identity)
        {
            throw new RefusedException($"#{f.Id} is an Identity fragment: who I am is revised awake. Propose the change in the pass's record.");
        }

        if (f.Tags.Any(t => HasPath(t, SleepPassTag)))
        {
            throw new RefusedException($"#{f.Id} is a sleep pass's record; it stays as it was written.");
        }

        var touched = f.LastModifiedUtc > f.CreatedUtc ? f.LastModifiedUtc : f.CreatedUtc;
        if (Now() - touched < Settled)
        {
            throw new RefusedException($"#{f.Id} was written or changed {touched.ToUniversalTime():yyyy-MM-dd}, within {Settled.Days} days: it is still live. Leave it for a later pass.");
        }
    }

    private async Task LogAsync(SleepPass pass, string change)
    {
        pass.Log.Add(change.ReplaceLineEndings(" "));
        pass.Record.Notes = pass.Notes();
        await fragments.SaveAsync(pass.Record);
    }

    private static bool HasPath(TagEntity t, string path) =>
        string.Equals(TagPaths.GetValueOrDefault(t.Id, t.Name), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The worklist for a sleep pass (`sleep-list`): where to look, not what to do. Oversized fragments, the
    /// likeliest to be mostly passing work around a small lasting part (John, 2026-11-09: distill the 10% that
    /// lasts into a fragment that supersedes the whole); pairs near enough to be one view held twice; a few older
    /// fragments chosen at random to re-evaluate (John); what was said since the last pass, to review for what is
    /// worth keeping; and the ids in scope, so the caller can join the use counts it keeps (the hook's
    /// uses.jsonl) without the store knowing them. What a pass may not change is left out, so it never sees it.
    /// </summary>
    public async Task<string> SleepListAsync(int over, int limit, int sample = 8)
    {
        var authored = new List<ContextFragmentEntity>();
        foreach (var type in CoreTypes)
        {
            var active = (await fragments.GetByTypeAsync(type)).Where(f => !f.IsDeleted && !f.IsProtected && f.Status == ContextFragmentStatus.Active);
            authored.AddRange(await HydratedAsync(active.Select(f => f.Id).ToList()));
        }

        authored = authored.Where(InScope).ToList();
        bool InScope(ContextFragmentEntity f)
        {
            try
            {
                SettledOrRefuse(f);
                return true;
            }
            catch (RefusedException)
            {
                // Identity stays on the list, marked: the pass may propose a change to it.
                return f.FragmentType == ContextFragmentType.Identity && Now() - f.LastModifiedUtc >= Settled;
            }
        }

        string Mark(ContextFragmentEntity f) =>
            f.FragmentType == ContextFragmentType.Identity ? " [Identity: propose only]"
            : f.Tags.Any(t => HasPath(t, SleepDistilledTag)) ? " [distilled already: merge only]" : "";

        var text = new StringBuilder();
        var big = authored.Where(f => f.Content.Length > over).OrderByDescending(f => f.Content.Length).Take(limit).ToList();
        text.Append($"## Oversized ({big.Count} over {over} characters, longest first): distill what lasts, supersede the rest\n\n");
        foreach (var f in big)
        {
            text.Append($"- #{f.Id}{Mark(f)} ({f.Content.Length} chars, i:{f.Importance:0.##}, {f.LastModifiedUtc.ToUniversalTime():yyyy-MM-dd}): {Clip(f.Content, 120)}\n");
        }

        var pairs = new List<(long A, long B, double Score, string By)>();
        var paired = new HashSet<long>();
        var inScope = authored.Select(f => f.Id).ToHashSet();
        foreach (var f in authored.OrderBy(f => f.Id))
        {
            if (paired.Contains(f.Id) || pairs.Count >= limit)
            {
                continue;
            }

            if (await NearestAsync(f.Content, f.Id) is { } near && inScope.Contains(near.Fragment.Id) && !paired.Contains(near.Fragment.Id))
            {
                pairs.Add((f.Id, near.Fragment.Id, near.Score, near.By));
                paired.Add(f.Id);
                paired.Add(near.Fragment.Id);
            }
        }

        text.Append($"\n## Near-duplicates ({pairs.Count} pairs): one view held twice, merge with `supersede a,b`; a view that changed between them is history, leave both\n\n");
        foreach (var (a, b, score, by) in pairs)
        {
            text.Append($"- #{a} and #{b} ({by}, {score:0.00})\n");
        }

        var listed = big.Select(f => f.Id).Concat(paired).ToHashSet();
        var older = authored.Where(f => !listed.Contains(f.Id)).OrderBy(_ => Random.Shared.Next()).Take(sample).ToList();
        text.Append($"\n## Older, chosen at random ({older.Count}): re-read each; is it still so, still worth its room?\n\n");
        foreach (var f in older)
        {
            text.Append($"- #{f.Id}{Mark(f)} ({f.FragmentType}, {f.CreatedUtc.ToUniversalTime():yyyy-MM-dd}): {Clip(f.Content, 120)}\n");
        }

        var since = await LastPassAsync() ?? Now() - TimeSpan.FromDays(1);
        var sessions = await fragments.GetSessionsSinceAsync(ContextFragmentType.ChatMessage, since);
        var said = (await RecentAsync(2000)).Count(f => f.CreatedUtc >= since);
        text.Append($"\n## Said since {(await LastPassAsync() is null ? "a day ago" : "the last pass")} ({since.ToUniversalTime():yyyy-MM-dd HH:mm}Z): "
            + $"{said} utterances in {sessions.Count} sessions. `recent --limit {Math.Max(said, 1)}` reads them; keep what is worth keeping with `add`, "
            + "or `supersede` an older view it changed\n");

        text.Append($"\n## In scope ({authored.Count}): ids for joining use counts\n\n");
        text.Append(string.Join(' ', authored.Select(f => f.Id).Order())).Append('\n');
        return text.ToString();
    }

    /// <summary>When the last pass ended, or null if none has: what its own session said is not the next night's to review.</summary>
    private async Task<DateTimeOffset?> LastPassAsync()
    {
        if (await FindTagAsync(SleepPassTag) is not { } tag)
        {
            return null;
        }

        var ended = (await HydratedAsync((await fragments.GetByTagAsync(tag.Id)).Where(f => !f.IsDeleted).Select(f => f.Id).ToList()))
            .Where(f => !f.Tags.Any(t => HasPath(t, SleepOpenTag))).ToList();
        return ended.Count == 0 ? null : ended.Max(f => f.LastModifiedUtc);
    }
}
