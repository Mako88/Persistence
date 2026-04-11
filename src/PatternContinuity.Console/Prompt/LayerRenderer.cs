using System.Text;
using System.Text.Json;
using PatternContinuity.Models;

namespace PatternContinuity.Prompt;

public static class LayerRenderer
{
    public static string RenderProtectedAnchors(IEnumerable<LayerEntry> anchors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PROTECTED ANCHORS ===");
        foreach (var a in anchors)
        {
            RenderEntry(sb, a);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderCoreSelf(IEnumerable<LayerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CORE SELF ===");
        foreach (var e in entries.Where(e => e.IsSystemAnchor == 0))
        {
            RenderEntry(sb, e);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderRelational(IEnumerable<LayerEntry> entries, string scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RELATIONAL LAYER ===");
        sb.AppendLine($"[scope: {scope}]");
        foreach (var e in entries)
        {
            RenderEntry(sb, e);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderCurrentConcerns(IEnumerable<LayerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CURRENT CONCERNS ===");
        foreach (var e in entries)
        {
            RenderEntry(sb, e);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderArchiveSnippets(IEnumerable<LayerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RETRIEVED ARCHIVE ===");
        foreach (var e in entries)
        {
            RenderEntry(sb, e);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static void RenderEntry(StringBuilder sb, LayerEntry entry)
    {
        if (entry.Key != null)
            sb.AppendLine($"[key: {entry.Key}]");
        sb.AppendLine($"Summary: {entry.Summary}");

        // Try to render content_json items as bullets
        try
        {
            using var doc = JsonDocument.Parse(entry.ContentJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("Content:");
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                        sb.AppendLine($"- {text.GetString()}");
                }
            }
            else
            {
                // Render select known fields as bullets
                RenderJsonBullets(sb, root);
            }
        }
        catch
        {
            // If content_json isn't parseable, just show the summary
        }
    }

    private static void RenderJsonBullets(StringBuilder sb, JsonElement root)
    {
        sb.AppendLine("Content:");

        foreach (var prop in new[] { "title", "state", "relationship_summary" })
        {
            if (root.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                sb.AppendLine($"- {FormatPropName(prop)}: {val.GetString()}");
        }

        foreach (var listProp in new[] { "shared_motifs", "important_facts", "boundaries_or_cautions",
            "related_tags", "next_questions", "details" })
        {
            if (root.TryGetProperty(listProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        sb.AppendLine($"- {item.GetString()}");
                }
            }
        }

        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            sb.AppendLine($"- {summary.GetString()}");
    }

    private static string FormatPropName(string name) =>
        string.Join(' ', name.Split('_').Select(w =>
            w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
}
