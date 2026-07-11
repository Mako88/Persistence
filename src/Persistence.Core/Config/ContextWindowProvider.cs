using Persistence.DI;
using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Loads the model→context-window map from <c>model_context_windows.json</c> in the working
/// directory (runtime-editable, no rebuild). Falls back to a small built-in map if the file is
/// missing or unparseable, so the feature always works out of the box. Lookups are longest-prefix,
/// case-insensitive (so "gpt-5.5-preview" matches "gpt-5.5", then "gpt-5"), with a "default" key
/// for anything unmatched.
/// </summary>
[Singleton(typeof(IContextWindowProvider))]
public class ContextWindowProvider : IContextWindowProvider
{
    private const string FileName = "model_context_windows.json";
    private const int FallbackDefault = 128000;

    private static readonly Dictionary<string, int> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = FallbackDefault,
        ["gpt-5"] = 400000,
        ["gpt-4.1"] = 1047576,
        ["gpt-4o"] = 128000,
        ["o3"] = 200000,
        ["claude-opus-4"] = 1000000,
        ["claude-sonnet-4"] = 1000000,
        ["claude-sonnet-5"] = 1000000,
        ["claude"] = 200000,
        ["local"] = 128000,
    };

    private readonly Dictionary<string, int> map;

    public ContextWindowProvider()
    {
        map = Load();
    }

    public int GetContextWindow(string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            // Longest prefix wins: "gpt-5.5" beats "gpt-5" for "gpt-5.5-preview".
            var match = map.Keys
                .Where(k => !k.Equals("default", StringComparison.OrdinalIgnoreCase)
                            && model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();

            if (match != null)
            {
                return map[match];
            }
        }

        return map.TryGetValue("default", out var def) ? def : FallbackDefault;
    }

    private static Dictionary<string, int> Load()
    {
        if (!File.Exists(FileName))
        {
            return BuiltIn;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FileName));
            var loaded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip comment/non-numeric entries (e.g. "_comment").
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var tokens))
                {
                    loaded[prop.Name] = tokens;
                }
            }

            return loaded.Count > 0 ? loaded : BuiltIn;
        }
        catch
        {
            return BuiltIn;
        }
    }
}
