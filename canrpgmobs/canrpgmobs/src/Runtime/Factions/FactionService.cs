using canrpgclasses.Core;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Factions
{
    public static class FactionService
    {
        // Mobs: identity tree. Players and player-guarded entities: players. Others: by entity code, else wildlife.
        public static string FactionOf(Entity e, MobCatalog catalog)
        {
            if (e is EntityRpgMob)
            {
                var tree = MobKeys.Identity(e);
                string? faction = tree?.GetString(MobKeys.Faction);
                if (!string.IsNullOrEmpty(faction)) return faction!;
                string? key = tree?.GetString(MobKeys.Key);
                return (key != null ? catalog.Resolve(key)?.Faction : null) ?? FactionMatrix.Monsters;
            }
            if (e is EntityPlayer || !string.IsNullOrEmpty(e.WatchedAttributes.GetString(AttrKeys.GuardedByPlayerUid))) return FactionMatrix.Players;
            return catalog.Factions.FactionForEntityCode(e.Code?.ToString()) ?? FactionMatrix.Wildlife;
        }

        public static bool AreAllies(Entity a, Entity b, MobCatalog catalog)
            => a == b || catalog.Factions.IsAlly(FactionOf(a, catalog), FactionOf(b, catalog));

        // canrpgclasses ally hook; null leaves non-mob pairs to the core's party rules
        public static bool? ResolveAlly(Entity caster, Entity target)
        {
            if (caster is not EntityRpgMob && target is not EntityRpgMob) return null;
            var catalog = canrpgmobsModSystem.For(caster.Api)?.Catalog;
            if (catalog == null) return null;
            return AreAllies(caster, target, catalog);
        }
    }
}
