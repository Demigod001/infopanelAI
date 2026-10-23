using System.Collections.Generic;

namespace InfoPanel.Models
{
    public class VersionModel
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }

        /// <summary>URL of a file holding the installer's SHA-256 hash, published alongside it.</summary>
        public string? ChecksumUrl { get; set; }
        public string Changelog { get; set; } = string.Empty;
        public ICollection<string> ChangelogItems { get; set; } = [];
    }
}
