using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public static class MobResolver
    {
        public const int MaxAliasHops = 8;

        private static readonly AiProfileModel DefaultAi = new() { id = "default" };

        // hop limit guards against cycles
        public static string ResolveAlias(string id, IReadOnlyDictionary<string, string> aliases)
        {
            for (int i = 0; i < MaxAliasHops && aliases.TryGetValue(id, out var next); i++) id = next;
            return id;
        }

        // key is a qualified template id or race|role
        public static ResolvedMob? Resolve(string key, CatalogData data, Func<string, bool>? spellUsable, out string? error)
        {
            key = ResolveAlias(key.Trim(), data.Aliases);
            MobTemplateModel? template = null;
            string raceId, roleId;

            int sep = key.IndexOf(IdRules.KeySeparator);
            if (sep < 0)
            {
                if (!data.Templates.TryGetValue(key, out template))
                {
                    error = $"unknown mob template '{key}' (use a template id or race{IdRules.KeySeparator}role)";
                    return null;
                }
                raceId = ResolveAlias(template.race, data.Aliases);
                roleId = ResolveAlias(template.role, data.Aliases);
            }
            else
            {
                if (sep == 0 || sep == key.Length - 1)
                {
                    error = $"mob key '{key}' must look like race{IdRules.KeySeparator}role";
                    return null;
                }
                raceId = ResolveAlias(key.Substring(0, sep), data.Aliases);
                roleId = ResolveAlias(key.Substring(sep + 1), data.Aliases);
                key = raceId + IdRules.KeySeparator + roleId;
            }

            if (!data.Races.TryGetValue(raceId, out var race)) { error = $"unknown race '{raceId}'"; return null; }
            if (!data.Roles.TryGetValue(roleId, out var role)) { error = $"unknown role '{roleId}'"; return null; }

            string? aiId = template?.aiProfile ?? role.aiProfile;
            var ai = aiId != null && data.AiProfiles.TryGetValue(ResolveAlias(aiId, data.Aliases), out var profile) ? profile : DefaultAi;

            error = null;
            return new ResolvedMob(key, race, role, template, ai, spellUsable, data);
        }

        // nearer sets win, stops at unknown ids and loops
        public static Dictionary<string, TEntry> FlattenSet<TSet, TEntry>(string? setId, IReadOnlyDictionary<string, TSet> sets,
            IReadOnlyDictionary<string, string> aliases, Func<TSet, string?> extends, Func<TSet, Dictionary<string, TEntry>?> entries)
            where TEntry : class
        {
            var chain = new List<TSet>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string? id = setId; id != null && chain.Count < MaxAliasHops;)
            {
                id = ResolveAlias(id, aliases);
                if (!seen.Add(id) || !sets.TryGetValue(id, out var set)) break;
                chain.Add(set);
                id = extends(set);
            }

            var result = new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var own = entries(chain[i]);
                if (own == null) continue;
                foreach (var pair in own) if (pair.Value != null) result[pair.Key] = pair.Value;
            }
            return result;
        }

        // deterministic per seed, explicit presetId wins
        public static AppearancePresetModel? PickPreset(AppearanceModel appearance, long seed, string? presetId = null)
        {
            var presets = appearance.presets;
            if (presets == null || presets.Count == 0) return null;
            if (!string.IsNullOrEmpty(presetId))
            {
                var named = presets.FirstOrDefault(p => p.id == presetId);
                if (named != null) return named;
            }

            double total = 0;
            foreach (var p in presets) total += Weight(p);
            if (total <= 0) return presets[0];

            double roll = UnitFromSeed(seed) * total;
            foreach (var p in presets)
            {
                roll -= Weight(p);
                if (roll < 0) return p;
            }
            return presets[presets.Count - 1];
        }

        private static double Weight(AppearancePresetModel p) => float.IsFinite(p.weight) ? Math.Max(0f, p.weight) : 0f;

        // SplitMix64, new Random(seed) gives correlated values for close seeds
        public static double UnitFromSeed(long seed)
        {
            ulong z = (ulong)seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
