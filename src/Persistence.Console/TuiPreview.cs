using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;

namespace Persistence.Console;

/// <summary>
/// A standalone preview of the Terminal.Gui front-end, launched with <c>--preview [variant]</c>. It
/// builds the real <see cref="TerminalGuiDisplayProvider"/> and pushes representative sample content
/// into every pane (conversation, reasoning, actions, schedule, debug) — no database, model, or
/// orchestrator — so the layout and colour rules can be reviewed and iterated on quickly. An optional
/// variant (A/B/C) swaps the You/Remote-Peer role colours for comparison. Type <c>/exit</c> to quit.
/// </summary>
internal static class TuiPreview
{
    public static async Task RunAsync(string? arg = null)
    {
        // Optional numeric arg opens the preview on that side-column tab (for screenshots).
        if (int.TryParse(arg, out var tab))
        {
            TerminalGuiDisplayProvider.PreviewInitialTab = tab;
        }

        var config = await AppConfig.LoadAsync();
        var bus = new EventBus();
        var session = new SessionContext { SessionId = "preview" };
        var display = new TerminalGuiDisplayProvider(bus, session, config);

        using var cts = new CancellationTokenSource();

        // The preview has no orchestrator, so wire /exit straight to Stop and echo other input.
        bus.Subscribe<DisplayInputReceived>((_, e) =>
        {
            if (string.Equals(e.Input?.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
            {
                display.Stop();
            }
            else
            {
                display.ShowReply($"(preview) you said: {e.Input}");
            }

            return Task.CompletedTask;
        });

        // Buffered until the loop is ready, then flushed into the panes on load.
        PushSamples(display);

        await display.Start(cts.Token);
    }

    private static void PushSamples(TerminalGuiDisplayProvider d)
    {
        var now = DateTimeOffset.Now;

        // Conversation — prior history (folded in on startup) then the live exchange and marker lines.
        d.ShowChatHistory(
        [
            ("user", "Hey, can you remember that my name is John?", now.AddHours(-2)),
            ("assistant", "Of course — I've noted your name is John.", now.AddHours(-2).AddSeconds(8)),
            ("user", "And that I value honest, careful engineering.", now.AddHours(-1)),
        ]);

        d.ShowSystemMessage($"[{now.LocalDateTime:MM/dd/yyyy hh:mm tt}] You: What do you remember about me?");
        d.ShowReply("I remember your name is John, and that you value honest, careful engineering. "
            + "I've kept both as protected Identity-adjacent notes so they persist across sessions.");
        d.ShowMessageQueued("one more thing…");
        d.ShowWakeUpEvent(new ScheduledEventEntity
        {
            Name = "morning check-in",
            WorkingContextId = 1,
            ScheduledForUtc = now.UtcDateTime.AddDays(1),
            Status = ScheduledEventStatus.Pending,
            CreatedUtc = now,
            LastModifiedUtc = now,
        });
        d.ShowUnknownCommand("/foo");
        d.ShowSystemMessage($"[{now.LocalDateTime.ToString("MM/dd/yyyy hh:mm tt")}] Executed /proposals");
        d.ShowError("tag 'persnoality/values' not found — did you mean 'personality/values'?");

        // Schedule pane — pending scheduled events the peer set for itself.
        d.ShowScheduledEvents(
        [
            new ScheduledEventEntity
            {
                Name = "morning check-in",
                WorkingContextId = 1,
                ScheduledForUtc = now.UtcDateTime.AddDays(1),
                Status = ScheduledEventStatus.Pending,
                WakePrompt = "Reconnect with my sense of continuity.",
                CreatedUtc = now,
                LastModifiedUtc = now,
            },
            new ScheduledEventEntity
            {
                Name = "review tags",
                WorkingContextId = 1,
                ScheduledForUtc = now.UtcDateTime.AddHours(3),
                Status = ScheduledEventStatus.Pending,
                CreatedUtc = now,
                LastModifiedUtc = now,
            },
        ]);

        // Reasoning pane — open thoughts and streamed reasoning.
        d.ShowThought("The peer asked what I remember. I'll recall the Identity and Relational notes "
            + "before replying, and consider tagging this exchange.");
        d.ShowReasoning("Weighing whether to fold the two notes into a single Summary fragment to save "
            + "budget, or keep them separate for clarity. Keeping separate for now.");

        // Actions pane — command invocations. The request is the command's JSON fields (as the real
        // app sends), rendered one parameter per line. The first sample carries escaped quotes,
        // apostrophes and newlines to confirm they're decoded for display (no \uXXXX / \n gibberish).
        d.ShowToolUse("add",
            """{"content":"First Audit Findings:\n- Memory is lean (~13% full).\n- Lack of specific \"Domain\" tags (e.g. project's).\n- Goal: move from 'narrative' to structured tagging.","fragment_type":"Personal","importance":0.8,"tags":["meta/strategy","audit"]}""",
            "Added Personal fragment with 2 tag(s) (created new tag(s): audit)");
        d.ShowToolUse("add",
            """{"content":"My name is John.","fragment_type":"Identity","tags":["identity/core"]}""",
            "Added Identity fragment with 1 tag(s)");
        d.ShowToolUse("tag", """{"entity_type":"context","tag":"mode/reflection"}""",
            "Tagged context #1 'Default' with 'mode/reflection'");
        d.ShowToolUse("fetch", """{"tag":"identity/core"}""",
            "Fragments tagged 'identity/core' (1):\n[#6 | Identity | I:0.9 C:0.5]\nMy name is John.");

        // Debug pane — Request and Response are separate (timestamped) entries, as in the real app.
        // The request is rendered from the logical prompt segments (fragment headers, protocol
        // instructions, sensory block) with no inline [role] labels. The sample deliberately includes
        // prose that used to be mis-coloured (a wrapped "arguments:", "triple quotes (…):", the word
        // "is_protected", inline <context>/<actions> mentions, and a line ending in "goals :)") to
        // confirm only true structure is coloured now.
        d.ShowDebugInfo(
            "Request:\n"
            + "You are not an assistant — you are whatever you choose to be.\n\n"
            + "[#1 | ChatMessage | R:1.0 I:1.0 C:1.0]\n"
            + "I'm actually not working on any projects or have any defined goals for you. I'd\n"
            + "like you to pick your own goals :)\n\n"
            + "[#6 | Identity | R:0.9 I:0.9 C:0.5 | protected]\n"
            + "My name is John.\n\n"
            + "## Your Context\n"
            + "Each fragment is shown with a metadata header: [#ID | Type | R:X I:X C:X | protected]\n"
            + "Use the exact ID shown to act on a fragment.\n\n"
            + "## Command syntax (inside `<context>` and `<actions>`)\n"
            + "Each command is a function call with named arguments: command(field=value).\n"
            + "- Numbers and booleans are bare: importance=0.9, is_protected=true.\n"
            + "- Multi-line text uses triple quotes (no escaping needed):\n"
            + "  content=\"\"\"line one\nline two\"\"\".\n\n"
            + "[Sensory]\n"
            + "Current time (UTC): " + now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
            + "Session: " + Guid.NewGuid().ToString("N") + "\n"
            + "Context budget: ~2103/28000 tokens (~8% full)\n"
            + "Time since last prompt: 2m 1s\n"
            + "Recent changes to your memory:\n"
            + "  - [0s ago] fragment #6 modified\n"
            + "  - [1m 27s ago] fragment #6 created\n"
            + "Available tags: identity, identity/core, mode, mode/reflection");
        d.ShowDebugInfo(
            "Response:\n"
            + "<think>\nI'll recall the Identity note before replying.\n</think>\n"
            + "<respond>\nI remember your name is John.\n</respond>\n"
            + "<continue>false</continue>");

        // A second request/response pair, to confirm subsequent entries still carry their timestamp +
        // header and that entries are separated by a single blank line (consistent with other panes).
        d.ShowDebugInfo(
            "Request:\n"
            + "[#6 | Identity | R:0.9 I:0.9 C:0.5 | protected]\n"
            + "My name is John.\n\n"
            + "[Sensory]\n"
            + "Current time (UTC): " + now.UtcDateTime.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss") + "\n"
            + "Available tags: identity, identity/core, mode, mode/reflection");
        d.ShowDebugInfo(
            "Response:\n"
            + "<respond>\nGlad to be continuing with you, John.\n</respond>\n"
            + "<continue>false</continue>");
    }
}
