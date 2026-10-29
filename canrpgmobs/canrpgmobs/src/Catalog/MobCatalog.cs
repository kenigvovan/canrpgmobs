using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using canrpgmobs.Data;

namespace canrpgmobs.Catalog
{
    // immutable, replaced as a whole on reload
    public sealed class MobCatalog
    {
        public static readonly MobCatalog Empty = new(new CatalogData(), null);

        public CatalogData Data { get; }
        public IReadOnlyDictionary<string, RaceModel> Races => Data.Races;
        public IReadOnlyDictionary<string, RoleModel> Roles => Data.Roles;
        public IReadOnlyDictionary<string, MobTemplateModel> Templates => Data.Templates;
        public IReadOnlyDictionary<string, string> Aliases => Data.Aliases;
        public FactionMatrix Factions { get; }
        public IReadOnlyDictionary<string, TierModel> Tiers => Data.Tiers;
        public IReadOnlyDictionary<string, LootTableModel> LootTables => Data.LootTables;
        public IReadOnlyDictionary<string, SpawnGroupModel> Groups => Data.Groups;
        public IReadOnlyDictionary<string, ZoneModel> Zones => Data.Zones;
        public ZoneIndex ZoneIndex => zoneIndex ??= new ZoneIndex(Data.Zones.Values);
        public ScalingModel Scaling => Data.Scaling ?? DefaultScaling;

        private ZoneIndex? zoneIndex;

        private static readonly ScalingModel DefaultScaling = new();

        // null = normal, also for removed tiers
        public TierModel? Tier(string? id) => id != null && Data.Tiers.TryGetValue(id, out var tier) ? tier : null;

        private readonly ISpellInfoSource? spells;
        private readonly ConcurrentDictionary<string, ResolvedMob?> resolved = new();

        public MobCatalog(CatalogData data, ISpellInfoSource? spells)
        {
            Data = data;
            this.spells = spells;
            Factions = new FactionMatrix(data.Factions);
            resolve = k => MobResolver.Resolve(k, Data, SpellUsable, out _);
        }

        // stored to avoid a lambda alloc per call, nameplates and AI hit this a lot
        private readonly Func<string, ResolvedMob?> resolve;

        public ResolvedMob? Resolve(string key)
            => resolved.TryGetValue(key, out var mob) ? mob : resolved.GetOrAdd(key, resolve);

        // uncached, with error text (commands, spawning)
        public ResolvedMob? Resolve(string key, out string? error)
            => MobResolver.Resolve(key, Data, SpellUsable, out error);

        public SpellSupport RateSpell(string spellId, out string[] reasons)
        {
            if (spells == null) { reasons = new[] { "spell system not available" }; return SpellSupport.Unsupported; }
            return spells.Rate(spellId, out reasons);
        }

        private bool SpellUsable(string spellId)
            => spells != null && !string.IsNullOrWhiteSpace(spellId) && spells.Exists(spellId)
               && spells.Rate(spellId, out _) != SpellSupport.Unsupported;
    }
}
