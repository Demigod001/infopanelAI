using InfoPanel.ConnectorHealth;

namespace InfoPanel.Tests;

public class ConnectorHealthTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Roughly the spread in the user's WireView readings: shares from about 15.9% to 17.6%.
    private static readonly double[] TypicalShares = [0.167, 0.160, 0.170, 0.163, 0.159, 0.181];

    private static PinSample Sample(DateTime t, double total, double[] shares, double? tempOut = 45) =>
        new(t, shares.Select(s => s * total).ToArray(), 40, tempOut);

    private static MinuteSummary Minute(DateTime t, double[] shares, double total = 30) => new()
    {
        MinuteUtc = t,
        Samples = 60,
        AvgTotalCurrent = total,
        Shares = (double[])shares.Clone(),
        MaxPinCurrents = shares.Select(s => s * total).ToArray()
    };

    [Fact]
    public void Light_load_samples_are_ignored()
    {
        var idle = Sample(Start, 1.5, TypicalShares);
        Assert.False(ConnectorHealthAnalyzer.IsLoadSample(idle));

        var acc = new MinuteAccumulator();
        acc.Add(idle);
        Assert.Null(acc.Flush());
    }

    [Fact]
    public void Imbalance_only_counts_when_average_pin_current_is_at_least_6A()
    {
        double[] uneven = [0.10, 0.18, 0.18, 0.18, 0.18, 0.18];

        // 30 A total = 5 A per pin: below Thermal Grizzly's 6 A level, so not assessed.
        Assert.Null(ConnectorHealthAnalyzer.Imbalance(Sample(Start, 30, uneven)));

        // 48 A total = 8 A per pin: (0.18 - 0.10) / (1/6) = 48%.
        var imbalance = ConnectorHealthAnalyzer.Imbalance(Sample(Start, 48, uneven));
        Assert.NotNull(imbalance);
        Assert.Equal(0.48, imbalance!.Value, 3);
    }

    [Fact]
    public void Accumulator_emits_previous_minute_with_mean_shares()
    {
        var acc = new MinuteAccumulator();
        for (int s = 0; s < 60; s++)
            Assert.Null(acc.Add(Sample(Start.AddSeconds(s), 30, TypicalShares)));

        var finished = acc.Add(Sample(Start.AddMinutes(1), 30, TypicalShares));

        Assert.NotNull(finished);
        Assert.Equal(Start, finished!.MinuteUtc);
        Assert.Equal(60, finished.Samples);
        Assert.Equal(30, finished.AvgTotalCurrent, 6);
        for (int i = 0; i < 6; i++)
            Assert.Equal(TypicalShares[i] / TypicalShares.Sum(), finished.Shares[i], 6);
    }

    [Fact]
    public void Csv_round_trips()
    {
        var original = new MinuteSummary
        {
            MinuteUtc = Start,
            Samples = 57,
            AvgTotalCurrent = 41.25,
            Shares = [0.17, 0.16, 0.17, 0.16, 0.16, 0.18],
            MaxPinCurrents = [8.5, 7.9, 8.7, 8.0, 7.9, 9.0],
            MaxImbalance = 0.14,
            MaxTempIn = 40.1,
            MaxTempOut = 53.1
        };

        Assert.True(MinuteSummary.TryParseCsv(original.ToCsv(), out var parsed));
        Assert.Equal(original.MinuteUtc, parsed!.MinuteUtc);
        Assert.Equal(original.Samples, parsed.Samples);
        Assert.Equal(original.Shares, parsed.Shares);
        Assert.Equal(original.MaxPinCurrents, parsed.MaxPinCurrents);
        Assert.Equal(original.MaxImbalance, parsed.MaxImbalance);
        Assert.Equal(original.MaxTempOut, parsed.MaxTempOut);
    }

    [Fact]
    public void Csv_rejects_the_header_and_malformed_lines()
    {
        Assert.False(MinuteSummary.TryParseCsv(MinuteSummary.CsvHeader, out _));
        Assert.False(MinuteSummary.TryParseCsv("garbage", out _));
    }

    [Fact]
    public void Reports_learning_until_baseline_is_complete()
    {
        var analyzer = new ConnectorHealthAnalyzer();
        for (int m = 0; m < ConnectorHealthSettings.BaselineMinutes - 1; m++)
            analyzer.AddMinute(Minute(Start.AddMinutes(m), TypicalShares));

        Assert.Equal(ConnectorHealthLevel.Learning, analyzer.Evaluate(null).Level);

        analyzer.AddMinute(Minute(Start.AddMinutes(1000), TypicalShares));
        Assert.NotEqual(ConnectorHealthLevel.Learning, analyzer.Evaluate(null).Level);
    }

    [Fact]
    public void Stable_connector_with_noise_stays_normal()
    {
        var random = new Random(1);
        var analyzer = new ConnectorHealthAnalyzer();

        // 90 days, two hours of gaming a day, with per-minute noise of about +/-0.6 points per pin.
        for (int day = 0; day < 90; day++)
            for (int m = 0; m < 120; m++)
                analyzer.AddMinute(Minute(Start.AddDays(day).AddMinutes(m), Noisy(TypicalShares, random, 0.006)));

        var status = analyzer.Evaluate(null);
        Assert.Equal(ConnectorHealthLevel.Normal, status.Level);
        Assert.True(Math.Abs(status.WorstDriftPoints) < 0.5);
    }

    [Fact]
    public void Slowly_degrading_pin_moves_to_watch_then_warning()
    {
        var random = new Random(2);
        var analyzer = new ConnectorHealthAnalyzer();
        ConnectorHealthLevel? firstNonNormal = null;
        int? watchDay = null, warningDay = null;

        // Pin 3 loses 0.05 points of share per day; the lost current moves to the other pins evenly.
        for (int day = 0; day < 120; day++)
        {
            var loss = day * 0.0005;
            var shares = (double[])TypicalShares.Clone();
            shares[2] -= loss;
            for (int i = 0; i < 6; i++)
                if (i != 2) shares[i] += loss / 5;

            for (int m = 0; m < 120; m++)
                analyzer.AddMinute(Minute(Start.AddDays(day).AddMinutes(m), Noisy(shares, random, 0.006)));

            var level = analyzer.Evaluate(null).Level;
            if (level is ConnectorHealthLevel.Watch or ConnectorHealthLevel.Warning)
                firstNonNormal ??= level;
            if (level == ConnectorHealthLevel.Watch) watchDay ??= day;
            if (level == ConnectorHealthLevel.Warning) warningDay ??= day;
        }

        var final = analyzer.Evaluate(null);
        Assert.Equal(ConnectorHealthLevel.Warning, final.Level);
        Assert.Equal(3, final.WorstPin);
        Assert.True(final.WorstDriftPoints < 0);
        Assert.Equal(ConnectorHealthLevel.Watch, firstNonNormal);
        Assert.NotNull(watchDay);
        Assert.NotNull(warningDay);
        Assert.True(watchDay < warningDay);
    }

    [Fact]
    public void Live_high_load_imbalance_is_a_warning_regardless_of_trend()
    {
        var analyzer = new ConnectorHealthAnalyzer();
        double[] uneven = [0.10, 0.18, 0.18, 0.18, 0.18, 0.18];

        var status = analyzer.Evaluate(Sample(Start, 48, uneven));
        Assert.Equal(ConnectorHealthLevel.Warning, status.Level);
        Assert.Contains("pin 1 lowest", status.Text);

        // Same proportions at light load are not a warning.
        Assert.Equal(ConnectorHealthLevel.Learning, analyzer.Evaluate(Sample(Start, 24, uneven)).Level);
    }

    [Fact]
    public void Reset_starts_learning_again()
    {
        var analyzer = new ConnectorHealthAnalyzer();
        for (int m = 0; m < ConnectorHealthSettings.BaselineMinutes + 100; m++)
            analyzer.AddMinute(Minute(Start.AddMinutes(m), TypicalShares));
        Assert.True(analyzer.BaselineComplete);

        analyzer.Reset(Start.AddDays(10));

        Assert.False(analyzer.BaselineComplete);
        Assert.Equal(ConnectorHealthLevel.Learning, analyzer.Evaluate(null).Level);
    }

    private static double[] Noisy(double[] shares, Random random, double amplitude)
    {
        var noisy = shares.Select(s => s + (random.NextDouble() * 2 - 1) * amplitude).ToArray();
        var sum = noisy.Sum();
        return noisy.Select(s => s / sum).ToArray();
    }
}
