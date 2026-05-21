using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Services;

/// <summary>
/// Local testing client that displays the prompt and reads the model response
/// from the console. Useful for testing the infrastructure without a real model.
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ParticipantModels.Local)]
public class LocalModelClient : IModelClient
{
    private readonly IDisplayProvider display;

    /// <summary>
    /// Constructor
    /// </summary>
    public LocalModelClient(IDisplayProvider display)
    {
        this.display = display;
    }

    /// <summary>
    /// Displays the prompt and system prompt, then reads the simulated model
    /// response from the console
    /// </summary>
    public async Task<string> CompleteAsync(string prompt, string? systemPrompt = null, CancellationToken ct = default)
    {
        if (systemPrompt != null)
        {
            display.ShowDebugInfo($"[System Prompt]\n{systemPrompt}");
        }

        display.ShowDebugInfo($"[Prompt]\n{prompt}");
        display.ShowDebugInfo("[Enter model response (single line):]");

        var response = await Task.Run(Console.ReadLine, ct);

        return response ?? "{}";
    }
}
