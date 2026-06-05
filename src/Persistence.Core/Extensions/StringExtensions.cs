using System.Diagnostics.CodeAnalysis;

namespace Persistence.Extensions;

/// <summary>
/// Extensions for strings
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Whether or not this string has a value (returns false for whitespace-only strings)
    /// </summary>
    public static bool HasValue([NotNullWhen(true)] this string? value) =>
        !string.IsNullOrWhiteSpace(value);
}
