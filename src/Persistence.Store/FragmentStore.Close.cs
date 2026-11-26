using Persistence.Data.Entities;

namespace Persistence.Store;

/// <summary>A split chain that can't give an assertion date: refused, never guessed.</summary>
internal sealed class ProvenanceException(string message) : Exception(message);

internal sealed partial class FragmentStore
{
    public int SplitHopLimit { get; set; } = 1000;

    public Task<ContextFragmentEntity> CloseAsync(long id, string? endedBy, string? reason) => throw new NotImplementedException();

    public Task<ContextFragmentEntity> ReopenAsync(long id, string? reason) => throw new NotImplementedException();

    public Task<DateTimeOffset> AssertionDateAsync(long id) => throw new NotImplementedException();

    public Task<string> BackfillSplitsAsync(bool apply) => throw new NotImplementedException();
}
