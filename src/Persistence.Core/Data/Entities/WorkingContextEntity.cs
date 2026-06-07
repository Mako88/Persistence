using Dapper.Contrib.Extensions;
using System.Text.Json.Serialization;

namespace Persistence.Data.Entities;


[Table("WorkingContexts")]
public record WorkingContextEntity : BaseEntity
{
    public required string Name { get; set; }

    public required string Summary { get; set; }

    [Computed]
    [JsonIgnore]
    public SortedList<int, WeightedContextFragment> ContextFragments { get; set; } = [];

    /// <summary>
    /// Wraps an existing <see cref="ContextFragmentEntity"/> as a
    /// <see cref="WeightedContextFragment"/> and adds it to this context with the
    /// given weight. Preserves the original entity's ID, tracking state, and all
    /// properties. See <see cref="AddFragment(WeightedContextFragment, long?)"/>
    /// for ordering behaviour.
    /// </summary>
    public void AddFragment(ContextFragmentEntity fragment, float relevance = 1.0f, long? insertAfter = null)
    {
        AddFragment(new WeightedContextFragment
        {
            Id = fragment.Id,
            FragmentType = fragment.FragmentType,
            Status = fragment.Status,
            Content = fragment.Content,
            Summary = fragment.Summary,
            Notes = fragment.Notes,
            Importance = fragment.Importance,
            Confidence = fragment.Confidence,
            IsProtected = fragment.IsProtected,
            IsDeleted = fragment.IsDeleted,
            Sources = fragment.Sources,
            Tags = fragment.Tags,
            Relevance = relevance,
            CreatedUtc = fragment.CreatedUtc,
            LastModifiedUtc = fragment.LastModifiedUtc,
            LastAccessedUtc = fragment.LastAccessedUtc,
            IsNew = fragment.IsNew,
            OriginalState = fragment.OriginalState,
        }, insertAfter);
    }

    /// <summary>
    /// Adds a fragment to this context. When <paramref name="insertAfter"/> is null,
    /// the fragment is appended at the end. When a fragment ID is provided, the new
    /// fragment is inserted immediately after it and all subsequent orders are shifted.
    /// Sets <see cref="WeightedContextFragment.Order"/> on the fragment automatically.
    /// </summary>
    public void AddFragment(WeightedContextFragment fragment, long? insertAfter = null)
    {
        if (insertAfter == null)
        {
            var nextOrder = ContextFragments.Count > 0
                ? ContextFragments.Keys.Max() + 1
                : 0;

            fragment.Order = nextOrder;
            ContextFragments[nextOrder] = fragment;
            return;
        }

        var target = ContextFragments.Values
            .FirstOrDefault(f => f.Id == insertAfter.Value)
            ?? throw new InvalidOperationException(
                $"Fragment {insertAfter.Value} not found in context");

        var insertOrder = target.Order + 1;

        // Shift fragments at or after the insertion point (iterate in reverse
        // so we don't overwrite entries we haven't moved yet)
        var toShift = ContextFragments
            .Where(kvp => kvp.Key >= insertOrder)
            .OrderByDescending(kvp => kvp.Key)
            .ToList();

        foreach (var (order, f) in toShift)
        {
            ContextFragments.Remove(order);
            f.Order = order + 1;
            ContextFragments[order + 1] = f;
        }

        fragment.Order = insertOrder;
        ContextFragments[insertOrder] = fragment;
    }
}

/// <summary>
/// A <see cref="ContextFragmentEntity"/> decorated with its relevance and position
/// within a <see cref="WorkingContextEntity"/>. Relevance and Order are junction-table
/// properties — they describe the relationship, not the fragment itself.
/// </summary>
public record WeightedContextFragment : ContextFragmentEntity
{
    /// <summary>
    /// How relevant this fragment is to the current prompt (0.0–1.0). Higher values rank it
    /// more strongly for inclusion when context is tight.
    /// </summary>
    [JsonIgnore]
    public required float Relevance { get; set; }

    /// <summary>
    /// Position of this fragment within the context. Set automatically by
    /// <see cref="WorkingContextEntity.AddFragment"/> — callers do not need to
    /// provide a value.
    /// </summary>
    [JsonIgnore]
    public int Order { get; set; }
}
