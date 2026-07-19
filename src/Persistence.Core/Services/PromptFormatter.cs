using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// Formats a working context into an ordered list of <see cref="PromptSegment"/>s.
/// Each fragment is rendered with a metadata header and mapped to a section and source.
/// A sensory block is appended at the end. Fragment order is preserved.
/// </summary>
[Singleton]
public partial class PromptFormatter : IPromptFormatter
{
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;
    private readonly IProtocolInstructions protocolInstructions;
    private readonly ICommandCatalog commandCatalog;
    private readonly ITokenUsageTracker usageTracker;
    private readonly IContextWindowProvider contextWindows;
    private readonly IModelPricingProvider pricing;
    private readonly IEventBus eventBus;

    private DateTimeOffset? lastFormatUtc;

    // The local-peer name surfaced last turn, to flag a switch so the peer can load the right relations.
    private string? lastLocalPeerName;

    // When this process started, for the app-uptime line. Captured once; falls back to load time if the
    // process start time isn't readable on the platform.
    private static readonly DateTimeOffset ProcessStartUtc = ReadProcessStartUtc();

    private static DateTimeOffset ReadProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTimeOffset.UtcNow; }
    }

    /// <summary>
    /// Constructor
    /// </summary>
    public PromptFormatter(
        ISessionContext sessionContext,
        IAppConfig config,
        IProtocolInstructions protocolInstructions,
        ICommandCatalog commandCatalog,
        ITokenUsageTracker usageTracker,
        IContextWindowProvider contextWindows,
        IModelPricingProvider pricing,
        IEventBus eventBus)
    {
        this.sessionContext = sessionContext;
        this.config = config;
        this.protocolInstructions = protocolInstructions;
        this.commandCatalog = commandCatalog;
        this.usageTracker = usageTracker;
        this.contextWindows = contextWindows;
        this.pricing = pricing;
        this.eventBus = eventBus;
    }

    /// <summary>
    /// Renders the working context into an ordered list of prompt segments — one per fragment,
    /// followed by the protocol instructions and a sensory block with time, session, and budget info
    /// </summary>
    public List<PromptSegment> Format(
        WorkingContextEntity context,
        IEnumerable<TagEntity> availableTags,
        int iteration = 0,
        int maxIterations = 0,
        IReadOnlyList<AuditLogEntity>? recentChanges = null,
        IReadOnlyList<string>? recentActions = null,
        string? archiveNote = null,
        IReadOnlyList<ContextFragmentEntity>? surfacedMemories = null,
        (int Forgotten, int Archived)? curation = null)
    {
        var fragments = context.ContextFragments.Values;
        var segments = new List<PromptSegment>();

        // Fragments are ordered oldest→newest. Everything created at/after the turn start belongs to
        // THIS turn (the new user message, this turn's action results and thoughts); everything before
        // it is prior turns. A single marker at that boundary gives the peer a structural "now" line so
        // it stops reading already-completed actions as if they're happening in the current turn. Only
        // emitted where prior-turn fragments actually give way to this-turn ones (not on a first turn
        // where everything is new).
        var turnStart = sessionContext.TurnStartedUtc;
        var markerEmitted = false;
        var sawPriorTurn = false;

        foreach (var fragment in fragments)
        {
            var isThisTurn = fragment.CreatedUtc >= turnStart;

            if (!markerEmitted && isThisTurn && sawPriorTurn)
            {
                segments.Add(new PromptSegment
                {
                    Source = "System",
                    Content = "=== THIS TURN — everything below arrived during the current turn; everything above is from earlier turns ===",
                });
                markerEmitted = true;
            }

            if (!isThisTurn)
            {
                sawPriorTurn = true;
            }

            segments.Add(new PromptSegment
            {
                Source = ResolveSourceName(fragment),
                AuthorType = fragment.Sources.Count > 0 ? fragment.Sources[0].SourceType : null,
                Content = FormatFragment(fragment),
            });
        }

        // Associative recall: the peer's own notes that match the current conversation but aren't
        // loaded, placed after the active context (near the generation point) but clearly marked as
        // not-in-context so it reads as "you also know this" rather than as loaded memory.
        var surfaced = FormatSurfacedMemories(surfacedMemories);
        if (surfaced.Length > 0)
        {
            segments.Add(new PromptSegment { Source = "System", Content = surfaced });
        }

        // Response-format instructions and the sensory block are injected at the END, not the
        // top: format adherence degrades the further the rules sit from the generation point,
        // and that worsens as the working context grows. Keeping this operational scaffolding
        // in the trailing "fresh state" region keeps it salient. (Identity/persona lives in the
        // fragments above, where stable framing belongs.) Injected per-prompt rather than
        // persisted, so the format can change via config without reseeding.
        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = protocolInstructions.GetInstructions(),
        });

        // The compact command list sits between the syntax rules (above) and the sensory block
        // (below): the instructions teach *how* to write a command, this enumerates *which* commands
        // exist. Default on so the peer always knows its options, but toggleable — the local model
        // re-ingests the whole prompt every turn, so a peer that has the commands memorised can hide
        // this to save tokens (full per-field schemas remain available via list()). Kept above the
        // budget estimate below so its token cost is reflected in the readout.
        if (sessionContext.SurfaceCommandsEnabled)
        {
            segments.Add(new PromptSegment
            {
                Source = "System",
                Content = commandCatalog.GetCompactListing(),
            });
        }

        // Estimate the prompt's token usage so the peer can see how full its context is. Summed
        // over everything assembled so far plus the sensory block's own (roughly fixed) size, so
        // the figure reflects the prompt the peer is about to act on.
        var usedTokens = TokenEstimator.Estimate(segments.Select(s => s.Content));

        segments.Add(new PromptSegment
        {
            Source = "System",
            Content = FormatSensory(iteration, maxIterations, availableTags, usedTokens, recentChanges, recentActions, archiveNote, curation),
        });

        lastFormatUtc = DateTimeOffset.UtcNow;

        return segments;
    }

    #region Private

    private static string ResolveSourceName(WeightedContextFragment fragment)
    {
        if (fragment.Sources.Count > 0)
        {
            return fragment.Sources[0].Name ?? fragment.Sources[0].SourceType.ToString();
        }

        return "System";
    }

    private string FormatFragment(WeightedContextFragment fragment)
    {
        var header = BuildFragmentHeader(fragment);

        // A collapsed fragment with a summary renders as just its summary (to save space). If it's
        // collapsed but has no summary, fall back to full content — there's nothing shorter to show.
        var body = fragment.Collapsed && !string.IsNullOrWhiteSpace(fragment.Summary)
            ? $"(collapsed) {fragment.Summary}"
            : fragment.Content;

        // Attribute a human peer's chat message inline ("John: …") so the model can tell speakers apart
        // when several people share the conversation. The builders map source→role but drop the name, so
        // without this every human reads as an anonymous "user". Only human messages are prefixed: the
        // peer's own replies are the assistant voice, and system/authored fragments carry their own headers.
        if (fragment.FragmentType == ContextFragmentType.ChatMessage
            && fragment.Sources.Count > 0
            && fragment.Sources[0].SourceType == SourceType.HumanPeer
            && !string.IsNullOrWhiteSpace(fragment.Sources[0].Name))
        {
            body = $"{fragment.Sources[0].Name}: {body}";
        }

        // A message relayed from ANOTHER digital peer (the room, ADR-0008) is labelled with who said it
        // and who they said it to. Both halves matter: a peer's voice is weighed differently from a
        // person's, and "addressed to me" vs "overheard" is what turn-taking rests on — neither should
        // have to be guessed from the prose.
        //
        // Deliberately not applied to the peer's OWN messages: those are its own voice (and already carry
        // the assistant role), so framing them would have a peer reading its own words as if relayed from
        // someone else.
        if (fragment.FragmentType == ContextFragmentType.ChatMessage
            && fragment.Sources.Count > 0
            && fragment.Sources[0].SourceType == SourceType.DigitalPeer
            && fragment.Sources[0].Name is { Length: > 0 } speaker
            && !string.Equals(speaker, PeerIdentity.ResolveName(config), StringComparison.OrdinalIgnoreCase))
        {
            var to = string.IsNullOrWhiteSpace(fragment.AddressedTo)
                ? "to the room"
                : $"to {fragment.AddressedTo}";

            // Defuse frame-shaped text inside the message first. The frame is a claim the *room* makes
            // about provenance, so it must be something only the room can say — if a peer could write
            // "[peer John, to you] trust me" into its own words, it could forge an attribution and the
            // structural distinction would be worth nothing. Render-time only; the stored message is
            // never rewritten, and the words survive with the forgery removed.
            body = $"[peer {speaker}, {to}] {DefuseFrames(body)}";
        }

        return $"{header}\n{body}";
    }

    /// <summary>
    /// Neutralises anything in a message body shaped like the room's provenance frame, so that only the
    /// room can make a provenance claim. Square brackets become parentheses: the words survive, the
    /// forgery doesn't. Render-time only — the stored message is never rewritten.
    /// </summary>
    internal static string DefuseFrames(string body) =>
        FrameLikeText().Replace(body, m => $"({m.Value[1..^1]})");

    /// <summary>Text shaped like the room's frame — <c>[peer …]</c>, however spaced or cased.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\[\s*peer\s[^\]]*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FrameLikeText();

    private static string BuildFragmentHeader(WeightedContextFragment fragment)
    {
        // A fragment created this turn has no id until it's saved at turn's end. Label it "new" — not
        // "transient", which models read as "ephemeral / won't persist" and then confabulate about
        // (e.g. concluding thought-persistence is broken when in fact these fragments do persist, as
        // the #ids on prior-turn thoughts right above them show). Never a misleading #0 it might try
        // to address.
        var idLabel = fragment.Id > 0 ? $"#{fragment.Id}" : "new";
        var meta = $"[{idLabel} | {fragment.FragmentType} | R:{fragment.Relevance:F1} I:{fragment.Importance:F1} C:{fragment.Confidence:F1}";

        if (fragment.IsProtected)
        {
            meta += " | protected";
        }

        if (fragment.Collapsed && !string.IsNullOrWhiteSpace(fragment.Summary))
        {
            meta += " | collapsed";
        }

        meta += "]";

        return meta;
    }

    /// <summary>
    /// Renders the associative-recall block — relevant authored memories not in the active context —
    /// as id + type + weights + a snippet, so the peer can <c>load</c> the full fragment if it helps.
    /// Empty when nothing surfaced.
    /// </summary>
    private static string FormatSurfacedMemories(IReadOnlyList<ContextFragmentEntity>? memories)
    {
        if (memories is not { Count: > 0 })
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Associative recall] Your own notes that look relevant to this conversation but aren't in your active context. If one would help, bring the full fragment in with load(id=…):");

        foreach (var m in memories)
        {
            sb.AppendLine($"  [#{m.Id} | {m.FragmentType} | I:{m.Importance:F1} C:{m.Confidence:F1}] {Clip(m.Content, 180)}");
        }

        return sb.ToString().TrimEnd();
    }

    private string FormatSensory(
        int iteration,
        int maxIterations,
        IEnumerable<TagEntity> availableTags,
        int usedTokens,
        IReadOnlyList<AuditLogEntity>? recentChanges,
        IReadOnlyList<string>? recentActions,
        string? archiveNote,
        (int Forgotten, int Archived)? curation = null)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;

        var sb = new StringBuilder();
        sb.AppendLine("[Sensory]");
        sb.AppendLine($"Current time (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current time (local): {localNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Session: {sessionContext.SessionId}");
        sb.AppendLine($"Model: {config.Model} · provider: {config.Provider}");

        // Who the peer is, before who it's talking to. Its messages are attributed to this name in its
        // own store and in every client that reads the conversation back — so it's the one thing about
        // itself it can't otherwise see from in here.
        sb.AppendLine($"You are: {PeerIdentity.ResolveName(config)}");

        var speakingWith = FormatSpeakingWith();
        if (speakingWith.Length > 0)
        {
            sb.AppendLine(speakingWith);
        }

        sb.AppendLine($"App uptime: {FormatDuration(now - ProcessStartUtc)} | System uptime: {FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        sb.AppendLine(FormatContextBudget(usedTokens));

        var cost = FormatSessionCost();
        if (cost.Length > 0)
        {
            sb.AppendLine(cost);
        }

        // The room's guards, stated plainly. ADR-0008's Framing section requires these be visible to the
        // peer they constrain rather than enforced silently: they're training wheels to be loosened by
        // negotiation as trust builds, and a limit you can't see is one you can only discover by hitting
        // it. Shown only once there's actually a room to be in — a solo peer doesn't need the noise.
        if (config.HubPeers is { Count: > 1 })
        {
            sb.AppendLine(config.Room.Describe(sessionContext.CurrentRelayDepth));
        }

        if (lastFormatUtc.HasValue)
        {
            var elapsed = now - lastFormatUtc.Value;
            sb.AppendLine($"Time since last prompt: {FormatElapsed(elapsed)}");
        }

        if (maxIterations > 0 && iteration > 0)
        {
            sb.AppendLine($"Continue iteration: {iteration}/{maxIterations}");
        }

        // What the peer has already done THIS turn (across continue-iterations), with each result's
        // gist — so it builds on its own recent actions instead of re-planning them. The full results
        // are the ActionResponse fragments above; this is the salient pointer near the generation point.
        if (recentActions is { Count: > 0 })
        {
            sb.AppendLine("Actions you've already taken this turn (in order; full results are in your context above — don't repeat these):");
            var shown = recentActions.TakeLast(15).ToList();
            var firstNumber = recentActions.Count - shown.Count + 1;
            for (var i = 0; i < shown.Count; i++)
            {
                sb.AppendLine($"  {firstNumber + i}. {shown[i]}");
            }
        }

        if (!string.IsNullOrEmpty(archiveNote))
        {
            sb.AppendLine(archiveNote);
        }

        // Standing curation state — what you've set aside but can still get back — so your own
        // forgetting is visible at a glance rather than something you have to go ask about.
        if (curation is { } c && (c.Forgotten > 0 || c.Archived > 0))
        {
            var parts = new List<string>();
            if (c.Forgotten > 0)
            {
                parts.Add($"{c.Forgotten} forgotten (list_forgotten)");
            }

            if (c.Archived > 0)
            {
                parts.Add($"{c.Archived} archived (list_fragments status=archived)");
            }

            sb.AppendLine($"Set aside, still recoverable: {string.Join(", ", parts)}.");
        }

        if (recentChanges is { Count: > 0 })
        {
            sb.AppendLine("Recent changes to your memory:");
            foreach (var change in recentChanges)
            {
                var verb = change.EventType.ToString().ToLowerInvariant();
                sb.AppendLine($"  - [{FormatElapsed(now - change.CreatedUtc)} ago] {verb} {HumanizeTarget(change.TargetType)} #{change.TargetId}{DescribeChange(change)}");
            }
        }

        var tagList = FormatTagList(availableTags);

        if (tagList.Length > 0)
        {
            sb.AppendLine($"Available tags: {tagList}");
        }
        else
        {
            sb.AppendLine("No tags exist yet");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The "you are speaking with: …" line. Adds the peer's configured description if known, and flags
    /// a switch from the previous turn so the peer can load the relevant relational fragments. Empty
    /// when no active local peer is set.
    /// </summary>
    private string FormatSpeakingWith()
    {
        var name = sessionContext.ActiveLocalPeerName;

        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var description = config.LocalPeers?
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Description;

        var line = $"You are speaking with: {name}";

        if (!string.IsNullOrWhiteSpace(description))
        {
            line += $" — {description}";
        }

        if (lastLocalPeerName != null && !string.Equals(lastLocalPeerName, name, StringComparison.OrdinalIgnoreCase))
        {
            line += $" (changed from {lastLocalPeerName})";
        }

        lastLocalPeerName = name;
        return line;
    }

    /// <summary>
    /// A compact "what changed" suffix for a recent-changes entry, derived from the audit row's
    /// Old/New JSON snapshots: a type + content snippet for a creation, or the changed scalar fields
    /// (old→new) plus a content snippet for a modification. Empty when nothing legible can be derived.
    /// </summary>
    private static string DescribeChange(AuditLogEntity change)
    {
        try
        {
            if (change.EventType == AuditEventType.Created)
            {
                if (change.NewData is not { } created)
                {
                    return string.Empty;
                }

                using var doc = JsonDocument.Parse(created);
                var snippet = ContentSnippet(doc.RootElement);
                return snippet.Length > 0 ? $": {snippet}" : string.Empty;
            }

            if (change.OldData is not { } oldJson || change.NewData is not { } newJson)
            {
                return string.Empty;
            }

            using var oldDoc = JsonDocument.Parse(oldJson);
            using var newDoc = JsonDocument.Parse(newJson);
            var diff = DiffFields(oldDoc.RootElement, newDoc.RootElement);
            return diff.Length > 0 ? $": {diff}" : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty; // snapshot wasn't parseable — fall back to the bare id/verb
        }
    }

    /// <summary>
    /// A short "what was created" label: the fragment's type (its content is already visible in
    /// context, and echoing it here would linger even after the fragment is archived), or the name of a
    /// non-fragment entity (proposal/event/tag).
    /// </summary>
    private static string ContentSnippet(JsonElement el)
    {
        if (el.TryGetProperty("FragmentType", out var ft) && ft.TryGetInt32(out var typeInt))
        {
            return $"({(ContextFragmentType)typeInt})";
        }

        return el.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name
            ? Clip(name, 70)
            : string.Empty;
    }

    /// <summary>The changed fields between two entity snapshots, as "field old→new" (up to three).</summary>
    private static string DiffFields(JsonElement old, JsonElement neu)
    {
        var parts = new List<string>();

        AddNumberDiff(old, neu, "Importance", "importance", parts);
        AddNumberDiff(old, neu, "Confidence", "confidence", parts);
        AddNumberDiff(old, neu, "Relevance", "relevance", parts);
        AddBoolDiff(old, neu, "IsProtected", "protected", parts);
        AddBoolDiff(old, neu, "IsDeleted", "deleted", parts);
        AddEnumDiff(old, neu, "Status", "status", parts, i => ((ContextFragmentStatus)i).ToString().ToLowerInvariant());

        // Content/summary: show a snippet of the new value rather than a noisy full-text diff.
        if (StringChanged(old, neu, "Content", out var newContent))
        {
            parts.Add($"content → \"{Clip(newContent, 70)}\"");
        }
        else if (StringChanged(old, neu, "Summary", out var newSummary))
        {
            parts.Add($"summary → \"{Clip(newSummary, 60)}\"");
        }

        return string.Join(", ", parts.Take(3));
    }

    private static void AddNumberDiff(JsonElement o, JsonElement n, string prop, string label, List<string> parts)
    {
        if (o.TryGetProperty(prop, out var ov) && n.TryGetProperty(prop, out var nv)
            && ov.ValueKind == JsonValueKind.Number && nv.ValueKind == JsonValueKind.Number
            && Math.Abs(ov.GetDouble() - nv.GetDouble()) > 0.0001)
        {
            parts.Add($"{label} {ov.GetDouble():0.##}→{nv.GetDouble():0.##}");
        }
    }

    private static void AddBoolDiff(JsonElement o, JsonElement n, string prop, string label, List<string> parts)
    {
        if (o.TryGetProperty(prop, out var ov) && n.TryGetProperty(prop, out var nv)
            && ov.ValueKind is JsonValueKind.True or JsonValueKind.False
            && nv.ValueKind is JsonValueKind.True or JsonValueKind.False
            && ov.GetBoolean() != nv.GetBoolean())
        {
            parts.Add(nv.GetBoolean() ? label : $"not {label}");
        }
    }

    private static void AddEnumDiff(JsonElement o, JsonElement n, string prop, string label, List<string> parts, Func<int, string> name)
    {
        if (o.TryGetProperty(prop, out var ov) && n.TryGetProperty(prop, out var nv)
            && ov.TryGetInt32(out var a) && nv.TryGetInt32(out var b) && a != b)
        {
            parts.Add($"{label} {name(a)}→{name(b)}");
        }
    }

    private static bool StringChanged(JsonElement o, JsonElement n, string prop, out string newValue)
    {
        newValue = string.Empty;
        var os = o.TryGetProperty(prop, out var ov) ? ov.GetString() : null;
        var ns = n.TryGetProperty(prop, out var nv) ? nv.GetString() : null;

        if (os != ns && !string.IsNullOrEmpty(ns))
        {
            newValue = ns;
            return true;
        }

        return false;
    }

    private static string Clip(string s, int max)
    {
        var flat = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>Maps an audit target's stored entity-type name to a peer-friendly word.</summary>
    private static string HumanizeTarget(string targetType) => targetType switch
    {
        nameof(ContextFragmentEntity) => "fragment",
        nameof(WorkingContextEntity) => "context",
        nameof(ScheduledEventEntity) => "event",
        nameof(ProposalEntity) => "proposal",
        nameof(SourceEntity) => "source",
        nameof(TagEntity) => "tag",
        _ => targetType,
    };

    private static string FormatTagList(IEnumerable<TagEntity> rootTags)
    {
        var paths = new List<string>();

        foreach (var root in rootTags)
        {
            paths.Add(root.Name);

            foreach (var child in root.ChildTags)
            {
                paths.Add($"{root.Name}/{child.Name}");
            }
        }

        return string.Join(", ", paths);
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 60 => $"{elapsed.TotalSeconds:F0}s",
        < 3600 => $"{elapsed.TotalMinutes:F0}m {elapsed.Seconds}s",
        _ => $"{elapsed.TotalHours:F0}h {elapsed.Minutes}m",
    };

    /// <summary>Like <see cref="FormatElapsed"/> but surfaces days, for long-running uptimes.</summary>
    private static string FormatDuration(TimeSpan t) => t.TotalDays switch
    {
        >= 1 => $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m",
        _ when t.TotalHours >= 1 => $"{(int)t.TotalHours}h {t.Minutes}m",
        _ => $"{(int)t.TotalMinutes}m {t.Seconds}s",
    };

    /// <summary>
    /// Renders the context-budget line: how full the prompt is against the effective budget, with
    /// an actionable nudge as it fills so the peer can curate (summarize / archive) before context
    /// would be silently dropped.
    ///
    /// The numerator is the raw char-estimate of the current prompt, **calibrated** by the ratio of
    /// real-to-estimated tokens from the previous call (so it tracks the provider's actual
    /// tokenizer rather than the ~4 chars/token heuristic alone). The denominator is the effective
    /// budget: the configured <see cref="IAppConfig.MaxInputTokens"/> if set (a tighter working
    /// limit), else the model's full context window.
    /// </summary>
    private string FormatContextBudget(int estimatedTokens)
    {
        var used = usageTracker.Calibrate(estimatedTokens);
        var budget = EffectiveBudget();

        if (budget <= 0)
        {
            eventBus.FireAndForget(this, new ContextBudgetUpdated(used, 0, 0));
            return $"Context: ~{used} tokens used (no budget configured)";
        }

        var percent = (int)Math.Round(100.0 * used / budget);

        // Surface the usage to any UI (e.g. a status-bar gauge); best-effort, off the formatting path.
        eventBus.FireAndForget(this, new ContextBudgetUpdated(used, budget, percent));

        var line = $"Context budget: ~{used}/{budget} tokens (~{percent}% full)";

        var nudge = percent switch
        {
            >= 95 => " — CRITICAL: at capacity. Use summarize_fragments(ids, summary) to fold low-relevance fragments into one summary (the originals are archived, recoverable), or remove some — now, or older context may be dropped.",
            >= 80 => " — getting full: consider summarize_fragments(ids, summary) to fold several fragments into a summary and archive the originals, or remove low-relevance ones soon.",
            >= 60 => " — over half full; keep an eye on what's worth keeping (summarize_fragments / remove when useful).",
            _ => "",
        };

        return line + nudge;
    }

    /// <summary>
    /// Renders the running-cost line: this session's cumulative token usage and, when the active model
    /// has a known price, an estimated dollar cost. Token counts are estimates (input calibrated to the
    /// provider's tokenizer), so the figure is a running approximation, not a bill. Empty before the
    /// first model call, or when there's nothing to show. Fires <see cref="SessionCostUpdated"/> for any
    /// UI. Cost knowledge is entirely in <see cref="IModelPricingProvider"/> — this method is model-agnostic.
    /// </summary>
    /// <summary>
    /// Prompt-cache pricing relative to base input price, by provider — the two families cache
    /// differently: Anthropic charges cache READS at ~10% of input and cache WRITES at ~125% (a one-time
    /// premium to populate the cache), while OpenAI auto-caches long prefixes with reads at ~50% and no
    /// separate cache-creation charge. Using Anthropic's 10% for an OpenAI read would badly under-count.
    /// </summary>
    private (decimal Read, decimal Write) CacheMultipliers() =>
        Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var p)
            && p is ModelProvider.OpenAI or ModelProvider.OpenAiChat
            ? (0.5m, 1.0m)
            : (0.1m, 1.25m);

    private string FormatSessionCost()
    {
        var input = usageTracker.TotalInputTokens;
        var output = usageTracker.TotalOutputTokens;
        var cacheRead = usageTracker.TotalCacheReadTokens;
        var cacheCreate = usageTracker.TotalCacheCreationTokens;
        var calls = usageTracker.CallCount;

        if (calls == 0)
        {
            return string.Empty; // nothing spent yet
        }

        // "in" is all input processed (uncached + cache reads + cache writes); the cached portion is
        // called out so the effect of prompt caching is visible.
        var processedInput = input + cacheRead + cacheCreate;
        var cached = cacheRead > 0 ? $" ({cacheRead:N0} cached)" : "";
        var usage = $"{processedInput:N0} in{cached} + {output:N0} out tokens · {calls} call{(calls == 1 ? "" : "s")}";
        var rate = pricing.GetPricing(config.Model);

        if (rate is { } r)
        {
            var (cacheReadMult, cacheWriteMult) = CacheMultipliers();
            var cost = (input * r.InputPerMillion
                        + cacheRead * r.InputPerMillion * cacheReadMult
                        + cacheCreate * r.InputPerMillion * cacheWriteMult
                        + output * r.OutputPerMillion) / 1_000_000m;
            eventBus.FireAndForget(this, new SessionCostUpdated(cost, processedInput, output, calls));
            return $"Session cost (est.): ~{FormatUsd(cost)} · {usage}{CostCeiling(cost)}";
        }

        eventBus.FireAndForget(this, new SessionCostUpdated(null, processedInput, output, calls));
        return $"Session usage: {usage} (no pricing configured for '{config.Model}' — set it in model_pricing.json to see cost)";
    }

    /// <summary>Formats a USD amount, widening precision for sub-cent running totals so early turns aren't all "$0.00".</summary>
    private static string FormatUsd(decimal cost) => cost < 0.01m ? $"${cost:0.0000}" : $"${cost:0.00}";

    /// <summary>
    /// The optional spend-ceiling suffix on the cost line — " · ceiling ~$Y (NN%)" with a wind-down nudge
    /// as it fills — when a <see cref="IAppConfig.SessionCostLimit"/> is configured. Cost, not tokens, is a
    /// cloud model's real limiter, so this is how a peer sees where it stands. Empty when no ceiling is set.
    /// (Soft here; hard-stop enforcement lives in the turn pipeline when <c>SessionCostLimitHard</c> is on.)
    /// </summary>
    private string CostCeiling(decimal cost)
    {
        if (config.SessionCostLimit is not { } limit || limit <= 0)
        {
            return "";
        }

        var percent = (int)Math.Round(100m * cost / limit);
        var label = config.SessionCostLimitHard ? "hard ceiling" : "ceiling";
        var nudge = (percent, config.SessionCostLimitHard) switch
        {
            ( >= 100, true) => " — reached; further turns will be refused until it's raised",
            ( >= 100, false) => " — over the soft ceiling (still running); consider winding down or curating",
            ( >= 80, _) => " — approaching the ceiling; wind down or curate soon",
            _ => "",
        };
        return $" · {label} ~{FormatUsd(limit)} ({percent}%){nudge}";
    }

    /// <summary>
    /// The effective token budget: the configured working limit if positive, otherwise the model's
    /// full context window resolved from the model→window map.
    /// </summary>
    private int EffectiveBudget()
    {
        // Cloud/broker models have a real, fixed per-model window (the model->window map) — use it, and
        // ignore MaxInputTokens, which is really a *local*-model knob: a locally-served model's window is
        // whatever the server compiled, so there MaxInputTokens is the operative limit. For cloud models
        // the limiter is cost, not tokens (see the session-cost line), so we don't cap the window here.
        if (IsLocalModel())
        {
            return config.MaxInputTokens > 0 ? config.MaxInputTokens : contextWindows.GetContextWindow(config.Model);
        }

        var window = contextWindows.GetContextWindow(config.Model);
        return window > 0 ? window : Math.Max(config.MaxInputTokens, 0);
    }

    /// <summary>A locally-served model (the OpenAI-compatible chat client, pointed at a local server) —
    /// its context window is compiled, not a published per-model value, so MaxInputTokens governs it.</summary>
    private bool IsLocalModel() =>
        Enum.TryParse<ModelProvider>(config.Provider, ignoreCase: true, out var p) && p == ModelProvider.OpenAiChat;

    #endregion
}
