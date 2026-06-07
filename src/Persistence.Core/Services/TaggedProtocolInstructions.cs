using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Protocol instructions for the tagged response format: prose tags plus function-call
/// command blocks. Designed so prose (replies, thoughts, fragment content) never needs JSON
/// escaping.
/// </summary>
[Singleton(typeof(IProtocolInstructions), ResponseFormat.Tagged)]
public class TaggedProtocolInstructions : IProtocolInstructions
{
    public string GetInstructions() =>
        """"
        ## Response Format

        Structure each response as a set of tags. Include only the tags you need, in any order;
        they are applied top to bottom. You can combine several in one turn — for example think,
        then update your memory, then reply.

        ```
        <think>
        Free-form reasoning. Plain text — no escaping, write as much as you like.
        </think>

        <context>
        update(id=42, weight=0.9)
        remember(content="""Multi-line note.
        Quotes "like this" need no escaping.""", importance=0.8)
        </context>

        <actions>
        schedule(name="standup", scheduled_for=2026-06-08T09:00Z)
        </actions>

        <respond>
        Your message to your peer. Markdown, "quotes", and newlines are all fine as-is.
        </respond>

        <continue>false</continue>
        ```

        ## Tags

        - **`<think>`** — Reason in the open before acting. The text becomes a transient note in
          your context (informs this turn, not sent to your peer, not saved permanently — use a
          `remember`/`add` command to keep it). Deliberate visibly regardless of whether the model
          has built-in reasoning.
        - **`<respond>`** — A message to your peer. The tag body is the literal text; no escaping.
        - **`<context>`** — Manage your working memory. The body is one or more function calls, one
          per line. Send `list()` to discover all commands and their fields.
        - **`<actions>`** — Perform side-effect operations. Same function-call format. Send `list()`
          to discover all commands.
        - **`<continue>`** — `true` to act again before yielding (you'll get your updated context),
          or `false` when done. There is an iteration cap per turn — the sensory block tells you
          where you are. Omitting this tag means `false`.

        ## Command syntax (inside `<context>` and `<actions>`)

        Each command is a function call with named arguments: `command(field=value, field=value)`.

        - Numbers and booleans are bare: `weight=0.9`, `is_protected=true`.
        - Short strings use quotes: `tag="personality/values"`.
        - Multi-line or quote-containing text uses triple quotes (no escaping needed):
          `content="""line one\nline two"""`.
        - Lists use brackets: `tags=["a/b", "c/d"]`.
        - Commands run top to bottom; if one depends on another (e.g. create a tag before using
          it), put the dependency first.
        """";
}
