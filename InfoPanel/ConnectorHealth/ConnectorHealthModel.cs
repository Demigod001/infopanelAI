using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InfoPanel.ConnectorHealth;

/// <summary>
/// Long-term health tracking for a GPU 12V-2x6 / 12VHPWR connector, using per-pin currents from a
/// Thermal Grizzly WireView Pro II (read through HWiNFO).
///
/// A contact that is wearing, loosening or contaminated has rising resistance, so over weeks it carries
/// a smaller share of the total current and the other pins pick up the difference. The WireView's own
/// alarm and shutdown catch sudden faults; this looks for that slow drift instead.
///
/// This file is pure logic with no app or WPF dependencies, so it can be unit tested on any OS.
/// </summary>
public static class ConnectorHealthSettings
{
    public const int PinCount = 6;

    /// <summary>
    /// Minimum total current (A) for a sample to count towards pin shares. At light load the pins carry
    /// a fraction of an amp each and measurement noise dominates the differences between them.
    /// 18 A is an average of 3 A per pin.
    /// </summary>
    public const double MinTotalCurrentForShares = 18.0;

    /// <summary>
    /// Thermal Grizzly: below about 6 A per pin, imbalance is not normally associated with increased risk.
    /// The imbalance check only applies when the average pin current is at least this.
    /// </summary>
    public const double ImbalanceMinAveragePinCurrent = 6.0;

    /// <summary>
    /// Thermal Grizzly treats a difference of 40% or more between pins as elevated (above 6 A).
    /// Imbalance here is (highest pin - lowest pin) / average pin current.
    /// </summary>
    public const double ImbalanceWarningFraction = 0.40;

    /// <summary>Minutes under load used to learn each pin's normal share after a (re)seat.</summary>
    public const int BaselineMinutes = 300;

    /// <summary>Minutes under load in the rolling "recent" window compared against the baseline.</summary>
    public const int RecentMinutes = 300;

    /// <summary>Minimum recent minutes (after the baseline) before drift is reported.</summary>
    public const int MinRecentMinutesForDrift = 60;

    /// <summary>
    /// Change in a pin's share of the total, in percentage points, that is flagged.
    /// With six pins an even share is about 16.7%, so 1.5 points is roughly a 9% change in that pin's
    /// current relative to the others, and 3 points roughly 18%.
    /// </summary>
    public const double DriftWatchPoints = 1.5;
    public const double DriftWarningPoints = 3.0;
}

public enum ConnectorHealthLevel
{
    NoData,
    Learning,
    Normal,
    Watch,
    Warning
}

/// <summary>One reading of the connector, taken about once a second.</summary>
public readonly record struct PinSample(DateTime TimeUtc, double[] Currents, double? TempIn, double? TempOut)
{
    public double TotalCurrent => Currents.Sum();
}

/// <summary>Summary of the samples under load within one minute. One CSV row.</summary>
public sealed class MinuteSummary
{
    public DateTime MinuteUtc { get; init; }
    public int Samples { get; init; }
    public double AvgTotalCurrent { get; init; }
    public double[] Shares { get; init; } = new double[ConnectorHealthSettings.PinCount];
    public double[] MaxPinCurrents { get; init; } = new double[ConnectorHealthSettings.PinCount];

    /// <summary>Highest imbalance seen at high load in this minute, or null if load never reached that level.</summary>
    public double? MaxImbalance { get; init; }
    public double? MaxTempIn { get; init; }
    public double? MaxTempOut { get; init; }

    public const string CsvHeader =
        "minute_utc,samples,avg_total_a,share1,share2,share3,share4,share5,share6," +
        "max1_a,max2_a,max3_a,max4_a,max5_a,max6_a,max_imbalance,max_temp_in_c,max_temp_out_c";

    public string ToCsv()
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new List<string>
        {
            MinuteUtc.ToString("yyyy-MM-ddTHH:mm:00Z", c),
            Samples.ToString(c),
            AvgTotalCurrent.ToString("0.###", c)
        };
        parts.AddRange(Shares.Select(s => s.ToString("0.######", c)));
        parts.AddRange(MaxPinCurrents.Select(m => m.ToString("0.###", c)));
        parts.Add(MaxImbalance?.ToString("0.####", c) ?? "");
        parts.Add(MaxTempIn?.ToString("0.#", c) ?? "");
        parts.Add(MaxTempOut?.ToString("0.#", c) ?? "");
        return string.Join(',', parts);
    }

    public static bool TryParseCsv(string line, out MinuteSummary? summary)
    {
        summary = null;
        var f = line.Split(',');
        if (f.Length != 18) return false;

        var c = CultureInfo.InvariantCulture;
        if (!DateTime.TryParse(f[0], c, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var minute)) return false;
        if (!int.TryParse(f[1], NumberStyles.Integer, c, out var samples)) return false;
        if (!double.TryParse(f[2], NumberStyles.Float, c, out var avgTotal)) return false;

        var shares = new double[ConnectorHealthSettings.PinCount];
        var maxes = new double[ConnectorHealthSettings.PinCount];
        for (int i = 0; i < ConnectorHealthSettings.PinCount; i++)
        {
            if (!double.TryParse(f[3 + i], NumberStyles.Float, c, out shares[i])) return false;
            if (!double.TryParse(f[9 + i], NumberStyles.Float, c, out maxes[i])) return false;
        }

        summary = new MinuteSummary
        {
            MinuteUtc = minute,
            Samples = samples,
            AvgTotalCurrent = avgTotal,
            Shares = shares,
            MaxPinCurrents = maxes,
            MaxImbalance = ParseOptional(f[15]),
            MaxTempIn = ParseOptional(f[16]),
            MaxTempOut = ParseOptional(f[17])
        };
        return true;

        static double? ParseOptional(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>Collects the load samples of the current minute and produces a <see cref="MinuteSummary"/>.</summary>
public sealed class MinuteAccumulator
{
    private const int N = ConnectorHealthSettings.PinCount;

    private DateTime? _minute;
    private int _count;
    private double _totalSum;
    private readonly double[] _shareSums = new double[N];
    private readonly double[] _maxCurrents = new double[N];
    private double? _maxImbalance;
    private double? _maxTempIn;
    private double? _maxTempOut;

    /// <summary>
    /// Adds a sample. Returns the finished summary of the previous minute when the sample starts a new
    /// minute and the previous minute had load samples; otherwise null.
    /// </summary>
    public MinuteSummary? Add(PinSample sample)
    {
        var minute = TruncateToMinute(sample.TimeUtc);
        MinuteSummary? finished = null;

        if (_minute != null && minute != _minute)
        {
            finished = Flush();
        }

        _minute = minute;

        if (!ConnectorHealthAnalyzer.IsLoadSample(sample)) return finished;

        var total = sample.TotalCurrent;
        _count++;
        _totalSum += total;
        for (int i = 0; i < N; i++)
        {
            _shareSums[i] += sample.Currents[i] / total;
            _maxCurrents[i] = Math.Max(_maxCurrents[i], sample.Currents[i]);
        }

        var imbalance = ConnectorHealthAnalyzer.Imbalance(sample);
        if (imbalance.HasValue)
            _maxImbalance = Math.Max(_maxImbalance ?? 0, imbalance.Value);
        if (sample.TempIn.HasValue)
            _maxTempIn = Math.Max(_maxTempIn ?? double.MinValue, sample.TempIn.Value);
        if (sample.TempOut.HasValue)
            _maxTempOut = Math.Max(_maxTempOut ?? double.MinValue, sample.TempOut.Value);

        return finished;
    }

    /// <summary>Returns the summary for the minute in progress (if it had load samples) and resets.</summary>
    public MinuteSummary? Flush()
    {
        MinuteSummary? result = null;
        if (_minute != null && _count > 0)
        {
            result = new MinuteSummary
            {
                MinuteUtc = _minute.Value,
                Samples = _count,
                AvgTotalCurrent = _totalSum / _count,
                Shares = _shareSums.Select(s => s / _count).ToArray(),
                MaxPinCurrents = (double[])_maxCurrents.Clone(),
                MaxImbalance = _maxImbalance,
                MaxTempIn = _maxTempIn,
                MaxTempOut = _maxTempOut
            };
        }

        _count = 0;
        _totalSum = 0;
        Array.Clear(_shareSums);
        Array.Clear(_maxCurrents);
        _maxImbalance = null;
        _maxTempIn = null;
        _maxTempOut = null;
        return result;
    }

    private static DateTime TruncateToMinute(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
}

/// <summary>Result of an evaluation, ready for display.</summary>
public sealed class ConnectorHealthStatus
{
    public ConnectorHealthLevel Level { get; init; }
    public string Text { get; init; } = "";
    public int BaselineMinutes { get; init; }
    public int RecentMinutes { get; init; }

    /// <summary>Learned normal share of each pin (0-1), once the baseline is complete.</summary>
    public double[]? BaselineShares { get; init; }

    /// <summary>Average share of each pin (0-1) over the recent window.</summary>
    public double[]? RecentShares { get; init; }

    /// <summary>Recent minus baseline share for each pin, in percentage points.</summary>
    public double[]? DriftPoints { get; init; }

    /// <summary>1-based pin with the largest absolute drift, or 0.</summary>
    public int WorstPin { get; init; }
    public double WorstDriftPoints { get; init; }
}

/// <summary>
/// Learns each pin's normal share of the current after a (re)seat, then compares a rolling recent
/// window against it.
/// </summary>
public sealed class ConnectorHealthAnalyzer
{
    private const int N = ConnectorHealthSettings.PinCount;

    private readonly double[] _baselineShareSums = new double[N];
    private int _baselineSamples;
    private int _baselineMinutes;

    private readonly Queue<MinuteSummary> _recent = new();

    public DateTime? BaselineStartUtc { get; private set; }

    public bool BaselineComplete => _baselineMinutes >= ConnectorHealthSettings.BaselineMinutes;

    public static bool IsLoadSample(PinSample sample) =>
        sample.Currents.Length == N &&
        sample.Currents.All(c => c >= 0 && double.IsFinite(c)) &&
        sample.TotalCurrent >= ConnectorHealthSettings.MinTotalCurrentForShares;

    /// <summary>
    /// (highest - lowest) / average pin current, only when the average pin current is high enough for
    /// imbalance to matter; otherwise null.
    /// </summary>
    public static double? Imbalance(PinSample sample)
    {
        if (sample.Currents.Length != N) return null;
        var average = sample.TotalCurrent / N;
        if (average < ConnectorHealthSettings.ImbalanceMinAveragePinCurrent) return null;
        return (sample.Currents.Max() - sample.Currents.Min()) / average;
    }

    /// <summary>Forget everything (connector reseated or replaced).</summary>
    public void Reset(DateTime? baselineStartUtc = null)
    {
        Array.Clear(_baselineShareSums);
        _baselineSamples = 0;
        _baselineMinutes = 0;
        _recent.Clear();
        BaselineStartUtc = baselineStartUtc;
    }

    public void AddMinute(MinuteSummary minute)
    {
        if (minute.Samples <= 0) return;
        BaselineStartUtc ??= minute.MinuteUtc;

        if (!BaselineComplete)
        {
            for (int i = 0; i < N; i++)
                _baselineShareSums[i] += minute.Shares[i] * minute.Samples;
            _baselineSamples += minute.Samples;
            _baselineMinutes++;
            return;
        }

        _recent.Enqueue(minute);
        while (_recent.Count > ConnectorHealthSettings.RecentMinutes)
            _recent.Dequeue();
    }

    public ConnectorHealthStatus Evaluate(PinSample? liveSample)
    {
        // A live high-load imbalance beyond Thermal Grizzly's guideline is reported first, whatever the trend.
        if (liveSample is PinSample live && Imbalance(live) is double liveImbalance &&
            liveImbalance >= ConnectorHealthSettings.ImbalanceWarningFraction)
        {
            var (high, low) = HighLowPins(live.Currents);
            return Build(ConnectorHealthLevel.Warning,
                $"Warning: pins unbalanced now ({liveImbalance:P0}, pin {high} highest, pin {low} lowest)");
        }

        if (!BaselineComplete)
        {
            return Build(ConnectorHealthLevel.Learning,
                $"Learning: {_baselineMinutes} of {ConnectorHealthSettings.BaselineMinutes} minutes under load");
        }

        var baseline = _baselineShareSums.Select(s => s / _baselineSamples).ToArray();

        if (_recent.Count < ConnectorHealthSettings.MinRecentMinutesForDrift)
        {
            return Build(ConnectorHealthLevel.Normal, "Normal: baseline learned, collecting trend", baseline);
        }

        var recentSamples = _recent.Sum(m => m.Samples);
        var recent = new double[N];
        foreach (var m in _recent)
            for (int i = 0; i < N; i++)
                recent[i] += m.Shares[i] * m.Samples;
        for (int i = 0; i < N; i++)
            recent[i] /= recentSamples;

        var drift = Enumerable.Range(0, N).Select(i => (recent[i] - baseline[i]) * 100).ToArray();
        var worstIndex = Enumerable.Range(0, N).OrderByDescending(i => Math.Abs(drift[i])).First();
        var worst = drift[worstIndex];
        var direction = worst < 0 ? "less" : "more";

        var level = Math.Abs(worst) >= ConnectorHealthSettings.DriftWarningPoints ? ConnectorHealthLevel.Warning
                  : Math.Abs(worst) >= ConnectorHealthSettings.DriftWatchPoints ? ConnectorHealthLevel.Watch
                  : ConnectorHealthLevel.Normal;

        var text = level switch
        {
            ConnectorHealthLevel.Normal => "Normal: pin shares stable",
            ConnectorHealthLevel.Watch => $"Watch: pin {worstIndex + 1} carrying {Math.Abs(worst):0.0} pts {direction} than its baseline",
            _ => $"Warning: pin {worstIndex + 1} carrying {Math.Abs(worst):0.0} pts {direction} than its baseline",
        };

        return new ConnectorHealthStatus
        {
            Level = level,
            Text = text,
            BaselineMinutes = _baselineMinutes,
            RecentMinutes = _recent.Count,
            BaselineShares = baseline,
            RecentShares = recent,
            DriftPoints = drift,
            WorstPin = worstIndex + 1,
            WorstDriftPoints = worst
        };

        ConnectorHealthStatus Build(ConnectorHealthLevel lvl, string txt, double[]? baselineShares = null) => new()
        {
            Level = lvl,
            Text = txt,
            BaselineMinutes = _baselineMinutes,
            RecentMinutes = _recent.Count,
            BaselineShares = baselineShares ?? (BaselineComplete ? _baselineShareSums.Select(s => s / _baselineSamples).ToArray() : null)
        };
    }

    private static (int High, int Low) HighLowPins(double[] currents)
    {
        var high = Array.IndexOf(currents, currents.Max()) + 1;
        var low = Array.IndexOf(currents, currents.Min()) + 1;
        return (high, low);
    }
}
