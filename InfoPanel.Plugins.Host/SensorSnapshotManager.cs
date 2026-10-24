using System.Collections.Concurrent;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;

namespace InfoPanel.Plugins.Host
{
    public class SensorSnapshotManager
    {
        private static readonly ILogger Logger = Log.ForContext<SensorSnapshotManager>();
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();
        private readonly ConcurrentDictionary<(string containerId, string entryId), string> _keyCache = new();

        public List<EntryUpdateDto> GetChangedEntries(PluginWrapper wrapper)
        {
            var updates = new List<EntryUpdateDto>();

            foreach (var container in wrapper.PluginContainers)
            {
                IPluginData[] entries;
                try
                {
                    entries = container.Entries.ToArray();
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    var key = _keyCache.GetOrAdd(
                        (container.Id, entry.Id),
                        k => $"{wrapper.Id}/{k.containerId}/{k.entryId}");
                    try
                    {
                        var update = CreateUpdateIfChanged(key, container.Id, entry);
                        if (update != null)
                            updates.Add(update);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Skipped sensor {EntryId} in {PluginId}", entry.Id, wrapper.Id);
                    }
                }
            }

            return updates;
        }

        private EntryUpdateDto? CreateUpdateIfChanged(string key, string containerId, IPluginData entry)
        {
            if (entry is IPluginSensor sensor)
            {
                var currentHash = HashCode.Combine(sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
                if (_lastValues.TryGetValue(key, out var lastHash) && lastHash is int lastInt && lastInt == currentHash)
                    return null;

                _lastValues[key] = currentHash;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "sensor",
                    SensorValue = new SensorValueDto
                    {
                        Value = sensor.Value,
                        ValueMin = sensor.ValueMin,
                        ValueMax = sensor.ValueMax,
                        ValueAvg = sensor.ValueAvg,
                        Unit = sensor.Unit
                    }
                };
            }
            else if (entry is IPluginText text)
            {
                if (_lastValues.TryGetValue(key, out var lastVal) && lastVal is string lastStr && lastStr == text.Value)
                    return null;

                _lastValues[key] = text.Value;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "text",
                    TextValue = text.Value
                };
            }
            else if (entry is IPluginTable table)
            {
                // Do not record a hash when the read fails. The next tick retries,
                // instead of the update loop aborting every plugin for this pass.
                if (!TableDtoConverter.TryConvert(table, out var dto, out var hash))
                {
                    Logger.Debug("Table {EntryId} changed while it was being read", entry.Id);
                    return null;
                }

                if (_lastValues.TryGetValue(key, out var lastHash) && lastHash is int lastInt && lastInt == hash)
                    return null;

                _lastValues[key] = hash;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "table",
                    TableValue = dto
                };
            }

            return null;
        }

        public void Clear()
        {
            _lastValues.Clear();
            _keyCache.Clear();
        }
    }
}
