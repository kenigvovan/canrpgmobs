using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    // Preset part values: "skin3" fixed, "hair1|hair4" one of them, "*" any variant of the model.
    // Picked per part from the mob's seed, so the look survives reloads.
    public static class AppearanceParts
    {
        public const char Separator = '|';
        public const string Any = "*";

        public static bool IsRandom(string? value) => value != null && (value.Trim() == Any || value.IndexOf(Separator) >= 0);

        public static string[] Options(string? value)
            => (value ?? "").Split(Separator).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToArray();

        // variantsOf: the model's variants of a part, null when unknown
        public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? parts, long seed, Func<string, IReadOnlyList<string>?> variantsOf)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parts == null) return result;
            foreach (var (part, value) in parts)
            {
                var options = value.Trim() == Any ? variantsOf(part)?.ToArray() ?? Array.Empty<string>() : Options(value);
                if (options.Length == 0) continue;
                result[part] = options.Length == 1 ? options[0] : options[(int)(Hash(seed, part) % (ulong)options.Length)];
            }
            return result;
        }

        // how many looks a preset can give, for the validator
        public static long Combinations(IReadOnlyDictionary<string, string>? parts, Func<string, IReadOnlyList<string>?> variantsOf)
        {
            long total = 1;
            if (parts == null) return total;
            foreach (var (part, value) in parts)
            {
                int count = value.Trim() == Any ? variantsOf(part)?.Count ?? 1 : Options(value).Length;
                total = Math.Min(long.MaxValue / 1000, total * Math.Max(1, count));
            }
            return total;
        }

        // stable across runs, string.GetHashCode is not
        private static ulong Hash(long seed, string part)
        {
            ulong h = 14695981039346656037UL ^ (ulong)seed;
            foreach (char c in part)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            return h;
        }
    }
}
