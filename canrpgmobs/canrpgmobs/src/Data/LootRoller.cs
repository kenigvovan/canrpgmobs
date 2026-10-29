using System;
using System.Collections.Generic;

namespace canrpgmobs.Data
{
    // Type is item or block
    public readonly record struct LootDrop(string Type, string Code, int Count);

    public static class LootRoller
    {
        // also breaks table cycles
        public const int MaxDepth = 4;

        public static List<LootDrop> Roll(IReadOnlyList<string> tableIds, IReadOnlyDictionary<string, LootTableModel> tables, int level, TierModel? tier, Random rng)
        {
            var drops = new List<LootDrop>();
            string tierId = tier?.id ?? StatMath.DefaultTier;
            foreach (var id in tableIds)
                if (tables.TryGetValue(id, out var table))
                    RollTable(table, tables, level, tierId, tier?.lootRolls ?? 0, 0, rng, drops);
            return Merge(drops);
        }

        private static void RollTable(LootTableModel table, IReadOnlyDictionary<string, LootTableModel> tables, int level, string tierId,
            int bonusRolls, int depth, Random rng, List<LootDrop> drops)
        {
            if (table.guaranteed != null)
            {
                foreach (var entry in table.guaranteed)
                    if (Eligible(entry, level, tierId) && rng.NextDouble() < entry.chance)
                        Drop(entry, table, tables, level, tierId, depth, rng, drops);
            }

            var entries = table.entries;
            if (entries == null || entries.Count == 0) return;

            double total = Math.Max(0f, table.emptyWeight);
            foreach (var entry in entries)
                if (Eligible(entry, level, tierId)) total += Math.Max(0f, entry.weight);
            if (total <= 0) return;

            int rolls = RollRange(table.rolls, rng) + (table.tierRollBonus && depth == 0 ? Math.Max(0, bonusRolls) : 0);
            for (int r = 0; r < rolls; r++)
            {
                double pick = rng.NextDouble() * total;
                foreach (var entry in entries)
                {
                    if (!Eligible(entry, level, tierId)) continue;
                    pick -= Math.Max(0f, entry.weight);
                    if (pick >= 0) continue;
                    if (rng.NextDouble() < entry.chance) Drop(entry, table, tables, level, tierId, depth, rng, drops);
                    break;
                }
                // fell through = empty weight picked
            }
        }

        // editor only: drop chance per pick, ignores level and tier limits
        public static float PickChance(float weight, float chance, float totalWeight, bool guaranteed)
        {
            float clamped = Math.Clamp(chance, 0f, 1f);
            if (guaranteed) return clamped;
            return totalWeight <= 0 ? 0f : Math.Max(0f, weight) / totalWeight * clamped;
        }

        private static bool Eligible(LootEntryModel? entry, int level, string tierId)
            => entry != null && !string.IsNullOrWhiteSpace(entry.code) && Array.IndexOf(LootEntryModel.Types, entry.type) >= 0
               && level >= entry.minLevel && (entry.tiers == null || entry.tiers.Contains(tierId));

        private static void Drop(LootEntryModel entry, LootTableModel owner, IReadOnlyDictionary<string, LootTableModel> tables,
            int level, string tierId, int depth, Random rng, List<LootDrop> drops)
        {
            if (entry.type == LootEntryModel.Table)
            {
                // no tier bonus rolls for nested tables
                if (depth + 1 < MaxDepth && tables.TryGetValue(entry.code, out var nested))
                    RollTable(nested, tables, level, tierId, 0, depth + 1, rng, drops);
                return;
            }

            int count = (int)Math.Round(RollRange(entry.count, rng) * StatMath.Factor(owner.levelCountScale, level - 1));
            if (count > 0) drops.Add(new LootDrop(entry.type, entry.code, count));
        }

        private static int RollRange(int[]? range, Random rng)
        {
            if (range is not { Length: >= 2 }) return 1;
            int min = Math.Max(0, range[0]), max = Math.Max(min, range[1]);
            return rng.Next(min, max + 1);
        }

        private static List<LootDrop> Merge(List<LootDrop> drops)
        {
            if (drops.Count < 2) return drops;
            var merged = new List<LootDrop>();
            var index = new Dictionary<(string, string), int>();
            foreach (var drop in drops)
            {
                if (index.TryGetValue((drop.Type, drop.Code), out int at)) merged[at] = drop with { Count = merged[at].Count + drop.Count };
                else
                {
                    index[(drop.Type, drop.Code)] = merged.Count;
                    merged.Add(drop);
                }
            }
            return merged;
        }
    }
}
