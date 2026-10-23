using System;

namespace InfoPanel.Utils;

/// <summary>
/// Compares the version strings this fork publishes (MinVer informational versions and GitHub release tags).
/// </summary>
public static class VersionComparison
{
    public static bool IsNewer(string latest, string current)
    {
        return Compare(latest, current) > 0;
    }

    /// <summary>
    /// True when <paramref name="latest"/> should be offered to an install of <paramref name="current"/>.
    /// A stable install is never moved onto a prerelease. A prerelease install is offered a newer
    /// prerelease and any newer stable release.
    /// </summary>
    public static bool IsOfferedUpdate(string latest, string current)
    {
        if (!IsNewer(latest, current)) return false;
        return IsEligibleRelease(latest, current);
    }

    /// <summary>
    /// A stable install only considers stable releases. A prerelease install considers both.
    /// </summary>
    public static bool IsEligibleRelease(string candidate, string current)
    {
        return Prerelease(current) != null || Prerelease(candidate) == null;
    }

    public static bool CanParse(string version)
    {
        return Version.TryParse(Split(version).baseVersion, out _);
    }

    /// <summary>
    /// Semantic-version comparison. Numeric parts compare as numbers, and a stable release is newer
    /// than any prerelease of the same version (so 1.4.0-preview.4 is offered over preview.3, and
    /// 1.4.0 is offered over either).
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (aBase, aPre) = Split(a);
        var (bBase, bPre) = Split(b);

        var hasA = Version.TryParse(aBase, out var aVersion);
        var hasB = Version.TryParse(bBase, out var bVersion);
        if (!hasA || !hasB) return hasA.CompareTo(hasB);

        var cmp = aVersion!.CompareTo(bVersion);
        if (cmp != 0) return cmp;

        if (aPre == null && bPre == null) return 0;
        if (aPre == null) return 1;
        if (bPre == null) return -1;

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

    public static string? Prerelease(string version) => Split(version).prerelease;

    private static (string baseVersion, string? prerelease) Split(string version)
    {
        version = version.Trim().TrimStart('v', 'V');
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var idx = version.IndexOf('-');
        return idx >= 0 ? (version[..idx], version[(idx + 1)..]) : (version, null);
    }
}
