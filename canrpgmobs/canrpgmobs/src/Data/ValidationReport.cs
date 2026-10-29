using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public enum Severity { Info, Warning, Error }

    // Path looks like "canrpgmobs:config/mobs/goblins.json > role canrpgmobs:shaman > skill firebolt"
    public readonly record struct Problem(Severity Severity, string Path, string Message)
    {
        public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Path}: {Message}";

        // whole ids only, goblin must not match goblin_chief
        public static bool PathMentions(string path, string qualifiedId)
            => path.Split(new[] { " > " }, StringSplitOptions.None).Any(s => s.EndsWith(" " + qualifiedId, StringComparison.Ordinal));
    }

    // errors drop the entry, warnings keep it
    public sealed class ValidationReport
    {
        private readonly List<Problem> problems = new();

        public IReadOnlyList<Problem> Problems => problems;
        public int ErrorCount => problems.Count(p => p.Severity == Severity.Error);
        public int WarningCount => problems.Count(p => p.Severity == Severity.Warning);
        public string Summary => $"{ErrorCount} error(s), {WarningCount} warning(s)";

        public void Error(string path, string message) => problems.Add(new Problem(Severity.Error, path, message));
        public void Warning(string path, string message) => problems.Add(new Problem(Severity.Warning, path, message));
        public void Info(string path, string message) => problems.Add(new Problem(Severity.Info, path, message));

        public IEnumerable<Problem> Worst(int count)
            => problems.Where(p => p.Severity != Severity.Info).OrderByDescending(p => p.Severity).Take(count);
    }
}
