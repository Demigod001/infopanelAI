using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace InfoPanel.Services;

/// <summary>
/// Checks this fork's GitHub Releases for newer versions.
///
/// The original project checked api.infopanel.net, which lists the upstream author's builds; installing
/// one of those would replace this fork. Releases are created by the GitHub Actions workflow when a
/// version tag is pushed, with the installer and a .sha256 file attached.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The GitHub repository ("owner/name") whose releases provide updates.</summary>
    public const string UpdateRepository = "Demigod001/infopanelAI";

    public const string InstallerAssetName = "InfoPanelSetup.exe";
    public const string ChecksumAssetName = "InfoPanelSetup.exe.sha256";

    private static readonly Lazy<UpdateChecker> _instance = new(() => new UpdateChecker());
    private static readonly ILogger Logger = Log.ForContext<UpdateChecker>();

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static UpdateChecker Instance => _instance.Value;

    public VersionModel? LatestVersion { get; private set; }
    public IReadOnlyList<VersionEntry>? Versions { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public event Action? UpdateCheckCompleted;

    private UpdateChecker()
    {
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InfoPanel", VersionHelper.AppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (args.Contains("action") && args["action"] == "viewUpdate")
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Application.Current.MainWindow is Views.Windows.MainWindow mainWindow)
                {
                    mainWindow.RestoreWindow();
                    mainWindow.Navigate(typeof(Views.Pages.UpdatesPage));
                }
            });
        }
    }

    public async Task CheckAsync(bool showNotification = false)
    {
        try
        {
            var url = $"https://api.github.com/repos/{UpdateRepository}/releases?per_page=30";
            using var response = await HttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream) ?? [];

            var versions = releases
                .Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName) && TryParseVersion(r.TagName, out _))
                .Select(r => new VersionEntry
                {
                    Version = r.TagName!.StartsWith('v') ? r.TagName : $"v{r.TagName}",
                    Changelog = r.Body ?? string.Empty,
                    DownloadUrl = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))?.DownloadUrl,
                    ChecksumUrl = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))?.DownloadUrl,
                })
                // Only offer releases that can actually be installed and verified.
                .Where(v => v.DownloadUrl != null && v.ChecksumUrl != null)
                .OrderByDescending(v => v.Version, Comparer<string>.Create(CompareVersions))
                .ToList();

            Versions = versions;

            var latest = versions.FirstOrDefault();
            if (latest != null)
            {
                LatestVersion = new VersionModel
                {
                    Version = latest.Version,
                    DownloadUrl = latest.DownloadUrl,
                    ChecksumUrl = latest.ChecksumUrl,
                    Changelog = latest.Changelog,
                    ChangelogItems = latest.ChangelogItems
                };

                var currentVersion = VersionHelper.AppVersion;
                UpdateAvailable = IsNewerVersion(latest.Version, currentVersion);

                if (UpdateAvailable)
                {
                    Logger.Information("Update available: {Version}", latest.Version);
                    if (showNotification)
                    {
                        ShowUpdateNotification(latest.Version);
                    }
                }
                else
                {
                    ClearUpdateNotification();
                }
            }
            else
            {
                // No releases yet: report "up to date" rather than leaving the page waiting.
                LatestVersion = new VersionModel { Version = VersionHelper.AppVersion };
                UpdateAvailable = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates");
        }

        UpdateCheckCompleted?.Invoke();
    }

    private static void ShowUpdateNotification(string version)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("InfoPanel Update Available")
                .AddText($"Version {version} is ready to download.")
                .AddButton(new ToastButton()
                    .SetContent("View Update")
                    .AddArgument("action", "viewUpdate"))
                .Show(toast =>
                {
                    toast.Tag = "update";
                    toast.Group = "infopanel";
                });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to show update notification");
        }
    }

    private static void ClearUpdateNotification()
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove("update", "infopanel");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear update notification");
        }
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        return CompareVersions(latest, current) > 0;
    }

    /// <summary>
    /// Semantic-version comparison. The previous version only compared the numeric part and treated any two
    /// prereleases of the same version as equal, so e.g. 1.4.0-preview.4 was never offered over preview.3.
    /// </summary>
    internal static int CompareVersions(string a, string b)
    {
        var (aBase, aPre) = SplitVersion(a);
        var (bBase, bPre) = SplitVersion(b);

        var hasA = Version.TryParse(aBase, out var aVersion);
        var hasB = Version.TryParse(bBase, out var bVersion);
        if (!hasA || !hasB) return hasA.CompareTo(hasB);

        var cmp = aVersion!.CompareTo(bVersion);
        if (cmp != 0) return cmp;

        // Same numeric version: a stable release is newer than any prerelease of it.
        if (aPre == null && bPre == null) return 0;
        if (aPre == null) return 1;
        if (bPre == null) return -1;

        // Compare prerelease identifiers left to right; numeric parts numerically.
        var aParts = aPre.Split('.');
        var bParts = bPre.Split('.');
        for (int i = 0; i < Math.Min(aParts.Length, bParts.Length); i++)
        {
            var aIsNum = long.TryParse(aParts[i], out var aNum);
            var bIsNum = long.TryParse(bParts[i], out var bNum);

            int partCmp = (aIsNum, bIsNum) switch
            {
                (true, true) => aNum.CompareTo(bNum),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(aParts[i], bParts[i])
            };

            if (partCmp != 0) return partCmp;
        }

        return aParts.Length.CompareTo(bParts.Length);
    }

    private static bool TryParseVersion(string version, out Version? parsed)
    {
        return Version.TryParse(SplitVersion(version).baseVersion, out parsed);
    }

    private static (string baseVersion, string? prerelease) SplitVersion(string version)
    {
        version = version.TrimStart('v', 'V');
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var idx = version.IndexOf('-');
        return idx >= 0 ? (version[..idx], version[(idx + 1)..]) : (version, null);
    }

    public class VersionEntry
    {
        public string Version { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public ICollection<string> ChangelogItems { get; set; } = [];
        public string? DownloadUrl { get; set; }
        public string? ChecksumUrl { get; set; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
    }
}
