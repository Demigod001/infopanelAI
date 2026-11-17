using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Utils;

/// <summary>A HWiNFO reading as identified in shared memory.</summary>
public readonly record struct HwInfoReadingRef(int RemoteIndex, uint ParentId, uint ParentInstance, uint SensorId, string NameDefault);

/// <summary>
/// Finds the reading a saved panel item meant, after HWiNFO has renumbered its readings.
///
/// Panel items store HWiNFO's numeric identifiers (sensor ID, instance, entry ID). HWiNFO can change
/// these, e.g. after an update or reset, or when a USB device such as a WireView is detected in a
/// different order, and the item then shows nothing. Items also store the reading's default name,
/// so they can be matched again. A match is only accepted when exactly one reading fits, so an
/// item is never connected to a guess (for example one of two drives with identical reading names).
/// Pure logic, unit tested.
/// </summary>
public static class HwInfoMatching
{
    public static HwInfoReadingRef? FindReplacement(int remoteIndex, uint parentId, uint sensorId, string? nameDefault,
        IEnumerable<HwInfoReadingRef> available)
    {
        if (string.IsNullOrWhiteSpace(nameDefault)) return null;

        var sameName = available
            .Where(r => r.RemoteIndex == remoteIndex && string.Equals(r.NameDefault, nameDefault, StringComparison.Ordinal))
            .ToList();
        if (sameName.Count == 0) return null;

        // 1. Same device type and reading, only the device's instance number changed.
        var sameDeviceAndReading = sameName.Where(r => r.ParentId == parentId && r.SensorId == sensorId).ToList();
        if (sameDeviceAndReading.Count == 1) return sameDeviceAndReading[0];
        if (sameDeviceAndReading.Count > 1) return null; // several identical devices: can't tell which

        // 2. Same device type, the reading's own ID changed.
        var sameDevice = sameName.Where(r => r.ParentId == parentId).ToList();
        if (sameDevice.Count == 1) return sameDevice[0];
        if (sameDevice.Count > 1) return null;

        // 3. The device's ID changed too: accept only a name that is unique across all readings.
        return sameName.Count == 1 ? sameName[0] : null;
    }
}
