using Persistence.Data.Entities;
using Persistence.Runtime;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for the raw-context decay selection — which fragments get archived to keep the context
/// lean. Pure (no DB): only conversation + tool results beyond the recent window are selected, and the
/// peer's authored fragments are never touched.
/// </summary>
public class RawContextDecayTests
{
    private static WeightedContextFragment Frag(long id, ContextFragmentType type) => new()
    {
        Id = id,
        FragmentType = type,
        Status = ContextFragmentStatus.Active,
        Content = $"fragment {id}",
        Relevance = 1f,
        Importance = 0.5f,
        Confidence = 0.5f,
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    };

    private static WorkingContextEntity ContextWith(params WeightedContextFragment[] fragments)
    {
        var context = new WorkingContextEntity
        {
            Id = 1,
            Name = "c",
            Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        foreach (var fragment in fragments)
        {
            context.AddFragment(fragment); // assigns Order in insertion sequence
        }

        return context;
    }

    [Fact]
    public void ArchivesTheOldestRawBeyondTheWindowAndKeepsRecent()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.ChatMessage),
            Frag(2, ContextFragmentType.ChatMessage),
            Frag(3, ContextFragmentType.ChatMessage),
            Frag(4, ContextFragmentType.ChatMessage),
            Frag(5, ContextFragmentType.ChatMessage));

        var archive = TurnHandler.SelectRawFragmentsToArchive(context, window: 3);

        Assert.Equal(new long[] { 1, 2 }, archive.Select(f => f.Id)); // the two oldest
    }

    [Fact]
    public void NeverArchivesAuthoredFragments_OnlyRaw()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.ChatMessage),
            Frag(2, ContextFragmentType.Personal),       // authored — sacred
            Frag(3, ContextFragmentType.ActionResponse), // raw (tool result)
            Frag(4, ContextFragmentType.Identity),       // authored — sacred
            Frag(5, ContextFragmentType.ChatMessage));

        // Raw fragments are #1, #3, #5; keeping the most recent 1 (#5) archives #1 and #3.
        var archive = TurnHandler.SelectRawFragmentsToArchive(context, window: 1);

        Assert.Equal(new long[] { 1, 3 }, archive.Select(f => f.Id).OrderBy(x => x));
        Assert.DoesNotContain(archive, f =>
            f.FragmentType is ContextFragmentType.Personal or ContextFragmentType.Identity);
    }

    [Fact]
    public void ArchivesNothingWhenWithinTheWindowOrDisabled()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.ChatMessage),
            Frag(2, ContextFragmentType.ChatMessage));

        Assert.Empty(TurnHandler.SelectRawFragmentsToArchive(context, window: 5)); // within window
        Assert.Empty(TurnHandler.SelectRawFragmentsToArchive(context, window: 0)); // disabled
    }
}
