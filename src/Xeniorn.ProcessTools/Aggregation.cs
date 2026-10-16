namespace Xeniorn.ProcessTools;

/// <summary>
/// Summary statistics computed over the samples of one period. Combine with <c>|</c>. The numeric values are stable
/// (they are persisted as column positions by the SQLite package, so never renumber).
/// </summary>
[Flags]
public enum Aggregation
{
    /// <summary>No aggregation (use together with raw values).</summary>
    None = 0,

    /// <summary>Smallest sample.</summary>
    Min = 1,

    /// <summary>Largest sample.</summary>
    Max = 2,

    /// <summary>Arithmetic mean.</summary>
    Mean = 4,

    /// <summary>Median (mean of the two middle samples when the count is even).</summary>
    Median = 8,

    /// <summary>Sample standard deviation (n - 1); 0 for a single sample.</summary>
    StdDev = 16,

    /// <summary>Root mean square, sqrt(mean(x squared)).</summary>
    Rms = 32,

    /// <summary>Last sample of the period: the value the process ended the period at (useful for counters and trends).</summary>
    Last = 64,

    /// <summary>Every aggregation.</summary>
    All = Min | Max | Mean | Median | StdDev | Rms | Last,
}

/// <summary>The aggregation functions, usable on any series of numbers.</summary>
public static class Aggregator
{
    /// <summary>Computes one aggregation. <paramref name="values"/> must not be empty and <paramref name="aggregation"/> must be a single flag.</summary>
    public static double Compute(ReadOnlySpan<double> values, Aggregation aggregation)
    {
        if (values.IsEmpty)
            throw new ArgumentException("At least one value is required.", nameof(values));

        switch (aggregation)
        {
            case Aggregation.Min:
                return Min(values);
            case Aggregation.Max:
                return Max(values);
            case Aggregation.Mean:
                return Mean(values);
            case Aggregation.Median:
                return Median(values);
            case Aggregation.StdDev:
                return StdDev(values);
            case Aggregation.Rms:
                return Rms(values);
            case Aggregation.Last:
                return values[^1];
            default:
                throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, "Pass exactly one aggregation.");
        }
    }

    /// <summary>Computes every aggregation set in <paramref name="aggregations"/>.</summary>
    public static IReadOnlyDictionary<Aggregation, double> ComputeAll(ReadOnlySpan<double> values, Aggregation aggregations)
    {
        var result = new Dictionary<Aggregation, double>();
        foreach (var flag in Flags(aggregations))
            result[flag] = Compute(values, flag);
        return result;
    }

    /// <summary>The individual flags set in <paramref name="aggregations"/>, in enum order.</summary>
    public static IEnumerable<Aggregation> Flags(Aggregation aggregations)
    {
        foreach (var flag in new[] { Aggregation.Min, Aggregation.Max, Aggregation.Mean, Aggregation.Median, Aggregation.StdDev, Aggregation.Rms, Aggregation.Last })
        {
            if (aggregations.HasFlag(flag))
                yield return flag;
        }
    }

    private static double Min(ReadOnlySpan<double> v)
    {
        var m = v[0];
        foreach (var x in v)
            if (x < m) m = x;
        return m;
    }

    private static double Max(ReadOnlySpan<double> v)
    {
        var m = v[0];
        foreach (var x in v)
            if (x > m) m = x;
        return m;
    }

    private static double Mean(ReadOnlySpan<double> v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x;
        return sum / v.Length;
    }

    private static double Median(ReadOnlySpan<double> v)
    {
        var sorted = v.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double StdDev(ReadOnlySpan<double> v)
    {
        if (v.Length < 2)
            return 0;
        var mean = Mean(v);
        double sumSq = 0;
        foreach (var x in v)
            sumSq += (x - mean) * (x - mean);
        return Math.Sqrt(sumSq / (v.Length - 1));
    }

    private static double Rms(ReadOnlySpan<double> v)
    {
        double sumSq = 0;
        foreach (var x in v)
            sumSq += x * x;
        return Math.Sqrt(sumSq / v.Length);
    }
}
