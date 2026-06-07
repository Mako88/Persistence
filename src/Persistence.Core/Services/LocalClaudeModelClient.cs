using Persistence.Config;
using Persistence.DI;
using Persistence.Services.Streaming;
using System.Runtime.CompilerServices;
using System.Text;

namespace Persistence.Services;

/// <summary>
/// Model client whose "model" is an external agent (e.g. Claude) supplying completions
/// out-of-band through the API, via <see cref="IRemotePeerBroker"/>. Lets a human-or-agent
/// stand in as the remote peer: the assembled prompt is parked on the broker, the agent reads
/// it and submits a response, which becomes the completion. The agent is expected to answer in
/// the configured response format (e.g. the tagged format), exactly as a model would.
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.LocalClaude)]
public class LocalClaudeModelClient : IModelClient
{
    private readonly IRemotePeerBroker broker;

    public LocalClaudeModelClient(IRemotePeerBroker broker)
    {
        this.broker = broker;
    }

    public Task<string> CompleteAsync(PromptRequest request, CancellationToken ct = default) =>
        broker.RequestCompletionAsync(Flatten(request), ct);

    /// <summary>
    /// Streams the externally-supplied response as a single output-text delta followed by
    /// completion. There is no token stream from an out-of-band agent — this exists so the
    /// streaming turn path works unchanged.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        PromptRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);

        yield return ModelStreamEvent.OutputText(response);
        yield return ModelStreamEvent.Completed();
    }

    #region Private

    /// <summary>
    /// Renders the prompt messages into a single role-labelled text block for the external agent.
    /// </summary>
    private static string Flatten(PromptRequest request)
    {
        var sb = new StringBuilder();

        foreach (var message in request.Messages)
        {
            sb.AppendLine($"[{message.Role}]");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    #endregion
}
