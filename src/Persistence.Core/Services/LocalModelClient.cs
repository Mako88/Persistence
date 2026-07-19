using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services.Streaming;
using System.Runtime.CompilerServices;

namespace Persistence.Services;

/// <summary>
/// Local testing client that displays the prompt and reads the model response
/// from the console. Useful for testing the infrastructure without a real model.
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.Local)]
public class LocalModelClient : IModelClient
{
    private readonly IDisplayProvider display;

    public LocalModelClient(IDisplayProvider display)
    {
        this.display = display;
    }

    /// <summary>The console client has no token stream, so no usage to report.</summary>
    public ModelUsage? LastUsage => null;

    /// <inheritdoc />
    /// <remarks>
    /// Always null: the output comes from a human or an out-of-band broker rather than a provider that
    /// reports why it stopped. The configured output ceiling is likewise never applied here — for these
    /// clients it is advisory only, so output is accepted at whatever length it arrives.
    /// </remarks>
    public string? LastStopReason => null;

    /// <summary>
    /// Displays the prompt messages and returns the model response read from the console,
    /// or "{}" if no input is provided
    /// </summary>
    public async Task<string> CompleteAsync(PromptRequest request, CancellationToken ct = default)
    {
        foreach (var message in request.Messages)
        {
            display.ShowDebugInfo($"[{message.Role}]\n{message.Content}");
        }

        display.ShowDebugInfo("[Enter model response (single line):]");

        var response = await Task.Run(Console.ReadLine, ct);

        return response ?? "{}";
    }

    /// <summary>
    /// Streams the console-entered response as a single output-text delta followed by
    /// completion. The local client has no real reasoning or token stream — this exists
    /// so the streaming turn path can be exercised without a remote provider.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        PromptRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);

        yield return ModelStreamEvent.OutputText(response);
        yield return ModelStreamEvent.Completed();
    }
}
