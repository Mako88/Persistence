using Persistence.Services;

namespace Persistence.Store;

/// <summary>
/// The embedding client, remembering the vectors of the texts it embedded last. One recall embeds the
/// same message up to four times (each round over the fragments, then the conversation), each a call to
/// the embedding server; remembered, the message is embedded once.
/// </summary>
internal sealed class RememberingEmbeddingClient(IEmbeddingClient inner) : IEmbeddingClient
{
    private const int Kept = 256;
    private readonly Dictionary<string, float[]> remembered = [];
    private readonly Queue<string> order = new();

    public bool IsEnabled => inner.IsEnabled;

    public string Model => inner.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var missing = texts.Where(t => !remembered.ContainsKey(t)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var embedded = await inner.EmbedAsync(missing, ct);
            if (embedded.Count != missing.Count)
            {
                return []; // a failed call is the callers' to handle, as it was without the memory
            }

            for (var i = 0; i < missing.Count; i++)
            {
                remembered[missing[i]] = embedded[i];
                order.Enqueue(missing[i]);
                if (order.Count > Kept)
                {
                    remembered.Remove(order.Dequeue());
                }
            }
        }

        return texts.Select(t => remembered[t]).ToList();
    }
}
