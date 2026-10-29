using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using Vintagestory.API.Config;

namespace canrpgmobs.Client.Editor
{
    // Per-section lookup table, so screens don't each switch over section names.
    public partial class MobEditorDialog
    {
        private sealed record SectionInfo(
            Func<CatalogData, IEnumerable<string>> Ids,
            Func<CatalogData, string, object?> Find,
            Func<object, string?>? Name,
            string[] Tabs);

        private static readonly string[] MobTabs = { "main", "look", "gear", "combat", "skills", "loot" };
        private static readonly string[] PlainTabs = { "main", "gear", "combat", "skills", "loot" };
        private static readonly string[] LootTabs = { "table", "test" };
        private static readonly string[] ZoneTabs = { "main", "zspawns", "zdensity", "zshape" };

        private static string? Named(LocText? name) => name?.Resolve(Lang.CurrentLocale, "");

        private static readonly Dictionary<string, SectionInfo> SectionTable = new(StringComparer.Ordinal)
        {
            ["races"] = new(d => d.Races.Keys, (d, id) => d.Races.GetValueOrDefault(id), m => Named(((RaceModel)m).name), MobTabs),
            ["roles"] = new(d => d.Roles.Keys, (d, id) => d.Roles.GetValueOrDefault(id), m => Named(((RoleModel)m).name), PlainTabs),
            ["mobs"] = new(d => d.Templates.Keys, (d, id) => d.Templates.GetValueOrDefault(id), m => Named(((MobTemplateModel)m).name), MobTabs),
            ["aiProfiles"] = new(d => d.AiProfiles.Keys, (d, id) => d.AiProfiles.GetValueOrDefault(id), null, PlainTabs),
            ["factions"] = new(d => d.Factions.Keys, (d, id) => d.Factions.GetValueOrDefault(id), m => Named(((FactionModel)m).name), PlainTabs),
            ["tiers"] = new(d => d.Tiers.Keys, (d, id) => d.Tiers.GetValueOrDefault(id), m => Named(((TierModel)m).name), PlainTabs),
            ["lootTables"] = new(d => d.LootTables.Keys, (d, id) => d.LootTables.GetValueOrDefault(id), null, LootTabs),
            ["animSets"] = new(d => d.AnimSets.Keys, (d, id) => d.AnimSets.GetValueOrDefault(id), null, PlainTabs),
            ["soundSets"] = new(d => d.SoundSets.Keys, (d, id) => d.SoundSets.GetValueOrDefault(id), null, PlainTabs),
            ["groups"] = new(d => d.Groups.Keys, (d, id) => d.Groups.GetValueOrDefault(id), null, PlainTabs),
            ["zones"] = new(d => d.Zones.Keys, (d, id) => d.Zones.GetValueOrDefault(id), m => Named(((ZoneModel)m).name), ZoneTabs)
        };

        private static SectionInfo? Section(string section) => SectionTable.GetValueOrDefault(section);

        // main: what admins make; library: the building blocks mobs are made of
        private static readonly string[] MainSections = { "mobs", "zones", "groups", "lootTables" };
        private static readonly string[] LibrarySections = { "races", "roles", "aiProfiles", "factions", "tiers", "animSets", "soundSets" };

        private static bool InLibrary(string section) => Array.IndexOf(LibrarySections, section) >= 0;
    }
}
