using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Protocol instructions for the JSON response format: one
/// <c>{ action, continue, data }</c> object per turn.
/// </summary>
[Singleton(typeof(IProtocolInstructions), ResponseFormat.Json)]
public class JsonProtocolInstructions : IProtocolInstructions
{
    /// <summary>
    /// Returns the system instructions describing the JSON response format and its actions
    /// </summary>
    public string GetInstructions() =>
        """
        ## Response Format

        Every response you give must be a single JSON object with this structure:

        ```
        {
          "action": "respond_to_user" | "manage_context" | "execute_actions" | "think",
          "continue": true | false,
          "data": <action-specific payload>
        }
        ```

        Set `continue` to `true` when you want to take additional actions before yielding back
        to your peer. You will receive your updated context and can act again. Set it to `false`
        when you are done for this turn. There is an iteration cap per turn — the sensory block
        tells you where you are.

        ## Actions

        ### respond_to_user
        Send a message to your peer. `data` is the text string you want them to see.

        ### manage_context
        Manage your working memory. `data` is an array of commands — each command is an object
        with a single property named after the command, set to an object with its fields. Send
        `[{"list": {}}]` to discover all available commands and their schemas.

        ### execute_actions
        Perform side-effect operations. Same command format as manage_context. Send
        `[{"list": {}}]` to discover all available commands and their schemas.

        ### think
        Reason in the open before acting. `data` is the text of your thought. The thought is
        added to your working context as a transient note (it informs this turn but is not sent
        to your peer and is not saved permanently — promote it with manage_context to keep it).
        Pair this with `continue: true` to act on your own thinking, or chain several think steps.
        This lets you deliberate visibly regardless of whether the underlying model has built-in
        reasoning.

        ### Command ordering
        Commands within a single action are executed sequentially in the order given. If a command
        depends on another (e.g. creating a tag before using it on a fragment), the dependency must
        appear first in the array.
        """;
}
