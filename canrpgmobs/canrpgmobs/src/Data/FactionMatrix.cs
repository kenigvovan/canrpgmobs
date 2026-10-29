using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public enum FactionRelation { Neutral, Ally, Hostile }

    // Built once per catalog. Unknown faction ids are neutral to everyone except themselves.
    public sealed class FactionMatrix
    {
        public const string Players = "canrpgmobs:players";
        public const string Wildlife = "canrpgmobs:wildlife";
        public const string Monsters = "canrpgmobs:monsters";
        public const string Everyone = "*";
        // always exist, even if no file defines them
        public static readonly string[] BuiltIns = { Players, Wildlife, Monsters };

        // for pick lists, sorted
        public static string[] WithBuiltIns(IEnumerable<string> ids) => ids.Concat(BuiltIns).Distinct().OrderBy(k => k).ToArray();

        private readonly IReadOnlyDictionary<string, FactionModel> factions;
        private readonly Dictionary<string, HashSet<string>> attackOnSight = new();
        private readonly (string pattern, string faction)[] codeRules;
        // index tables for known factions, AI asks per candidate per tick
        private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
        private readonly FactionRelation[,] relationTable;
        private readonly bool[,] attackTable;
        // only for ids missing from the catalog, like a removed faction
        private readonly ConcurrentDictionary<(string, string), FactionRelation> relationCache = new();
        private readonly ConcurrentDictionary<string, string?> codeCache = new();
        // stored to avoid allocating a lambda on every GetOrAdd
        private readonly Func<(string, string), FactionRelation> computeRelation;
        private readonly Func<string, string?> computeCodeFaction;

        public FactionMatrix(IReadOnlyDictionary<string, FactionModel> factions)
        {
            this.factions = factions;
            computeRelation = k => Explicit(k.Item1, k.Item2) ?? Explicit(k.Item2, k.Item1)
                ?? Explicit(k.Item1, Everyone) ?? Explicit(k.Item2, Everyone)
                ?? FactionRelation.Neutral;
            computeCodeFaction = c =>
            {
                foreach (var (pattern, faction) in codeRules!)
                    if (GlobMatch(pattern, c)) return faction;
                return null;
            };

            // most specific pattern first: game:wolf-male beats game:wolf-*
            codeRules = factions.Values
                .Where(f => f.id != Players && f.members?.entityCodes != null)
                .SelectMany(f => f.members!.entityCodes!.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => (QualifyCode(p), f.id)))
                .OrderByDescending(r => r.Item1.Replace("*", "").Length)
                .ThenBy(r => r.Item2, StringComparer.Ordinal)
                .ToArray();

            var known = factions.Keys.Append(Players).Append(Wildlife).Append(Monsters).Distinct().ToList();
            for (int i = 0; i < known.Count; i++) index[known[i]] = i;

            relationTable = new FactionRelation[known.Count, known.Count];
            for (int a = 0; a < known.Count; a++)
                for (int b = 0; b < known.Count; b++)
                    relationTable[a, b] = a == b ? FactionRelation.Ally : computeRelation((known[a], known[b]));

            foreach (var f in factions.Values)
            {
                var targets = f.attackOnSight != null
                    ? new HashSet<string>(f.attackOnSight.Where(t => !string.IsNullOrWhiteSpace(t)))
                    : new HashSet<string>(known.Where(other => other != f.id && Relation(f.id, other) == FactionRelation.Hostile));
                attackOnSight[f.id] = targets;
            }

            attackTable = new bool[known.Count, known.Count];
            for (int a = 0; a < known.Count; a++)
                for (int b = 0; b < known.Count; b++)
                    attackTable[a, b] = a != b && relationTable[a, b] != FactionRelation.Ally
                        && attackOnSight.TryGetValue(known[a], out var set) && (set.Contains(known[b]) || set.Contains(Everyone));
        }

        public IReadOnlyDictionary<string, FactionModel> Factions => factions;

        public bool Exists(string id) => factions.ContainsKey(id) || id is Players or Wildlife or Monsters;

        // -1 when not in the catalog. Hot callers keep the index instead of the id.
        public int IndexOf(string? id) => id != null && index.TryGetValue(id, out int i) ? i : -1;

        // specific relations beat "*", a relation on either side counts for both
        public FactionRelation Relation(string a, string b)
        {
            if (a == b) return FactionRelation.Ally;
            int ia = IndexOf(a), ib = IndexOf(b);
            if (ia >= 0 && ib >= 0) return relationTable[ia, ib];
            var key = (a, b);
            return relationCache.TryGetValue(key, out var known) ? known : relationCache.GetOrAdd(key, computeRelation);
        }

        public FactionRelation Relation(int a, int b) => a >= 0 && b >= 0 ? relationTable[a, b] : FactionRelation.Neutral;

        public bool IsAlly(string a, string b) => Relation(a, b) == FactionRelation.Ally;

        public bool IsAlly(int a, int b) => a >= 0 && b >= 0 && relationTable[a, b] == FactionRelation.Ally;

        public bool AttacksOnSight(string attacker, string target)
        {
            if (attacker == target) return false;
            int ia = IndexOf(attacker), ib = IndexOf(target);
            if (ia >= 0 && ib >= 0) return attackTable[ia, ib];
            if (IsAlly(attacker, target)) return false;
            return attackOnSight.TryGetValue(attacker, out var set) && (set.Contains(target) || set.Contains(Everyone));
        }

        public bool AttacksOnSight(int attacker, int target) => attacker >= 0 && target >= 0 && attackTable[attacker, target];

        public IReadOnlyCollection<string> AttackTargets(string faction)
            => attackOnSight.TryGetValue(faction, out var set) ? set : (IReadOnlyCollection<string>)Array.Empty<string>();

        public bool HelpsAllies(string faction) => !factions.TryGetValue(faction, out var f) || f.helpAllies;

        // for non-mob entities, via members.entityCodes
        public string? FactionForEntityCode(string? code)
        {
            if (string.IsNullOrEmpty(code) || codeRules.Length == 0) return null;
            return codeCache.TryGetValue(code!, out var known) ? known : codeCache.GetOrAdd(code!, computeCodeFaction);
        }

        public static bool TryParse(string? value, out FactionRelation relation)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "ally": relation = FactionRelation.Ally; return true;
                case "neutral": relation = FactionRelation.Neutral; return true;
                case "hostile": relation = FactionRelation.Hostile; return true;
                default: relation = FactionRelation.Neutral; return false;
            }
        }

        public static string QualifyCode(string pattern)
        {
            pattern = pattern.Trim();
            return pattern.Contains(':') ? pattern : "game:" + pattern;
        }

        // case-insensitive, * matches any run
        public static bool GlobMatch(string pattern, string text)
        {
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (p < pattern.Length && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) { p++; t++; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private FactionRelation? Explicit(string from, string to)
            => factions.TryGetValue(from, out var f) && f.relations != null && f.relations.TryGetValue(to, out var value)
               && TryParse(value, out var relation) ? relation : null;
    }
}
