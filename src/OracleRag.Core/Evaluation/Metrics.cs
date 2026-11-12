namespace OracleRag.Evaluation;

/// <summary>Retrieval metrics. All take ranked id lists; the first element is rank 1.</summary>
public static class Metrics
{
    /// <summary>1 when <paramref name="relevant"/> appears in the first <paramref name="k"/> results, else 0.</summary>
    public static double HitAtK(IReadOnlyList<string> ranked, string relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        for (var i = 0; i < Math.Min(k, ranked.Count); i++)
        {
            if (string.Equals(ranked[i], relevant, StringComparison.Ordinal))
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>1/rank of the relevant id, 0 when it is absent.</summary>
    public static double ReciprocalRank(IReadOnlyList<string> ranked, string relevant)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        for (var i = 0; i < ranked.Count; i++)
        {
            if (string.Equals(ranked[i], relevant, StringComparison.Ordinal))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    /// <summary>
    /// ANN recall@k: the share of the exact top-k that the approximate search also returned (order ignored). This is
    /// the number vector-index benchmarks report; it measures the index, not the embedding model.
    /// </summary>
    public static double OverlapRecall(IReadOnlyList<string> approximate, IReadOnlyList<string> exact, int k)
    {
        ArgumentNullException.ThrowIfNull(approximate);
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var truth = exact.Take(k).ToHashSet(StringComparer.Ordinal);
        if (truth.Count == 0)
        {
            return 1;
        }

        return approximate.Take(k).Count(truth.Contains) / (double)truth.Count;
    }

    /// <summary>Nearest-rank percentile (p in 0..100) of an unsorted sample; the sample is not modified.</summary>
    public static double Percentile(IReadOnlyCollection<double> sample, double p)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Count == 0)
        {
            throw new ArgumentException("Sample is empty.", nameof(sample));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(p, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 100);
        var sorted = sample.Order().ToArray();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Median as the mean of the two middle values for even samples.</summary>
    public static double Median(IReadOnlyCollection<double> sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Count == 0)
        {
            throw new ArgumentException("Sample is empty.", nameof(sample));
        }

        var sorted = sample.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
