namespace Persistence.Config;

/// <summary>
/// Resolves a model's token pricing from a runtime-editable map, so a running cost can be shown
/// without hardcoding per-model rates in code. Model-agnostic: the code has no per-model branches,
/// only a data lookup (see <see cref="ModelPricingProvider"/>).
/// </summary>
public interface IModelPricingProvider
{
    /// <summary>
    /// Pricing for <paramref name="model"/> by longest case-insensitive prefix match, or null when
    /// no rate is known (so callers can show token counts without a dollar figure rather than guess).
    /// </summary>
    ModelPricing? GetPricing(string model);
}
