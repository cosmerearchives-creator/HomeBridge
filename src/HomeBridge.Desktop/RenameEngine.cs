using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HomeBridge.Desktop;

public sealed record RenameRule(string Find, string Replace, string Prefix, string Suffix);
public sealed record RenamePlan(string Original, string Destination)
{
    public string OriginalName => Path.GetFileName(Original);
    public string NewName => Path.GetFileName(Destination);
}

public static class RenameEngine
{
    public static IReadOnlyList<RenamePlan> Preview(IEnumerable<string> paths, RenameRule rule)
    {
        var plans = new List<RenamePlan>();
        foreach (var fullPath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(fullPath)) throw new IOException("File missing: " + fullPath);
            var extension = Path.GetExtension(fullPath);
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            if (!string.IsNullOrEmpty(rule.Find))
                stem = stem.Replace(rule.Find, rule.Replace, StringComparison.OrdinalIgnoreCase);
            var newName = rule.Prefix + stem + rule.Suffix + extension;
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Invalid filename: " + newName);
            if (newName.EndsWith(' ') || newName.EndsWith('.'))
                throw new IOException("Filename ends in an invalid character: " + newName);
            var destination = Path.Combine(Path.GetDirectoryName(fullPath)!, newName);
            plans.Add(new RenamePlan(fullPath, destination));
        }
        if (plans.GroupBy(p => p.Destination, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new IOException("Two files would have the same new name.");
        var sources = plans.Select(p => p.Original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            if (!string.Equals(plan.Original, plan.Destination, StringComparison.OrdinalIgnoreCase)
                && (File.Exists(plan.Destination) || Directory.Exists(plan.Destination)))
                throw new IOException("Target already exists: " + plan.Destination);
            if (!sources.Contains(plan.Original)) throw new IOException("Source not selected.");
        }
        return plans;
    }

    // A failed operation rolls back names changed during the current batch.
    public static IReadOnlyList<RenamePlan> Apply(IReadOnlyList<RenamePlan> preview)
    {
        var completed = new List<RenamePlan>();
        try
        {
            foreach (var plan in preview)
            {
                if (string.Equals(plan.Original, plan.Destination, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(plan.Destination)) throw new IOException("Target appeared: " + plan.Destination);
                File.Move(plan.Original, plan.Destination);
                completed.Add(plan);
            }
            return completed;
        }
        catch
        {
            foreach (var plan in completed.AsEnumerable().Reverse())
                if (File.Exists(plan.Destination) && !File.Exists(plan.Original))
                    File.Move(plan.Destination, plan.Original);
            throw;
        }
    }

    public static void Undo(IReadOnlyList<RenamePlan> completed)
    {
        // Preflight before reversing so a new file is never overwritten.
        foreach (var plan in completed)
        {
            if (!File.Exists(plan.Destination)) throw new IOException("Renamed file no longer exists: " + plan.Destination);
            if (File.Exists(plan.Original)) throw new IOException("Original name is now occupied: " + plan.Original);
        }
        foreach (var plan in completed.Reverse())
            File.Move(plan.Destination, plan.Original);
    }
}
