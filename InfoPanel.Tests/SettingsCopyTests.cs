using System.Collections.ObjectModel;
using InfoPanel.Models;

namespace InfoPanel.Tests;

public class SettingsCopyTests
{
    private sealed class Sample
    {
        public int Rate { get; set; } = 15;
        public string Name { get; set; } = "panel";
        public bool Enabled { get; set; }
        public ObservableCollection<string> Devices { get; } = [];
        public string Computed => Name;
    }

    [Fact]
    public void Copies_every_public_read_write_property_and_leaves_get_only_alone()
    {
        var source = new Sample { Rate = 60, Name = "second monitor", Enabled = true };
        source.Devices.Add("left");
        var target = new Sample { Rate = 1, Name = "old", Enabled = false };
        target.Devices.Add("keep");

        SettingsCopy.CopySettableProperties(target, source);

        Assert.Equal(60, target.Rate);
        Assert.Equal("second monitor", target.Name);
        Assert.True(target.Enabled);
        Assert.Equal(["keep"], target.Devices);
    }

    [Fact]
    public void ReplaceItems_swaps_the_collection_contents()
    {
        var target = new List<int> { 1, 2 };
        SettingsCopy.ReplaceItems(target, new[] { 9 });
        Assert.Equal([9], target);
    }

    [Fact]
    public void LoadSettings_copies_properties_instead_of_listing_them()
    {
        var root = RepoRoot();
        var load = ExtractMethod(File.ReadAllText(Path.Combine(root, "InfoPanel", "Models", "ConfigModel.cs")), "public void LoadSettings()");

        Assert.Contains("SettingsCopy.CopySettableProperties(Settings, settings)", load, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.UiWidth", load, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.ProgramSpecificPanelsEnabled", load, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_settings_collection_is_replaced_on_load()
    {
        var root = RepoRoot();
        var settings = File.ReadAllText(Path.Combine(root, "InfoPanel", "Models", "Settings.cs"));
        var load = ExtractMethod(File.ReadAllText(Path.Combine(root, "InfoPanel", "Models", "ConfigModel.cs")), "public void LoadSettings()");

        var names = System.Text.RegularExpressions.Regex.Matches(settings, @"public ObservableCollection<[^>]+> (\w+)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.Contains($"Settings.{name}", load, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InfoPanel.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find InfoPanel.sln from the test output directory.");
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}");
        var brace = source.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Unclosed method {signature}");
    }
}
