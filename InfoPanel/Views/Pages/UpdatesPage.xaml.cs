using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class UpdatesPage : Page
    {
        private static readonly ILogger Logger = Log.ForContext<UpdatesPage>();

        public UpdatesViewModel ViewModel
        {
            get;
        }

        public UpdatesPage(UpdatesViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
            ApplyUpdateCheckResults();
        }

        private void ButtonCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdates();
        }

        private void ApplyUpdateCheckResults()
        {
            var checker = UpdateChecker.Instance;

            if (checker.LatestVersion != null)
            {
                ApplyVersionResult(checker);
                ApplyChangelog(checker);
            }
            else
            {
                // Startup check hasn't completed yet — listen for it
                ViewModel.UpdateCheckInProgress = true;
                checker.UpdateCheckCompleted += OnStartupCheckCompleted;
            }
        }

        private void OnStartupCheckCompleted()
        {
            UpdateChecker.Instance.UpdateCheckCompleted -= OnStartupCheckCompleted;
            Dispatcher.Invoke(() =>
            {
                var checker = UpdateChecker.Instance;
                ApplyVersionResult(checker);
                ApplyChangelog(checker);
                ViewModel.UpdateCheckInProgress = false;
            });
        }

        private void ApplyVersionResult(UpdateChecker checker)
        {
            if (checker.UpdateAvailable && checker.LatestVersion != null)
            {
                ViewModel.VersionModel = checker.LatestVersion;
                ViewModel.UpdateAvailable = true;
            }
            else
            {
                ViewModel.UpdateAvailable = false;
            }
        }

        private void ApplyChangelog(UpdateChecker checker)
        {
            ViewModel.UpdateVersions.Clear();

            if (checker.Versions == null) return;

            bool first = true;
            foreach (var v in checker.Versions)
            {
                var (summary, details) = ParseSummary(v.Changelog);

                ViewModel.UpdateVersions.Add(new UpdateVersion
                {
                    Version = v.Version,
                    Title = v.Changelog,
                    Expanded = first,
                    Changelog = details,
                    Summary = summary
                });

                first = false;
            }
        }

        private static (string? summary, string details) ParseSummary(string changelog)
        {
            const string summaryHeader = "## Summary";

            // Normalize line endings to \n for reliable parsing
            var normalized = changelog.Replace("\r\n", "\n");

            var summaryIndex = normalized.IndexOf(summaryHeader, StringComparison.Ordinal);
            if (summaryIndex < 0)
                return (null, changelog);

            var afterHeader = normalized[(summaryIndex + summaryHeader.Length)..];

            // Look for a line that is just "---"
            var separatorIndex = afterHeader.IndexOf("\n---\n", StringComparison.Ordinal);
            if (separatorIndex < 0)
                return (null, changelog);

            var summary = afterHeader[..separatorIndex].Trim();
            var details = afterHeader[(separatorIndex + 5)..].Trim(); // 5 = "\n---\n".Length

            if (string.IsNullOrEmpty(summary))
                return (null, changelog);

            return (summary, details);
        }

        private async void CheckUpdates()
        {
            ViewModel.UpdateCheckInProgress = true;

            var checker = UpdateChecker.Instance;
            await checker.CheckAsync();

            ApplyVersionResult(checker);
            ApplyChangelog(checker);

            ViewModel.UpdateCheckInProgress = false;
        }

        private void ChangelogTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version)
            {
                PopulateChangelogInlines(tb, version.Changelog);
            }
        }

        private void SummaryTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version && version.Summary != null)
            {
                PopulateChangelogInlines(tb, version.Summary);
            }
        }

        private void DetailsTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version)
            {
                PopulateChangelogInlines(tb, version.Changelog);
            }
        }

        private static void PopulateChangelogInlines(TextBlock textBlock, string markdown)
        {
            textBlock.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(markdown)) return;

            var lines = markdown.Split('\n');
            bool firstLine = true;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## "))
                {
                    // Top-level header (e.g. "# InfoPanel v1.3.1 Release Notes") — skip
                    continue;
                }

                if (!firstLine)
                    textBlock.Inlines.Add(new LineBreak());

                if (trimmed.StartsWith("## "))
                {
                    // Category header — bold, slightly larger
                    if (!firstLine)
                        textBlock.Inlines.Add(new LineBreak()); // extra spacing before header

                    var header = trimmed[3..];
                    textBlock.Inlines.Add(new Run(header) { FontWeight = FontWeights.SemiBold, FontSize = 13 });
                }
                else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    // Bullet item
                    var text = trimmed[2..];
                    AddInlineText(textBlock, $"  \u2022 {text}");
                }
                else
                {
                    AddInlineText(textBlock, trimmed);
                }

                firstLine = false;
            }
        }

        private static readonly Regex BoldPattern = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

        private static void AddInlineText(TextBlock textBlock, string text)
        {
            var matches = BoldPattern.Matches(text);
            if (matches.Count == 0)
            {
                textBlock.Inlines.Add(new Run(text));
                return;
            }

            int pos = 0;
            foreach (Match match in matches)
            {
                if (match.Index > pos)
                    textBlock.Inlines.Add(new Run(text[pos..match.Index]));

                textBlock.Inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeights.SemiBold });
                pos = match.Index + match.Length;
            }

            if (pos < text.Length)
                textBlock.Inlines.Add(new Run(text[pos..]));
        }

        private async void ButtonUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VersionModel?.DownloadUrl is string url)
            {
                ViewModel.DownloadInProgress = true;
                ViewModel.DownloadProgress = 0;

                var cts = new CancellationTokenSource();
                IProgress<DownloadProgressArgs> progressReporter = new Progress<DownloadProgressArgs>(args =>
                {
                    ViewModel.DownloadProgress = args.PercentComplete;
                    ViewModel.DownloadStatus = $"{FormatBytes(args.BytesReceived)} / {FormatBytes(args.TotalBytes)} ({args.PercentComplete:F0}%)";
                });

                // The download used to sit outside the try block, so a network error escaped this
                // async void handler, and DownloadInProgress was never reset (button stuck).
                // Failures were also swallowed by an empty catch, so the user saw nothing happen.
                try
                {
                    // Never run an installer we cannot verify.
                    if (ViewModel.VersionModel.ChecksumUrl is not string checksumUrl)
                    {
                        throw new InvalidOperationException("This release has no checksum file, so it cannot be verified.");
                    }

                    var expectedHash = await DownloadExpectedSha256Async(checksumUrl, cts.Token);

                    using var stream = await DownloadStreamWithProgressAsync(url, cts.Token, progressReporter);

                    var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Error("Update checksum mismatch. Expected {Expected}, got {Actual}", expectedHash, actualHash);
                        throw new InvalidOperationException("The downloaded installer did not match its published checksum, so it was not run. Please try again later.");
                    }
                    stream.Position = 0;

                    var downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "updates");
                    Directory.CreateDirectory(downloadPath);

                    var filePath = Path.Combine(downloadPath, "InfoPanelSetup.exe");

                    SaveStreamToFile(stream, filePath);

                    // UseShellExecute so Windows can show the UAC prompt the installer needs.
                    // .NET defaults this to false, and CreateProcess cannot start an exe that
                    // requires elevation (ERROR_ELEVATION_REQUIRED), so the update silently did nothing.
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Update download or launch failed");
                    System.Windows.MessageBox.Show(
                        $"The update could not be downloaded or started.\n\n{ex.Message}",
                        "InfoPanel Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                ViewModel.DownloadInProgress = false;
            }
        }

        /// <summary>
        /// Reads a .sha256 file ("&lt;hex&gt;" or "&lt;hex&gt;  filename", as written by sha256sum/PowerShell) and returns the hash.
        /// </summary>
        private static async Task<string> DownloadExpectedSha256Async(string checksumUrl, CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient();
            var content = await httpClient.GetStringAsync(checksumUrl, cancellationToken);
            var hash = content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException("The published checksum file is not valid.");
            }

            return hash;
        }

        public static void SaveStreamToFile(Stream stream, string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                stream.CopyTo(fileStream);
            }
        }

        public static async Task<Stream> DownloadStreamWithProgressAsync(string url, CancellationToken cancellationToken, IProgress<DownloadProgressArgs> progessReporter)
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var receivedBytes = 0;
            var buffer = new byte[4096];
            var totalBytes = Convert.ToDouble(response.Content.Headers.ContentLength);

            var memStream = new MemoryStream();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

                await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }
                receivedBytes += bytesRead;

                var args = new DownloadProgressArgs(receivedBytes, totalBytes);
                progessReporter.Report(args);
            }

            memStream.Position = 0;
            return memStream;
        }

        private static string FormatBytes(double bytes)
        {
            return bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824:F2} GB",
                >= 1_048_576 => $"{bytes / 1_048_576:F1} MB",
                >= 1_024 => $"{bytes / 1_024:F0} KB",
                _ => $"{bytes:F0} B"
            };
        }

        public class DownloadProgressArgs : EventArgs
        {
            public DownloadProgressArgs(int bytesReceived, double totalBytes)
            {
                BytesReceived = bytesReceived;
                TotalBytes = totalBytes;
            }

            public double TotalBytes { get; }

            public double BytesReceived { get; }

            // TotalBytes is 0 when the server sends no Content-Length; avoid showing NaN/Infinity.
            public double PercentComplete => TotalBytes > 0 ? 100 * (BytesReceived / TotalBytes) : 0;
        }
    }
}
