using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canrpgmobs
{
    // /canrpgmobs spawngroup, spawner, audit
    public partial class canrpgmobsModSystem
    {
        private const string GroupPrefix = "group:";

        private TextCommandResult OnSpawnGroup(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not EntityPlayer player) return NoPlayer();

            string id = (string)args[0];
            int level = (int)args[1];
            var spawned = SpawnGroupAhead(player, id, level > 0 ? level : null, out var error);
            return spawned.Count > 0
                ? TextCommandResult.Success($"Spawned {spawned.Count} mob(s) of {IdRules.Qualify(id, ModId)}." + (error != null ? $" Some failed: {error}" : ""))
                : TextCommandResult.Error($"Could not spawn the group: {error}. Known groups: {KnownGroups()}");
        }

        private List<EntityAgent> SpawnGroupAhead(EntityPlayer player, string groupId, int? level, out string? error)
        {
            var ahead = player.Pos.XYZ.AheadCopy(4.0, 0f, player.Pos.Yaw);
            var center = player.Pos.AsBlockPos;
            center.Add((int)Math.Round(ahead.X - player.Pos.X), 0, (int)Math.Round(ahead.Z - player.Pos.Z));
            return MobSpawner.TrySpawnGroup(sapi!, groupId, center, level, null, 50, out error);
        }

        private TextCommandResult OnSpawnerSet(TextCommandCallingArgs args)
        {
            if (LookedAtSpawner(args) is not { } spawner) return TextCommandResult.Error("Look at a mob spawner block first.");

            string what = ((string)args[0]).Trim();
            var config = new SpawnerConfig
            {
                MaxAlive = (int)args[1], RespawnSeconds = (int)args[2], Radius = (int)args[3], ActivationRange = (int)args[4], Level = (int)args[5]
            };

            if (TierArg(args[6]) is { } tierArg)
            {
                string tier = IdRules.Qualify(tierArg, ModId);
                if (tier != StatMath.DefaultTier && !Catalog.Tiers.ContainsKey(tier)) return TextCommandResult.Error($"Unknown tier '{tier}'.");
                config.Tier = tier;
            }

            if (what.Equals("zone", StringComparison.OrdinalIgnoreCase)) config.UseZone = true;
            else if (what.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string group = IdRules.Qualify(what.Substring(GroupPrefix.Length), ModId);
                if (!Catalog.Groups.ContainsKey(group)) return TextCommandResult.Error($"Unknown group '{group}'. Known groups: {KnownGroups()}");
                config.GroupId = group;
            }
            else
            {
                string key = MobSpawner.NormalizeKey(what);
                if (Catalog.Resolve(key, out var error) == null) return TextCommandResult.Error($"'{key}': {error}");
                config.MobKey = key;
            }

            spawner.Configure(config);
            return TextCommandResult.Success(spawner.Describe());
        }

        private TextCommandResult OnSpawnerInfo(TextCommandCallingArgs args)
            => LookedAtSpawner(args) is { } spawner
                ? TextCommandResult.Success(spawner.Describe())
                : TextCommandResult.Error("Look at a mob spawner block first.");

        private TextCommandResult OnSpawnerClear(TextCommandCallingArgs args)
        {
            if (LookedAtSpawner(args) is not { } spawner) return TextCommandResult.Error("Look at a mob spawner block first.");
            int removed = spawner.DespawnAll();
            spawner.Configure(new SpawnerConfig());
            return TextCommandResult.Success($"Removed {removed} mob(s); the spawner is off.");
        }

        // leak check for temporary dimensions (Manifold)
        private TextCommandResult OnAudit(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server only.");

            int prunedMobs = Tracker.PruneMissing(sapi.World);
            int prunedTraces = BrainTrace.PruneMissing(sapi.World);
            int loaded = 0, untracked = 0;
            foreach (var entity in sapi.World.LoadedEntities.Values)
            {
                if (entity is not EntityRpgMob) continue;
                loaded++;
                if (entity.GetBehavior<EBRpgMob>() is not { } mob || !Tracker.Contains(mob)) untracked++;
            }

            bool clean = prunedMobs == 0 && prunedTraces == 0 && untracked == 0;
            return TextCommandResult.Success($"Mobs loaded: {loaded}, tracked: {Tracker.Count} ({Tracker.Pending} waiting for a catalog update). "
                                             + $"Hanging trackers removed now: {prunedMobs}; brain traces of gone mobs: {prunedTraces}; loaded mobs without a tracker: {untracked}. "
                                             + (clean ? "All clean." : "See above."));
        }

        private BEMobSpawner? LookedAtSpawner(TextCommandCallingArgs args)
            => sapi != null && args.Caller.Player?.CurrentBlockSelection?.Position is { } pos
                ? sapi.World.BlockAccessor.GetBlockEntity(pos) as BEMobSpawner
                : null;

        private string KnownGroups() => JoinOrDash(Catalog.Groups.Keys);
    }
}
