using System.Globalization;

namespace Persistence.Services;

/// <summary>
/// One recall's relevance floor on the meaning-based candidate list (the relevance-floor plan, Step B; scripts/recall-eval/README.md), and the record a
/// benchmark reads of it: every candidate's cosine, the best one's z-score against the whole store for this query, and
/// what the floor dropped. Ids and numbers only, never text or vectors (content-free diagnostics).
/// <para>
/// Two signals, each off unless set (<see cref="Config.EmbeddingSettings.MinCosine"/>, <see cref="Config.EmbeddingSettings.MinTopZ"/>):
/// an absolute cosine per candidate, and the top candidate's z-score, under which the whole semantic list is dropped
/// (nothing stands out from the store: a flat distribution, as an ack or small talk gives).
/// </para>
/// </summary>
public sealed record RelevanceFloor(
    int Stored,
    double Mean,
    double Sd,
    double? Top,
    double? TopZ,
    double? MinCosine,
    double? MinTopZ,
    IReadOnlyList<(long Id, double Cosine)> Candidates)
{
    /// <summary>Whether the z-score gate empties the semantic list (the top candidate doesn't stand out enough).</summary>
    public bool Emptied => MinTopZ is { } z && (TopZ is null || TopZ < z);

    /// <summary>Whether a candidate with this cosine enters fusion.</summary>
    public bool Keeps(double cosine) => !Emptied && (MinCosine is not { } c || cosine >= c);

    /// <summary>How many candidates the floor drops.</summary>
    public int Dropped => Candidates.Count(x => !Keeps(x.Cosine));

    /// <summary>
    /// The floor for one query. <paramref name="storeScores"/>: the query's cosine to every stored vector (the
    /// distribution the z-score is taken against). <paramref name="candidates"/>: the eligible candidates in rank order
    /// with their plain cosine (before any quality weight); the top is the best eligible one, not the best overall, since
    /// a standout the window already holds says nothing about what is left to show.
    /// </summary>
    public static RelevanceFloor Of(
        IReadOnlyList<double> storeScores, IReadOnlyList<(long Id, double Cosine)> candidates, double? minCosine, double? minTopZ)
    {
        var n = storeScores.Count;
        var mean = n == 0 ? 0 : storeScores.Average();
        var sd = n == 0 ? 0 : Math.Sqrt(storeScores.Sum(x => (x - mean) * (x - mean)) / n);
        double? top = candidates.Count == 0 ? null : candidates.Max(x => x.Cosine);
        double? z = top is { } t && sd > 0 ? (t - mean) / sd : null;
        return new RelevanceFloor(n, mean, sd, top, z, minCosine, minTopZ, candidates);
    }

    /// <summary>The diagnostics line: <c>recall-semantic: stored=… mean=… sd=… top=… z=… floor=… dropped=… emptied=… cands=id:cos,…</c>.</summary>
    public string Line()
    {
        static string F(double? x) => x is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "-";
        var floor = (MinCosine, MinTopZ) switch
        {
            (null, null) => "off",
            ({ } c, null) => $"cos>={F(c)}",
            (null, { } z) => $"z>={F(z)}",
            ({ } c, { } z) => $"cos>={F(c)},z>={F(z)}",
        };
        return $"recall-semantic: stored={Stored} mean={F(Mean)} sd={F(Sd)} top={F(Top)} z={F(TopZ)} floor={floor} " +
               $"dropped={Dropped} emptied={(Emptied ? "yes" : "no")} cands={string.Join(',', Candidates.Select(x => $"{x.Id}:{F(x.Cosine)}"))}";
    }
}
