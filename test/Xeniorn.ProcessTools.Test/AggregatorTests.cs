namespace Xeniorn.ProcessTools.Test;

public class AggregatorTests
{
    private static readonly double[] Odd = [4, 1, 3, 2, 10];
    private static readonly double[] Even = [4, 1, 3, 2];

    [Fact]
    public void Min_and_max_ignore_order()
    {
        Assert.Equal(1, Aggregator.Compute(Odd, Aggregation.Min));
        Assert.Equal(10, Aggregator.Compute(Odd, Aggregation.Max));
    }

    [Fact]
    public void Mean_is_arithmetic()
    {
        Assert.Equal(4, Aggregator.Compute(Odd, Aggregation.Mean));
    }

    [Fact]
    public void Median_is_the_middle_value_or_mean_of_the_two_middle_values()
    {
        Assert.Equal(3, Aggregator.Compute(Odd, Aggregation.Median));
        Assert.Equal(2.5, Aggregator.Compute(Even, Aggregation.Median));
    }

    [Fact]
    public void Median_does_not_reorder_the_callers_data()
    {
        var data = new[] { 3.0, 1.0, 2.0 };
        Aggregator.Compute(data, Aggregation.Median);
        Assert.Equal([3.0, 1.0, 2.0], data);
    }

    [Fact]
    public void StdDev_is_the_sample_standard_deviation()
    {
        // mean 5, sum of squared deviations 32, divided by n - 1
        double[] data = [2, 4, 4, 4, 5, 5, 7, 9];
        Assert.Equal(Math.Sqrt(32.0 / 7), Aggregator.Compute(data, Aggregation.StdDev), 10);
    }

    [Fact]
    public void StdDev_of_a_single_value_is_zero()
    {
        Assert.Equal(0, Aggregator.Compute([7], Aggregation.StdDev));
    }

    [Fact]
    public void Rms_is_root_of_mean_square()
    {
        Assert.Equal(Math.Sqrt((9 + 16) / 2.0), Aggregator.Compute([3, 4], Aggregation.Rms), 10);
    }

    [Fact]
    public void Last_is_the_final_sample()
    {
        Assert.Equal(10, Aggregator.Compute(Odd, Aggregation.Last));
    }

    [Fact]
    public void ComputeAll_returns_exactly_the_requested_flags()
    {
        var result = Aggregator.ComputeAll(Odd, Aggregation.Min | Aggregation.Max | Aggregation.Median);
        Assert.Equal([Aggregation.Min, Aggregation.Max, Aggregation.Median], result.Keys);
    }

    [Fact]
    public void All_covers_every_function()
    {
        Assert.Equal(7, Aggregator.Flags(Aggregation.All).Count());
    }

    [Fact]
    public void Empty_input_and_combined_flags_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Aggregator.Compute([], Aggregation.Min));
        Assert.Throws<ArgumentOutOfRangeException>(() => Aggregator.Compute(Odd, Aggregation.Min | Aggregation.Max));
    }
}
