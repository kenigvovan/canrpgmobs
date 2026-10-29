using System;
using System.Collections.Generic;
using System.Linq;
using canrpgclasses.Core;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Brain;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Spawning
{
    // Position uses internal coords (Y includes the dimension) and becomes the leash anchor.
    // Level null = random from levelRange. Owner = summoner entity id, 0 for normal spawns.
    public readonly record struct MobSpawnRequest(string MobKey, Vec3d Position, int? Level = null, float Yaw = 0f,
        long? Seed = null, string? PresetId = null, string? FactionOverride = null, string? Tier = null, bool Preview = false,
        long Owner = 0, string? ZoneId = null);

    // Spawn API for commands, spawner blocks and dungeon generators.
    public static class MobSpawner
    {
        public static EntityAgent? TrySpawn(ICoreServerAPI sapi, in MobSpawnRequest request, out string? error)
        {
            var catalog = canrpgmobsModSystem.For(sapi)?.Catalog;
            if (catalog == null) { error = "canrpgmobs is not loaded on the server"; return null; }

            var mob = catalog.Resolve(NormalizeKey(request.MobKey), out error);
            if (mob == null) return null;
            string? tierId = ResolveTier(catalog, mob, request.Tier, out error);
            if (tierId == null) return null;
            var tier = catalog.Tier(tierId);

            string? bodyCode = MobBodies.EntityCode(mob.Race.body);
            var type = bodyCode == null ? null : sapi.World.GetEntityType(new AssetLocation(bodyCode));
            if (type == null) { error = $"no entity type for body '{mob.Race.body}'"; return null; }
            if (sapi.World.ClassRegistry.CreateEntity(type) is not EntityAgent agent) { error = "the mob entity class is not an EntityAgent"; return null; }

            long seed = request.Seed ?? sapi.World.Rand.NextInt64();
            int level = request.Level ?? mob.MinLevel + sapi.World.Rand.Next(mob.MaxLevel - mob.MinLevel + 1);
            level = Math.Clamp(level, 1, Math.Max(1, catalog.Scaling.maxLevel));
            var preset = MobResolver.PickPreset(mob.Appearance, seed, request.PresetId);
            WriteIdentity(agent, mob, level, seed, preset, request, tierId, tier);
            WriteAppearance(sapi, agent, mob.Appearance, preset, tier?.size ?? 1f);

            agent.Pos.SetPosWithDimension(request.Position);
            agent.Pos.Yaw = request.Yaw;
            agent.World = sapi.World;
            sapi.World.SpawnEntity(agent);
            // server may silently refuse (repair mode), entity then has no id
            if (agent.EntityId == 0 || !sapi.World.LoadedEntities.ContainsKey(agent.EntityId))
            {
                error = "the server refused the spawn";
                return null;
            }
            MobsApi.RaiseMobSpawned(new MobSpawnedEvent(agent, mob, level, tierId), sapi.Logger);

            error = null;
            return agent;
        }

        // Failed members are skipped, error keeps the last failure.
        // Without requireFreeSpot, members with no free spot are placed at center.
        public static List<EntityAgent> TrySpawnGroup(ICoreServerAPI sapi, string groupId, BlockPos center, int? level, string? factionOverride,
            int maxCount, out string? error, float? radiusOverride = null, bool requireFreeSpot = false, string? zoneId = null)
        {
            var spawned = new List<EntityAgent>();
            error = null;
            var catalog = canrpgmobsModSystem.For(sapi)?.Catalog;
            if (catalog == null) { error = "canrpgmobs is not loaded on the server"; return spawned; }

            string id = IdRules.Qualify(groupId.Trim(), canrpgmobsModSystem.ModId);
            if (!catalog.Groups.TryGetValue(id, out var group)) { error = $"unknown group '{id}'"; return spawned; }
            string? faction = !string.IsNullOrWhiteSpace(factionOverride) ? factionOverride : group.faction;
            float wanted = radiusOverride ?? group.radius;
            float radius = float.IsFinite(wanted) ? Math.Clamp(wanted, 0f, 32f) : 4f;
            var rand = sapi.World.Rand;

            foreach (var member in group.members ?? new List<SpawnGroupMemberModel>())
            {
                if (member == null || string.IsNullOrWhiteSpace(member.mob)) continue;
                int count = member.count is { Length: >= 2 } range ? range[0] + rand.Next(Math.Max(0, range[1] - range[0]) + 1) : 1;
                for (int i = 0; i < count && spawned.Count < maxCount; i++)
                {
                    var found = SpawnPlacement.Find(sapi.World.BlockAccessor, center, radius, rand);
                    if (found == null && requireFreeSpot)
                    {
                        error = SpawnPlacement.NoFreeSpot;
                        continue;
                    }
                    var position = found ?? SpawnPlacement.CenterOf(center);
                    var request = new MobSpawnRequest(member.mob, position, member.level > 0 ? member.level : level, SpawnPlacement.RandomYaw(rand),
                        FactionOverride: faction, Tier: member.tier, ZoneId: zoneId);
                    var agent = TrySpawn(sapi, request, out var memberError);
                    if (agent != null) spawned.Add(agent);
                    else error = memberError;
                }
            }

            if (spawned.Count == 0) error ??= $"group '{id}' has no members to spawn";
            return spawned;
        }

        // explicit tier must exist, a missing default tier falls back to normal
        private static string? ResolveTier(MobCatalog catalog, ResolvedMob mob, string? requested, out string? error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string id = IdRules.Qualify(requested!, canrpgmobsModSystem.ModId);
                if (id == StatMath.DefaultTier || catalog.Tiers.ContainsKey(id)) return id;
                error = $"unknown tier '{id}' (known: {(catalog.Tiers.Count == 0 ? "-" : string.Join(", ", catalog.Tiers.Keys))})";
                return null;
            }
            return mob.DefaultTier != null && catalog.Tiers.ContainsKey(mob.DefaultTier) ? mob.DefaultTier : StatMath.DefaultTier;
        }

        // "goblin|shaman" becomes "canrpgmobs:goblin|canrpgmobs:shaman"
        public static string NormalizeKey(string key) => IdRules.QualifyKey(key, canrpgmobsModSystem.ModId);

        // before SpawnEntity, so behaviors see it in Initialize and it goes out with the spawn packet
        private static void WriteIdentity(EntityAgent agent, ResolvedMob mob, int level, long seed, AppearancePresetModel? preset, in MobSpawnRequest request,
            string tierId, TierModel? tier)
        {
            bool pinned = !string.IsNullOrWhiteSpace(request.FactionOverride);
            var tree = new TreeAttribute();
            tree.SetInt(MobKeys.FormatVersion, MobKeys.CurrentFormatVersion);
            tree.SetString(MobKeys.Key, mob.Key);
            tree.SetInt(MobKeys.Level, level);
            tree.SetLong(MobKeys.Seed, seed);
            tree.SetString(MobKeys.Preset, preset?.id ?? "");
            tree.SetString(MobKeys.Tier, tierId);
            tree.SetBool(MobKeys.Preview, request.Preview);
            tree.SetString(MobKeys.FallbackName, tier?.FormatName(mob.DisplayName("en"), level, "en") ?? mob.DisplayName("en"));
            tree.SetString(MobKeys.Faction, pinned ? IdRules.Qualify(request.FactionOverride!, canrpgmobsModSystem.ModId) : mob.Faction);
            tree.SetBool(MobKeys.FactionOverride, pinned);
            if (request.Owner != 0)
            {
                tree.SetLong(MobKeys.Owner, request.Owner);
                tree.SetBool(MobKeys.NoLoot, true);
            }
            if (request.ZoneId != null) tree.SetString(MobKeys.Zone, request.ZoneId);
            tree.SetDouble(MobKeys.AnchorX, request.Position.X);
            tree.SetDouble(MobKeys.AnchorY, request.Position.Y);
            tree.SetDouble(MobKeys.AnchorZ, request.Position.Z);
            agent.WatchedAttributes[MobKeys.Tree] = tree;
        }

        // Also used to re-dress live mobs after an edit.
        // A filled appliedParts tree stops PlayerModelLib from randomizing the skin server-side.
        internal static void WriteAppearance(ICoreAPI api, EntityAgent agent, AppearanceModel appearance, AppearancePresetModel? preset, float sizeMul)
        {
            var identity = MobKeys.Identity(agent);
            if (identity != null)
            {
                AppearanceSnapshot.Write(identity, appearance, preset, sizeMul);
                MobGear.Write(identity, appearance, preset);
            }
            // core spell anims are seraph codes, other shapes don't have them
            agent.WatchedAttributes.SetBool(RpgCombatApi.SuppressSpellAnimationKey, appearance.kind != AppearanceKinds.Plm);
            if (appearance.kind != AppearanceKinds.Plm) return;

            var wa = agent.WatchedAttributes;
            wa.SetString(MobKeys.PlmModel, appearance.model);
            wa.SetFloat(MobKeys.PlmSize, (appearance.size > 0f ? appearance.size : 1f) * sizeMul);

            var applied = new TreeAttribute();
            // lists and "*" pick one variant per part from the seed
            long seed = identity?.GetLong(MobKeys.Seed) ?? 0;
            foreach (var (part, variant) in AppearanceParts.Resolve(preset?.parts, seed, p => PlmVariants(api, appearance.model, p)))
                applied.SetString(part, variant);
            var skin = new TreeAttribute();
            skin[MobKeys.PlmAppliedParts] = applied;
            wa[MobKeys.PlmSkinConfig] = skin;
        }

        internal static IReadOnlyList<string>? PlmVariants(ICoreAPI api, string? model, string part)
        {
            var models = api.ModLoader.GetModSystem<PlayerModelLib.CustomModelsSystem>();
            if (models == null || string.IsNullOrEmpty(model)) return null;
            string code = models.CustomModels.ContainsKey(model!) || model != AppearanceModel.Seraph ? model! : PlayerModelLib.CustomModelsSystem.SeraphModelCode;
            if (!models.CustomModels.TryGetValue(code, out var data) || !data.SkinParts.TryGetValue(part, out var skinPart)) return null;
            return skinPart.Variants?.Select(v => v.Code).ToList();
        }
    }
}
