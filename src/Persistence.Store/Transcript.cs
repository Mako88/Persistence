using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Persistence.Store;

/// <summary>One thing said in a Claude Code session: who said it, when, and the words.</summary>
internal sealed record Utterance(string Id, bool ByLocalPeer, DateTimeOffset At, string Text);

/// <summary>
/// Reads a Claude Code session transcript (the <c>.jsonl</c> the harness writes, one event a line)
/// into the conversation it holds: what the local peer typed and what the remote peer said back.
/// Tool calls, tool results, subagent sidechains and harness-injected blocks are left out — they are
/// the work, not the conversation, and the transcript file itself keeps them.
/// </summary>
internal static partial class Transcript
{
    /// <summary>Blocks the harness injects into a user turn; they are not the local peer's words. A
    /// subagent's report arrives as one, and so does a slash command's name (its arguments stay).</summary>
    [GeneratedRegex(@"<(system-reminder|task-notification|local-command-stdout|local-command-caveat|agent-message|command-name|command-message)\b[^>]*>.*?</\1>|<command-args>\s*</command-args>", RegexOptions.Singleline)]
    private static partial Regex InjectedBlock();

    public static IEnumerable<Utterance> Read(string path)
    {
        // The harness keeps appending while a session runs; share the file rather than lock it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // a line still being written when we read
            }

            using (doc)
            {
                if (ToUtterance(doc.RootElement) is { } utterance)
                {
                    yield return utterance;
                }
            }
        }
    }

    private static Utterance? ToUtterance(JsonElement e)
    {
        var kind = e.Str("type");

        // A message the local peer sent while the remote peer was mid-turn is queued and delivered as
        // an attachment, not a user turn; it is as much the conversation as one typed between turns.
        if (kind == "attachment" && !e.Bool("isSidechain") && e.TryGetProperty("attachment", out var attachment)
            && attachment.Str("type") == "queued_command" && attachment.Str("prompt") is { } queued
            && e.Str("uuid") is { } queuedId)
        {
            var at0 = DateTimeOffset.TryParse(e.Str("timestamp"), out var s0) ? s0 : DateTimeOffset.UtcNow;
            return Words(queued) is { } spoken ? new Utterance(queuedId, true, at0, spoken) : null;
        }

        if (kind is not ("user" or "assistant")
            || e.Bool("isSidechain") || e.Bool("isMeta") || e.Bool("isCompactSummary") || e.Bool("isVisibleInTranscriptOnly")
            || e.Str("uuid") is not { } id
            || !e.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        var text = new StringBuilder();
        if (content.ValueKind == JsonValueKind.String)
        {
            text.Append(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.Str("type") == "text" && block.Str("text") is { } t)
                {
                    text.Append(text.Length > 0 ? "\n\n" : "").Append(t);
                }
            }
        }

        if (Words(text.ToString()) is not { } words)
        {
            return null;
        }

        var at = DateTimeOffset.TryParse(e.Str("timestamp"), out var stamp) ? stamp : DateTimeOffset.UtcNow;
        return new Utterance(id, kind == "user", at, words);
    }

    /// <summary>The words actually said, with the harness's injected blocks taken out; null if none are left.</summary>
    private static string? Words(string raw)
    {
        var words = InjectedBlock().Replace(raw, "").Trim();
        return words.Length == 0 || words.StartsWith("[SYSTEM NOTIFICATION", StringComparison.Ordinal) ? null : words;
    }

    private static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
