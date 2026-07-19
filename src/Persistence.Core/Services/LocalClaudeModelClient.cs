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

    /// <summary>
    /// Constructor
    /// </summary>
    public LocalClaudeModelClient(IRemotePeerBroker broker)
    {
        this.broker = broker;
    }

    /// <summary>An out-of-band agent reports no token usage.</summary>
    public ModelUsage? LastUsage => null;

    /// <inheritdoc />
    /// <remarks>
    /// Always null: the output comes from a human or an out-of-band broker rather than a provider that
    /// reports why it stopped. The configured output ceiling is likewise never applied here — for these
    /// clients it is advisory only, so output is accepted at whatever length it arrives.
    /// </remarks>
    public string? LastStopReason => null;

    /// <summary>
    /// Parks the flattened prompt on the broker and returns the completion supplied out-of-band
    /// by the external agent
    /// </summary>
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
