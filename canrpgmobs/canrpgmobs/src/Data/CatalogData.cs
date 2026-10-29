using System.Collections.Generic;

namespace canrpgmobs.Data
{
    // merged content of all files, keyed by qualified id
    public sealed class CatalogData
    {
        public Dictionary<string, RaceModel> Races { get; } = new();
        public Dictionary<string, RoleModel> Roles { get; } = new();
        public Dictionary<string, MobTemplateModel> Templates { get; } = new();
        public Dictionary<string, AiProfileModel> AiProfiles { get; } = new();
        public Dictionary<string, FactionModel> Factions { get; } = new();
        public Dictionary<string, TierModel> Tiers { get; } = new();
        public Dictionary<string, LootTableModel> LootTables { get; } = new();
        public ScalingModel? Scaling { get; set; }
        public Dictionary<string, AnimSetModel> AnimSets { get; } = new();
        public Dictionary<string, SoundSetModel> SoundSets { get; } = new();
        public Dictionary<string, SpawnGroupModel> Groups { get; } = new();
        public Dictionary<string, ZoneModel> Zones { get; } = new();
        public Dictionary<string, string> Aliases { get; } = new();
        // language code -> lang key -> text
        public Dictionary<string, Dictionary<string, string>> Lang { get; } = new();

        // everything with a translatable name; kind is the lang key prefix
        public IEnumerable<(string Kind, string Id, LocText? Name)> NamedEntries()
        {
            foreach (var (id, m) in Templates) yield return ("mob", id, m.name);
            foreach (var (id, m) in Races) yield return ("race", id, m.name);
            foreach (var (id, m) in Roles) yield return ("role", id, m.name);
            foreach (var (id, m) in Zones) yield return ("zone", id, m.name);
            foreach (var (id, m) in Groups) yield return ("group", id, m.name);
            foreach (var (id, m) in Tiers) yield return ("tier", id, m.name);
        }

        public RaceModel? RaceOf(MobTemplateModel template)
            => Races.TryGetValue(MobResolver.ResolveAlias(template.race, Aliases), out var race) ? race : null;
    }

    public enum SpellSupport { Supported, Degraded, Unsupported }

    // keeps the data layer independent of the spell system
    public interface ISpellInfoSource
    {
        bool Exists(string spellId);
        SpellSupport Rate(string spellId, out string[] reasons);
    }
}
