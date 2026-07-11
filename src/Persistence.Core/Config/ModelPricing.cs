namespace Persistence.Config;

/// <summary>
/// A model's token prices, in USD per 1,000,000 tokens. Input and output are priced separately
/// (output is typically several times input).
/// </summary>
public readonly record struct ModelPricing(decimal InputPerMillion, decimal OutputPerMillion);
