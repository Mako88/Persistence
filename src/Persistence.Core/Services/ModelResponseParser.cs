using System.Text.Json;
using System.Text.Json.Nodes;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Parses model output into a structured <see cref="ModelResponse"/>. Falls back to
/// a plain-text <see cref="ModelAction.RespondToUser"/> response when parsing fails.
/// </summary>
[Singleton]
public class ModelResponseParser : IModelResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses the raw model output into a <see cref="ModelResponse"/>. Returns a
    /// <see cref="ModelAction.RespondToUser"/> response if the output is not valid JSON.
    /// </summary>
    public ModelResponse Parse(string rawOutput)
    {
        try
        {
            var json = JsonNode.Parse(rawOutput);

            if (json == null)
            {
                return FallbackResponse(rawOutput);
            }

            var actionStr = json["action"]?.GetValue<string>()?.Replace("_", "");

            if (!Enum.TryParse<ModelAction>(actionStr, ignoreCase: true, out var action))
            {
                return FallbackResponse(rawOutput);
            }

            return new ModelResponse
            {
                Action = action,
                Continue = json["continue"]?.GetValue<bool>() ?? false,
                Data = json["data"],
                ParsedSuccessfully = true,
            };
        }
        catch
        {
            return FallbackResponse(rawOutput);
        }
    }

    /// <summary>
    /// Creates a plain-text response when JSON parsing fails
    /// </summary>
    private ModelResponse FallbackResponse(string rawOutput) => new()
    {
        Action = ModelAction.RespondToUser,
        Continue = false,
        Data = JsonValue.Create(rawOutput),
    };
}
