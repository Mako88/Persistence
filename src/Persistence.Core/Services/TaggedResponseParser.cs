using Persistence.DI;
using Persistence.Utilities;
using System.Text.RegularExpressions;

namespace Persistence.Services;

/// <summary>
/// Parses the tagged response format into a multi-action <see cref="ModelTurn"/>. Each
/// top-level tag is one action, applied in document order:
///
/// <code>
/// &lt;think&gt;free-form reasoning, unescaped&lt;/think&gt;
/// &lt;context&gt;
/// update(id=42, importance=0.9)
/// add(content="""multi-line note""", importance=0.8)
/// &lt;/context&gt;
/// &lt;actions&gt;
/// schedule(name="standup", scheduled_for=2026-06-08T09:00Z)
/// &lt;/actions&gt;
/// &lt;respond&gt;
/// Markdown reply with "quotes" and newlines, no escaping needed.
/// &lt;/respond&gt;
/// &lt;continue&gt;false&lt;/continue&gt;
/// </code>
///
/// Prose tags (<c>think</c>, <c>respond</c>) carry raw text. Command tags
/// (<c>context</c> → manage_context, <c>actions</c> → execute_actions) carry newline-separated
/// named-argument function calls, converted to the same JSON command array the handlers
/// already consume via <see cref="FunctionCallParser"/>.
/// </summary>
[Singleton(typeof(IModelResponseParser))]
public partial class TaggedResponseParser : IModelResponseParser
{
    /// <summary>
    /// Parses tagged-format output into a multi-action turn, ignoring unknown tags; returns a failed
    /// turn when the output is empty or contains no recognised tags
    /// </summary>
    public ModelTurn Parse(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return ModelTurn.Failed();
        }

        var actions = new List<ModelResponse>();
        var continueTurn = false;
        var sawKnownTag = false;

        foreach (Match m in TagRegex().Matches(rawOutput))
        {
            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var body = m.Groups["body"].Value;

            switch (tag)
            {
                case "think":
                    sawKnownTag = true;
                    actions.Add(TextAction(ModelAction.Think, body.Trim()));
                    break;

                case "respond":
                    sawKnownTag = true;
                    actions.Add(TextAction(ModelAction.RespondToUser, body.Trim()));
                    break;

                case "context":
                    sawKnownTag = true;
                    actions.Add(CommandAction(ModelAction.ManageContext, body));
                    break;

                case "actions":
                    sawKnownTag = true;
                    actions.Add(CommandAction(ModelAction.ExecuteActions, body));
                    break;

                case "continue":
                    sawKnownTag = true;
                    continueTurn = body.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;

                // Unknown tags are ignored — lets models wrap output in extra markup
                // without breaking the parse.
            }
        }

        // No recognised tags at all → treat the whole output as a plain reply, but mark it
        // unparsed so the turn handler can re-prompt for proper structure.
        if (!sawKnownTag)
        {
            return ModelTurn.Failed();
        }

        return new ModelTurn
        {
            Actions = actions,
            Continue = continueTurn,
            ParsedSuccessfully = true,
        };
    }

    /// <summary>
    /// Builds a model response carrying raw text as the action's data
    /// </summary>
    private static ModelResponse TextAction(ModelAction action, string text) =>
        new() { Action = action, Data = System.Text.Json.Nodes.JsonValue.Create(text) };

    /// <summary>
    /// Builds a model response whose data is the JSON command array parsed from the tag body
    /// </summary>
    private static ModelResponse CommandAction(ModelAction action, string body) =>
        new() { Action = action, Data = FunctionCallParser.Parse(body) };

    /// <summary>
    /// Returns the compiled regex that matches each top-level tag and its body
    /// </summary>
    // Matches <tag>...</tag> for any tag name; DOTALL so bodies span newlines. Non-greedy
    // body so adjacent tags don't merge.
    [GeneratedRegex(@"<(?<tag>\w+)\s*>(?<body>.*?)</\k<tag>\s*>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
