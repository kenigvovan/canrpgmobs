using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public static partial class MobValidator
    {
        private static void CheckLootRefs(List<string>? loot, string path, Scope scope)
        {
            if (loot == null) return;
            foreach (var id in loot)
                if (!string.IsNullOrWhiteSpace(id) && !scope.Data.LootTables.ContainsKey(id))
                    scope.Report.Warning(path, $"unknown loot table '{id}', nothing drops from it");
        }

        private static void CheckLootTable(LootTableModel table, CatalogData data, ValidationReport report, Rejected rejected)
        {
            string path = Path(table.source, "lootTable", table.id);
            if (table.rolls != null && !IntRange(table.rolls, 0, 100))
            {
                report.Error(path, "rolls must be [min, max] between 0 and 100, with min not above max");
                rejected.LootTables.Add(table.id);
            }
            if (!NonNegative(table.emptyWeight)) report.Warning(path, "emptyWeight must be 0 or more");
            if (!InRange(table.levelCountScale, 0f, 10f)) report.Warning(path, "levelCountScale should be between 0 and 10");
            if (table.entries is not { Count: > 0 } && table.guaranteed is not { Count: > 0 })
                report.Warning(path, "no entries and nothing guaranteed, the table never drops anything");
            CheckLootEntries(table.entries, path + " > entries", data, report);
            CheckLootEntries(table.guaranteed, path + " > guaranteed", data, report);
        }

        private static void CheckLootEntries(List<LootEntryModel>? entries, string path, CatalogData data, ValidationReport report)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null) continue;
                string at = $"{path}[{i}] {e.code}";
                if (!LootEntryModel.Types.Contains(e.type)) { report.Warning(at, $"type '{e.type}' is not item, block or table - entry skipped"); continue; }
                if (string.IsNullOrWhiteSpace(e.code)) { report.Warning(at, "code is empty - entry skipped"); continue; }
                if (e.type == LootEntryModel.Table)
                {
                    if (!data.LootTables.ContainsKey(e.code)) report.Warning(at, $"unknown loot table '{e.code}'");
                    if (e.count != null) report.Info(at, "count is not read for table entries");
                }
                else if (e.count != null && !IntRange(e.count, 0, 1000))
                    report.Warning(at, "count must be [min, max] between 0 and 1000, with min not above max");
                if (!NonNegative(e.weight)) report.Warning(at, "weight must be 0 or more");
                if (!InRange(e.chance, 0f, 1f)) report.Warning(at, "chance must be between 0 and 1");
                if (e.minLevel < 0) report.Warning(at, "minLevel must be 0 or more");
                if (e.tiers != null)
                {
                    foreach (var tier in e.tiers)
                        if (tier != StatMath.DefaultTier && !data.Tiers.ContainsKey(tier))
                            report.Warning(at, $"unknown tier '{tier}', the entry never drops for it");
                }
            }
        }

        // cycles are harmless (depth cap) but almost always a mistake
        private static void CheckLootCycles(CatalogData data, ValidationReport report)
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on path, 2 = done
            var path = new List<string>();
            foreach (var id in data.LootTables.Keys) Visit(id);

            void Visit(string id)
            {
                if (state.TryGetValue(id, out int seen))
                {
                    if (seen == 1)
                    {
                        var loop = path.Skip(path.IndexOf(id)).Append(id);
                        report.Warning($"lootTable {id}", $"loot tables form a cycle: {string.Join(" > ", loop)}; nested rolls stop after {LootRoller.MaxDepth} levels");
                    }
                    return;
                }
                if (!data.LootTables.TryGetValue(id, out var table)) return;

                state[id] = 1;
                path.Add(id);
                foreach (var entry in (table.entries ?? new List<LootEntryModel>()).Concat(table.guaranteed ?? new List<LootEntryModel>()))
                    if (entry?.type == LootEntryModel.Table && !string.IsNullOrEmpty(entry.code)) Visit(entry.code);
                path.RemoveAt(path.Count - 1);
                state[id] = 2;
            }
        }
    }
}
