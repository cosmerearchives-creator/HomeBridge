using System.Text.RegularExpressions;

namespace HomeBridge.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch)) throw new FormatException("Invalid release version.");
        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(p => p.Length > 1 && p.All(char.IsAsciiDigit) && p[0] == '0')) throw new FormatException("Invalid numeric prerelease identifier.");
        return new(major, minor, patch, prerelease);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var aNumeric = left[i].All(char.IsAsciiDigit); var bNumeric = right[i].All(char.IsAsciiDigit);
            if (aNumeric && bNumeric)
            {
                result = left[i].Length.CompareTo(right[i].Length);
                if (result == 0) result = string.CompareOrdinal(left[i], right[i]);
            }
            else if (aNumeric != bNumeric) result = aNumeric ? -1 : 1;
            else result = string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}
