using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatternContinuity.Actions;

public class ActionEnvelope
{
    [JsonPropertyName("assistant_reply")]
    public string AssistantReply { get; set; } = "";

    [JsonPropertyName("actions")]
    public List<ActionRequest> Actions { get; set; } = [];
}

/// <summary>
/// Represents a single action request from the model.
/// Uses a custom converter to handle multiple conventions:
/// the model may use "action", "type", or "action_type" as the key.
/// </summary>
[JsonConverter(typeof(ActionRequestConverter))]
public class ActionRequest
{
    public string Action { get; set; } = "";
    public JsonElement Payload { get; set; }
}

public class ActionRequestConverter : JsonConverter<ActionRequest>
{
    public override ActionRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Try multiple key names for the action identifier
        var action = TryGetString(root, "action")
            ?? TryGetString(root, "type")
            ?? TryGetString(root, "action_type")
            ?? "";

        // Try to get a nested payload object; if not present, treat the whole object as payload
        JsonElement payload;
        if (root.TryGetProperty("payload", out var p))
        {
            payload = p.Clone();
        }
        else
        {
            // Everything except the action key IS the payload
            payload = root.Clone();
        }

        return new ActionRequest { Action = action, Payload = payload };
    }

    public override void Write(Utf8JsonWriter writer, ActionRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("action", value.Action);
        writer.WritePropertyName("payload");
        value.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
}

public class ActionResult
{
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
    public string? TargetEntryId { get; set; }
    public string Summary { get; set; } = "";
    public string? ErrorText { get; set; }

    public static ActionResult Success(string action, string summary, string? targetId = null) =>
        new() { Action = action, Status = "executed", Summary = summary, TargetEntryId = targetId };

    public static ActionResult Error(string action, string error) =>
        new() { Action = action, Status = "failed", ErrorText = error, Summary = $"Failed: {error}" };

    public static ActionResult Proposed(string action, string summary, string? targetId = null) =>
        new() { Action = action, Status = "proposed", Summary = summary, TargetEntryId = targetId };
}
