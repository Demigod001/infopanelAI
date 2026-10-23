using InfoPanel.Monitors;
using InfoPanel.Plugins;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace InfoPanel.ConnectorHealth;

/// <summary>
/// Reads a Thermal Grizzly WireView Pro II through HWiNFO once a second, keeps a per-minute history
/// on disk, and publishes the analysis as built-in sensors ("Connector Health") that can be placed on
/// a panel like any plugin sensor.
///
/// History: %LOCALAPPDATA%\InfoPanel\connector-health\YYYY-MM.csv (one row per minute under load).
/// Baseline start: baseline-start.txt in the same folder, written when the user marks a reseat.
/// Does nothing beyond a dictionary scan per second when no WireView is present.
/// </summary>
public sealed class ConnectorHealthService
{
    private static readonly ILogger Logger = Log.ForContext<ConnectorHealthService>();
    private static readonly Lazy<ConnectorHealthService> _instance = new(() => new ConnectorHealthService());
    public static ConnectorHealthService Instance => _instance.Value;

    private const string PluginId = "connector-health";
    private const string PluginName = "Connector Health (WireView)";
    private const string ContainerId = "health";
    private const string ContainerName = "12V-2x6 connector";
    private const string SensorNameMarker = "WireView";

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "connector-health");
    private static readonly string BaselineFile = Path.Combine(DataFolder, "baseline-start.txt");

    private readonly object _lock = new();
    private readonly ConnectorHealthAnalyzer _analyzer = new();
    private readonly MinuteAccumulator _accumulator = new();
    private Timer? _timer;
    private int _tickRunning;

    // HWiNFO keys for pins 1-6, temperature in/out. Cached after the first successful scan.
    private (int, uint, uint, uint)[]? _pinKeys;
    private (int, uint, uint, uint)? _tempInKey;
    private (int, uint, uint, uint)? _tempOutKey;
    private DateTime _nextScanUtc = DateTime.MinValue;

    // Published sensors.
    private readonly PluginText _status = new("status", "Status", "No WireView data");
    private readonly PluginSensor _level = new("level", "Level (0 none, 1 learning, 2 normal, 3 watch, 4 warning)", 0);
    private readonly PluginSensor _worstDrift = new("worst-drift", "Largest pin drift", 0, "pts");
    private readonly PluginSensor _imbalance = new("imbalance", "Pin imbalance now", 0, "%");
    private readonly PluginSensor _learning = new("learning", "Baseline learned", 0, "%");
    private readonly PluginSensor[] _shares;
    private readonly PluginSensor[] _drifts;

    public ConnectorHealthStatus? LastStatus { get; private set; }
    public bool WireViewDetected => _pinKeys != null;

    private ConnectorHealthService()
    {
        _shares = Enumerable.Range(1, ConnectorHealthSettings.PinCount)
            .Select(p => new PluginSensor($"pin{p}-share", $"Pin {p} share of current", 0, "%")).ToArray();
        _drifts = Enumerable.Range(1, ConnectorHealthSettings.PinCount)
            .Select(p => new PluginSensor($"pin{p}-drift", $"Pin {p} drift from baseline", 0, "pts")).ToArray();
    }

    public void Start()
    {
        if (_timer != null) return;

        try
        {
            LoadHistory();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load connector health history");
        }

        RegisterSensors();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Logger.Information("Connector health tracking started");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        lock (_lock)
        {
            // Keep the partial minute rather than losing it on exit.
            if (_accumulator.Flush() is MinuteSummary last)
                Append(last);
        }
    }

    /// <summary>
    /// Start a new baseline after the connector was reseated or replaced. History files are kept for
    /// reference, but trend comparison only uses data from now on.
    /// </summary>
    public void ResetBaseline()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(BaselineFile, now.ToString("o", CultureInfo.InvariantCulture));
            _accumulator.Flush();
            _analyzer.Reset(now);
            Logger.Information("Connector health baseline reset at {Time}", now);
            Publish(null);
        }
    }

    public static string DataFolderPath => DataFolder;

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;
        try
        {
            var sample = ReadSample();
            lock (_lock)
            {
                if (sample is PinSample s)
                {
                    if (_accumulator.Add(s) is MinuteSummary finished)
                    {
                        _analyzer.AddMinute(finished);
                        Append(finished);
                    }
                }

                Publish(sample);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Connector health tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private PinSample? ReadSample()
    {
        if (_pinKeys == null || !_pinKeys.All(k => HWHash.SENSORHASH.ContainsKey(k)))
        {
            _pinKeys = null;
            // Rescan at most every 10 s while HWiNFO or the WireView is missing.
            if (DateTime.UtcNow < _nextScanUtc) return null;
            _nextScanUtc = DateTime.UtcNow.AddSeconds(10);
            if (!FindWireView()) return null;
        }

        var currents = new double[ConnectorHealthSettings.PinCount];
        for (int i = 0; i < currents.Length; i++)
        {
            if (!HWHash.SENSORHASH.TryGetValue(_pinKeys![i], out var reading)) return null;
            currents[i] = reading.ValueNow;
        }

        double? tempIn = _tempInKey is { } ti && HWHash.SENSORHASH.TryGetValue(ti, out var a) ? a.ValueNow : null;
        double? tempOut = _tempOutKey is { } to && HWHash.SENSORHASH.TryGetValue(to, out var b) ? b.ValueNow : null;

        return new PinSample(DateTime.UtcNow, currents, tempIn, tempOut);
    }

    /// <summary>
    /// Locates the WireView's readings by exact name ("Pin 1 Current" ... "Pin 6 Current") under a
    /// HWiNFO sensor whose name contains "WireView". Prefers the local machine over HWiNFO remotes.
    /// </summary>
    private bool FindWireView()
    {
        var candidates = HWHash.SENSORHASH
            .Where(kv => (kv.Value.ParentNameDefault ?? "").Contains(SensorNameMarker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key.Item1 == -1 ? 0 : 1)
            .ToList();

        if (candidates.Count == 0) return false;

        // Group by sensor so pins from two different devices are never mixed.
        foreach (var group in candidates.GroupBy(kv => (kv.Key.Item1, kv.Key.Item2, kv.Key.Item3)))
        {
            var pinKeys = new (int, uint, uint, uint)[ConnectorHealthSettings.PinCount];
            var found = 0;
            for (int p = 1; p <= ConnectorHealthSettings.PinCount; p++)
            {
                var match = group.FirstOrDefault(kv => IsNamed(kv.Value, $"Pin {p} Current"));
                if (match.Value.NameDefault == null) break;
                pinKeys[p - 1] = match.Key;
                found++;
            }

            if (found != ConnectorHealthSettings.PinCount) continue;

            _pinKeys = pinKeys;
            _tempInKey = group.Where(kv => IsNamed(kv.Value, "Temperature In")).Select(kv => ((int, uint, uint, uint)?)kv.Key).FirstOrDefault();
            _tempOutKey = group.Where(kv => IsNamed(kv.Value, "Temperature Out")).Select(kv => ((int, uint, uint, uint)?)kv.Key).FirstOrDefault();
            Logger.Information("WireView found in HWiNFO: {Sensor}", group.First().Value.ParentNameDefault);
            return true;
        }

        return false;

        static bool IsNamed(HWHash.HWINFO_HASH reading, string name) =>
            string.Equals(reading.NameDefault?.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reading.NameCustom?.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    private void Publish(PinSample? sample)
    {
        var status = _pinKeys == null && sample == null
            ? new ConnectorHealthStatus { Level = ConnectorHealthLevel.NoData, Text = "No WireView data" }
            : _analyzer.Evaluate(sample);
        LastStatus = status;

        _status.Value = status.Text;
        _level.Value = (int)status.Level;
        _worstDrift.Value = (float)status.WorstDriftPoints;
        _learning.Value = (float)Math.Min(100.0, 100.0 * status.BaselineMinutes / ConnectorHealthSettings.BaselineMinutes);

        if (sample is PinSample s)
        {
            var total = s.TotalCurrent;
            _imbalance.Value = (float)((ConnectorHealthAnalyzer.Imbalance(s) ?? 0) * 100);

            // Live share is only meaningful under load; otherwise hold the last value.
            if (ConnectorHealthAnalyzer.IsLoadSample(s))
            {
                for (int i = 0; i < _shares.Length; i++)
                    _shares[i].Value = (float)(s.Currents[i] / total * 100);
            }
        }

        for (int i = 0; i < _drifts.Length; i++)
            _drifts[i].Value = (float)(status.DriftPoints?[i] ?? 0);
    }

    private void RegisterSensors()
    {
        var order = 100_000; // after real plugins
        IEnumerable<IPluginData> all = new IPluginData[] { _status, _level, _worstDrift, _imbalance, _learning }
            .Concat(_shares).Concat(_drifts);

        foreach (var data in all)
        {
            var id = $"/{PluginId}/{ContainerId}/{data.Id}";
            PluginMonitor.SENSORHASH[id] = new PluginMonitor.PluginReading
            {
                Id = id,
                Name = data.Name,
                ContainerId = ContainerId,
                ContainerName = ContainerName,
                PluginId = PluginId,
                PluginName = PluginName,
                Data = data,
                IndexOrder = order++
            };
        }
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(DataFolder)) return;

        DateTime? baselineStart = null;
        if (File.Exists(BaselineFile) &&
            DateTime.TryParse(File.ReadAllText(BaselineFile).Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            baselineStart = parsed;
        }

        _analyzer.Reset(baselineStart);
        var loaded = 0;

        foreach (var file in Directory.GetFiles(DataFolder, "????-??.csv").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!MinuteSummary.TryParseCsv(line, out var minute) || minute == null) continue;
                if (baselineStart.HasValue && minute.MinuteUtc < baselineStart.Value) continue;
                _analyzer.AddMinute(minute);
                loaded++;
            }
        }

        Logger.Information("Connector health history loaded: {Minutes} minutes under load", loaded);
    }

    private static void Append(MinuteSummary minute)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var file = Path.Combine(DataFolder, minute.MinuteUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".csv");
            var lines = File.Exists(file)
                ? new[] { minute.ToCsv() }
                : new[] { MinuteSummary.CsvHeader, minute.ToCsv() };
            File.AppendAllLines(file, lines);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to write connector health history");
        }
    }
}
