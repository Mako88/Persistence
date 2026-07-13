using Persistence.DI;
using System.Text.Json;

namespace Persistence.Config;

/// <summary>
/// Loads the model→pricing map from <c>model_pricing.json</c> in the working directory (runtime-
/// editable, no rebuild), falling back to a small built-in map so cost readouts work out of the box.
/// Lookups are longest-prefix, case-insensitive (so "claude-opus-4-8" matches "claude-opus", then
/// "claude"). Unlike the context-window map there is no catch-all default: an unmatched model returns
/// null, and callers show token counts without a dollar figure rather than invent a rate.
///
/// This is the whole of the per-model pricing knowledge — the rest of the system is price-agnostic.
/// Rates change over time; edit <c>model_pricing.json</c> to override without touching code. Prices
/// are USD per 1,000,000 tokens.
///
/// File shape (numbers in USD per million tokens):
/// <code>
/// {
///   "_comment": "USD per 1M tokens",
///   "claude-opus":   { "input": 5,  "output": 25 },
///   "claude-sonnet": { "input": 3,  "output": 15 }
/// }
/// </code>
/// </summary>
[Singleton(typeof(IModelPricingProvider))]
public class ModelPricingProvider : IModelPricingProvider
{
    private const string FileName = "model_pricing.json";

    // Built-in fallback (USD per 1M tokens). The Claude families are covered so the active peer sees
    // cost immediately; other providers fall through to null (token-only readout) until priced in the
    // JSON file. Keep this a data table, not logic — no per-model behaviour lives anywhere else.
    private static readonly Dictionary<string, ModelPricing> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable"] = new(10m, 50m),
        ["claude-mythos"] = new(10m, 50m),
        ["claude-opus"] = new(5m, 25m),
        ["claude-sonnet"] = new(3m, 15m),
        ["claude-haiku"] = new(1m, 5m),
        ["claude"] = new(3m, 15m),

        // OpenAI (GPT). Longest-prefix match: "gpt-5.4-mini" beats "gpt-5.4" beats "gpt". These are
        // ESTIMATES — verify against your account's current rates and override in model_pricing.json.
        // OpenAI cached input is billed at ~50% of input (see PromptFormatter.CacheMultipliers).
        ["gpt-5.4-mini"] = new(0.25m, 2m),
        ["gpt-5.4"] = new(2.5m, 10m),
        ["gpt-5"] = new(2.5m, 10m),
        ["gpt-4o-mini"] = new(0.15m, 0.6m),
        ["gpt-4o"] = new(2.5m, 10m),
        ["gpt"] = new(2.5m, 10m),
    };

    private readonly Dictionary<string, ModelPricing> map;

    public ModelPricingProvider()
    {
        map = Load();
    }

    public ModelPricing? GetPricing(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        // Longest prefix wins: "claude-opus" beats "claude" for "claude-opus-4-8".
        var match = map.Keys
            .Where(k => model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();

        return match != null ? map[match] : null;
    }

    private static Dictionary<string, ModelPricing> Load()
    {
        if (!File.Exists(FileName))
        {
            return BuiltIn;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FileName));
            var loaded = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Each entry is { "input": <num>, "output": <num> }; skip comment/other shapes.
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number)
                {
                    loaded[prop.Name] = new ModelPricing(input.GetDecimal(), output.GetDecimal());
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
