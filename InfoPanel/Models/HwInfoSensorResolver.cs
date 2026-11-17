using InfoPanel.Utils;
using Serilog;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    /// <summary>
    /// Reads an item's HWiNFO value and, if HWiNFO has renumbered the reading, reconnects the item to it by
    /// name (see <see cref="HwInfoMatching"/>). Previously this repair only happened when importing a profile,
    /// so an existing profile went blank after HWiNFO changed its numbering while a new profile worked.
    /// </summary>
    internal static class HwInfoSensorResolver
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(HwInfoSensorResolver));
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

        private sealed class State
        {
            public long NextAttemptTicks;
            public (int Remote, uint Id, uint Instance, uint EntryId)? Replacement;
        }

        private static readonly ConditionalWeakTable<IHwInfoSensorItem, State> States = new();

        public static SensorReading? Read(IHwInfoSensorItem item)
        {
            var reading = SensorReader.ReadHwInfoSensor(item.HwInfoRemoteIndex, item.Id, item.Instance, item.EntryId);
            if (reading != null) return reading;

            var state = States.GetOrCreateValue(item);

            // Already reconnected; the saved IDs are being updated on the UI thread.
            if (state.Replacement is { } r)
            {
                reading = SensorReader.ReadHwInfoSensor(r.Remote, r.Id, r.Instance, r.EntryId);
                if (reading != null) return reading;
                state.Replacement = null;
            }

            var now = Environment.TickCount64;
            if (now < state.NextAttemptTicks || HWHash.SENSORHASH.IsEmpty) return null;
            state.NextAttemptTicks = now + (long)RetryInterval.TotalMilliseconds;

            var available = HWHash.SENSORHASH.Values.Select(h =>
                new HwInfoReadingRef(h.RemoteIndex, h.ParentID, h.ParentInstance, h.SensorID, h.NameDefault));
            var match = HwInfoMatching.FindReplacement(item.HwInfoRemoteIndex, item.Id, item.EntryId, item.SensorName, available);
            if (match is not HwInfoReadingRef m) return null;

            state.Replacement = (m.RemoteIndex, m.ParentId, m.ParentInstance, m.SensorId);
            Logger.Information("HWiNFO reading '{Name}' was renumbered; reconnected ({OldId:X}/{OldInstance}/{OldEntry:X} -> {NewId:X}/{NewInstance}/{NewEntry:X})",
                item.SensorName, item.Id, item.Instance, item.EntryId, m.ParentId, m.ParentInstance, m.SensorId);

            // Update the item's saved IDs on the UI thread (property changes drive the editor and saving).
            void Apply()
            {
                item.Id = m.ParentId;
                item.Instance = m.ParentInstance;
                item.EntryId = m.SensorId;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);

            return SensorReader.ReadHwInfoSensor(m.RemoteIndex, m.ParentId, m.ParentInstance, m.SensorId);
        }
    }
}
