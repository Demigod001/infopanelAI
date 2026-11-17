using InfoPanel.Utils;

namespace InfoPanel.Tests;

public class HwInfoMatchingTests
{
    private static HwInfoReadingRef R(uint parent, uint instance, uint sensor, string name, int remote = -1) =>
        new(remote, parent, instance, sensor, name);

    [Fact]
    public void Device_instance_renumbered_is_matched()
    {
        var available = new[] { R(0xE000, 1, 0x7000005, "Pin 1 Current"), R(0xE000, 1, 0x7000006, "Pin 2 Current") };
        var match = HwInfoMatching.FindReplacement(-1, 0xE000, 0x7000005, "Pin 1 Current", available);
        Assert.Equal(available[0], match);
    }

    [Fact]
    public void Reading_id_changed_on_same_device_is_matched()
    {
        var available = new[] { R(0xE000, 0, 0x8000001, "Pin 1 Current"), R(0x2000, 0, 0x1, "Pin 1 Current") };
        var match = HwInfoMatching.FindReplacement(-1, 0xE000, 0x7000005, "Pin 1 Current", available);
        Assert.Equal(available[0], match);
    }

    [Fact]
    public void Unique_name_matches_when_device_id_changed()
    {
        var available = new[] { R(0xF000, 0, 0x1, "GPU Hot Spot Temperature") };
        Assert.Equal(available[0], HwInfoMatching.FindReplacement(-1, 0xE000, 0x9, "GPU Hot Spot Temperature", available));
    }

    [Fact]
    public void Ambiguous_matches_are_rejected()
    {
        // Two identical drives: same device type, same reading.
        var drives = new[] { R(0xA000, 0, 0x1, "Drive Temperature"), R(0xA000, 1, 0x1, "Drive Temperature") };
        Assert.Null(HwInfoMatching.FindReplacement(-1, 0xA000, 0x1, "Drive Temperature", drives));

        // Name shared by two different devices and the original device is gone.
        var shared = new[] { R(0xB000, 0, 0x1, "Temperature"), R(0xC000, 0, 0x1, "Temperature") };
        Assert.Null(HwInfoMatching.FindReplacement(-1, 0xA000, 0x1, "Temperature", shared));
    }

    [Fact]
    public void Other_hwinfo_remotes_and_missing_names_are_ignored()
    {
        var available = new[] { R(0xE000, 0, 0x1, "Pin 1 Current", remote: 0) };
        Assert.Null(HwInfoMatching.FindReplacement(-1, 0xE000, 0x1, "Pin 1 Current", available));
        Assert.Null(HwInfoMatching.FindReplacement(-1, 0xE000, 0x1, "", available));
        Assert.Null(HwInfoMatching.FindReplacement(-1, 0xE000, 0x1, "Something else", available));
    }
}
