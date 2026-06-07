using System.Text.Json;
using System.Text.Json.Nodes;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Parses JSON model output (<c>{ action, continue, data }</c>) into a single-action
/// <see cref="ModelTurn"/>. Falls back to a plain-text <see cref="ModelAction.RespondToUser"/>
/// turn when parsing fails.
/// </summary>
[Singleton(typeof(IModelResponseParser), ResponseFormat.Json)]
public class ModelResponseParser : IModelResponseParser
{
    /// <summary>
    /// Parses the raw model output into a single-action <see cref="ModelTurn"/>. Returns a
    /// <see cref="ModelAction.RespondToUser"/> turn if the output is not valid JSON.
    /// </summary>
    public ModelTurn Parse(string rawOutput)
    {
        try
        {
            var json = JsonNode.Parse(rawOutput);

            if (json == null)
            {
                return Fallback(rawOutput);
            }

            var actionStr = json["action"]?.GetValue<string>()?.Replace("_", "");

            if (!Enum.TryParse<ModelAction>(actionStr, ignoreCase: true, out var action))
            {
                return Fallback(rawOutput);
            }

            var response = new ModelResponse
            {
                Action = action,
                Data = json["data"],
            };

            return new ModelTurn
            {
                Actions = [response],
                Continue = json["continue"]?.GetValue<bool>() ?? false,
                ParsedSuccessfully = true,
            };
        }
        catch
        {
            return Fallback(rawOutput);
        }
    }

    /// <summary>
    /// Creates a plain-text response turn when JSON parsing fails
    /// </summary>
    private static ModelTurn Fallback(string rawOutput) => new()
    {
        Actions = [new ModelResponse { Action = ModelAction.RespondToUser, Data = JsonValue.Create(rawOutput) }],
        Continue = false,
        ParsedSuccessfully = false,
    };
}
