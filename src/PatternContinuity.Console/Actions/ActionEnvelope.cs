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

public class ActionRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
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
