using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Spawning
{
    // Keeps up to MaxAlive mobs around the block while a player is near.
    // Dungeon generators place the block and call Configure.
    public class BEMobSpawner : BlockEntity
    {
        public const string ClassName = "canrpgmobs.MobSpawner";
        // settings dialog packets
        public const int PacketSave = 1001;
        public const int PacketClear = 1002;
        public const int PacketSpawnNow = 1003;
        private const int MaxSettingsBytes = 8192;
        private const int TickMs = 1000;
        private const int FailedRetryMs = 10000;
        // our mobs in neighbour chunks may load after the spawner
        private const long LoadGraceMs = 10000;
        private const long IdleCheckMs = 3000;
        // saved state, don't rename
        private const string SpawnedAttr = "sp_spawned";
        private const string FilledAttr = "sp_filled";
        private const string ErrorAttr = "sp_error";

        private long initializedAtMs;
        private long nextPlayerCheckMs;

        private readonly HashSet<long> spawned = new();
        private bool filled;
        private long respawnAtMs;
        private string? lastError;

        public SpawnerConfig Config { get; private set; } = new();
        public int AliveCount => spawned.Count;
        // synced for the settings dialog
        public string? LastError => lastError;

        // loaded on the client, for the zone map and outlines
        internal static readonly HashSet<BEMobSpawner> ClientLoaded = new();

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            initializedAtMs = api.World.ElapsedMilliseconds;
            if (api.Side == EnumAppSide.Server) RegisterGameTickListener(OnTick, TickMs, api.World.Rand.Next(TickMs));
            else ClientLoaded.Add(this);
        }

        public override void OnBlockRemoved()
        {
            ClientLoaded.Remove(this);
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            ClientLoaded.Remove(this);
            base.OnBlockUnloaded();
        }

        // server; existing mobs stay and count toward the new MaxAlive
        public void Configure(SpawnerConfig config)
        {
            var clean = config.Clamped();
            if (clean.MobKey != null) clean.MobKey = MobSpawner.NormalizeKey(clean.MobKey);
            if (clean.GroupId != null) clean.GroupId = IdRules.Qualify(clean.GroupId, canrpgmobsModSystem.ModId);
            if (clean.Tier != null) clean.Tier = IdRules.Qualify(clean.Tier, canrpgmobsModSystem.ModId);
            if (clean.Faction != null) clean.Faction = IdRules.Qualify(clean.Faction, canrpgmobsModSystem.ModId);
            Config = clean;
            respawnAtMs = 0;
            lastError = null;
            MarkDirty();
        }

        // error message or null
        public static string? Check(ICoreServerAPI sapi, SpawnerConfig config)
        {
            var catalog = canrpgmobsModSystem.For(sapi)?.Catalog;
            if (catalog == null) return "canrpgmobs is not loaded";
            var clean = config.Clamped();
            if (clean.GroupId != null)
            {
                string group = IdRules.Qualify(clean.GroupId, canrpgmobsModSystem.ModId);
                if (!catalog.Groups.ContainsKey(group)) return $"unknown group '{group}'";
            }
            else if (clean.MobKey != null && catalog.Resolve(MobSpawner.NormalizeKey(clean.MobKey), out var error) == null) return $"'{clean.MobKey}': {error}";
            if (clean.Tier != null)
            {
                string tier = IdRules.Qualify(clean.Tier, canrpgmobsModSystem.ModId);
                if (tier != StatMath.DefaultTier && !catalog.Tiers.ContainsKey(tier)) return $"unknown tier '{tier}'";
            }
            return null;
        }

        // ignores respawn delay and player distance
        public int SpawnNow(ICoreServerAPI sapi)
        {
            if (!Config.IsSet) return 0;
            if (Prune(sapi)) MarkDirty();
            int room = Config.MaxAlive - spawned.Count;
            if (room <= 0) return 0;
            var agents = Spawn(sapi, room, out string? error);
            SetError(error);
            Adopt(sapi, agents);
            return agents.Count;
        }

        public int DespawnAll()
        {
            int removed = 0;
            if (Api is ICoreServerAPI sapi)
            {
                foreach (long id in spawned)
                {
                    if (!sapi.World.LoadedEntities.TryGetValue(id, out var entity) || !entity.Alive) continue;
                    entity.Die(EnumDespawnReason.Removed);
                    removed++;
                }
            }
            spawned.Clear();
            filled = false;
            respawnAtMs = 0;
            MarkDirty();
            return removed;
        }

        public string Describe()
        {
            if (!Config.IsSet) return $"Mob spawner at {Pos}: off. Look at it and use /canrpgmobs spawner set &lt;race|role|template|group:id|zone&gt;.";
            var sb = new StringBuilder();
            string what = Config.FromZone
                ? ZoneHere(Api) is { } zone ? "zone " + zone.Model.id : "its zone (none here)"
                : Config.GroupId != null ? "group " + Config.GroupId : Config.MobKey!;
            sb.Append($"Mob spawner at {Pos}: {what}, {spawned.Count}/{Config.MaxAlive} alive, ");
            sb.Append($"respawn {Config.RespawnSeconds:0}s, radius {Config.Radius:0.#}, active within {Config.ActivationRange:0} blocks");
            if (Config.Level > 0) sb.Append($", level {Config.Level}");
            if (Config.Tier != null) sb.Append($", tier {Config.Tier}");
            if (Config.Faction != null) sb.Append($", faction {Config.Faction}");
            if (lastError != null) sb.Append($"; last problem: {lastError}");
            return sb.ToString();
        }

        private void OnTick(float dt)
        {
            if (Api is not ICoreServerAPI sapi) return;
            // chunk replaced under us (reused Manifold dimension), stop
            if (sapi.World.BlockAccessor.GetBlockEntity(Pos) != this)
            {
                UnregisterAllTickListeners();
                return;
            }
            if (!Config.IsSet) return;
            long now = sapi.World.ElapsedMilliseconds;
            if (now < nextPlayerCheckMs) return;
            if (!PlayerNear(sapi))
            {
                // check idle spawners less often
                nextPlayerCheckMs = now + IdleCheckMs;
                return;
            }
            if (spawned.Count > 0 && now - initializedAtMs < LoadGraceMs) return;
            if (Prune(sapi)) MarkDirty();

            int room = Config.MaxAlive - spawned.Count;
            if (room <= 0)
            {
                respawnAtMs = 0;
                return;
            }
            if (respawnAtMs == 0) respawnAtMs = now + (filled ? (long)(Config.RespawnSeconds * 1000f) : 0);
            if (now < respawnAtMs) return;

            var agents = Spawn(sapi, room, out string? error);
            if (error != null && error != lastError) sapi.Logger.Warning("[canrpgmobs] mob spawner at {0}: {1}", Pos, error);
            SetError(error);
            if (agents.Count == 0)
            {
                respawnAtMs = now + FailedRetryMs;
                return;
            }
            Adopt(sapi, agents);
        }

        private void Adopt(ICoreServerAPI sapi, List<EntityAgent> agents)
        {
            if (agents.Count == 0) return;
            respawnAtMs = 0;
            filled = true;
            foreach (var agent in agents) spawned.Add(agent.EntityId);
            MarkDirty();
            MobsApi.RaiseSpawnerSpawned(new SpawnerSpawnedEvent(new BlockPos(Pos.X, Pos.Y, Pos.Z, Pos.dimension), Config, agents), sapi.Logger);
        }

        private void SetError(string? error)
        {
            if (error == lastError) return;
            lastError = error;
            MarkDirty();
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(fromPlayer, packetid, data);
            if (Api is not ICoreServerAPI sapi || fromPlayer is not IServerPlayer player) return;
            if (packetid is not (PacketSave or PacketClear or PacketSpawnNow)) return;
            if (!player.HasPrivilege(Privilege.controlserver))
            {
                sapi.Logger.Audit("{0} without controlserver tried to change the mob spawner at {1}", player.PlayerName, Pos);
                return;
            }

            try
            {
                if (packetid == PacketSave) OnSavePacket(sapi, player, data);
                else if (packetid == PacketClear) OnClearPacket(player);
                else OnSpawnNowPacket(sapi, player);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[canrpgmobs] mob spawner at {0}: a settings packet from {1} failed: {2}", Pos, player.PlayerName, e);
            }
        }

        private void OnSavePacket(ICoreServerAPI sapi, IServerPlayer player, byte[] data)
        {
            if (data == null || data.Length > MaxSettingsBytes) return;
            var config = JsonConvert.DeserializeObject<SpawnerConfig>(Encoding.UTF8.GetString(data));
            if (config == null) return;
            if (Check(sapi, config) is { } problem)
            {
                Tell(player, "Mob spawner not saved: " + problem);
                return;
            }
            Configure(config);
            Tell(player, Describe());
        }

        private void OnClearPacket(IServerPlayer player)
        {
            int removed = DespawnAll();
            Configure(new SpawnerConfig());
            Tell(player, $"Removed {removed} mob(s); the spawner is off.");
        }

        private void OnSpawnNowPacket(ICoreServerAPI sapi, IServerPlayer player)
        {
            int count = SpawnNow(sapi);
            Tell(player, count > 0 ? $"Spawned {count} mob(s)." : $"Nothing spawned: {lastError ?? (Config.IsSet ? "the spawner is full" : "the spawner is off")}.");
        }

        private static void Tell(IServerPlayer player, string message)
            => player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);

        // client too, for the block info and the dialog
        public ZoneShape? ZoneHere(ICoreAPI api)
            => canrpgmobsModSystem.For(api)?.Catalog.ZoneIndex.At(Pos.dimension, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);

        // the zone's table, level and tier; the spawner's own level, tier and faction win.
        // Not tagged with the zone, so they don't eat its budget.
        private List<EntityAgent> SpawnFromZone(ICoreServerAPI sapi, int room, out string? error)
        {
            var agents = new List<EntityAgent>();
            error = null;
            if (ZoneHere(sapi)?.Model is not { } zone)
            {
                error = "set to use the zone, but no zone covers this block";
                return agents;
            }
            var rand = sapi.World.Rand;
            bool day = ZoneSpawner.IsDay(sapi, Pos.X, Pos.Z);
            while (agents.Count < room)
            {
                if (ZoneSpawner.Pick(zone, day, rand) is not { } entry)
                {
                    if (agents.Count == 0) error = $"zone {zone.id} has nothing to spawn {(day ? "by day" : "at night")}";
                    break;
                }
                int? level = Config.Level > 0 ? Config.Level : ZoneSpawner.Roll(entry.level ?? zone.level, rand);
                string? tier = Config.Tier ?? ZoneSpawner.EntryTier(zone, entry, rand);
                string? faction = Config.Faction ?? zone.faction;
                if (!string.IsNullOrWhiteSpace(entry.group))
                {
                    var made = MobSpawner.TrySpawnGroup(sapi, entry.group!, Pos, level, faction, room - agents.Count, out error, Config.Radius, requireFreeSpot: true);
                    if (made.Count == 0) break;
                    agents.AddRange(made);
                    continue;
                }
                if (SpawnOne(sapi, entry.mob!, level, tier, faction, out error) is not { } agent) break;
                agents.Add(agent);
            }
            return agents;
        }

        // at a free spot within Radius; null = no spot or the spawn failed
        private EntityAgent? SpawnOne(ICoreServerAPI sapi, string mobKey, int? level, string? tier, string? faction, out string? error)
        {
            var rand = sapi.World.Rand;
            if (SpawnPlacement.Find(sapi.World.BlockAccessor, Pos, Config.Radius, rand) is not { } position)
            {
                error = SpawnPlacement.NoFreeSpot;
                return null;
            }
            var request = new MobSpawnRequest(mobKey, position, level, SpawnPlacement.RandomYaw(rand), FactionOverride: faction, Tier: tier);
            return MobSpawner.TrySpawn(sapi, request, out error);
        }

        private List<EntityAgent> Spawn(ICoreServerAPI sapi, int room, out string? error)
        {
            if (Config.FromZone) return SpawnFromZone(sapi, room, out error);
            int? level = Config.Level > 0 ? Config.Level : null;
            if (Config.GroupId != null)
                return MobSpawner.TrySpawnGroup(sapi, Config.GroupId, Pos, level, Config.Faction, room, out error, Config.Radius, requireFreeSpot: true);

            var agents = new List<EntityAgent>();
            error = null;
            for (int i = 0; i < room; i++)
            {
                // no spot, retry later
                if (SpawnOne(sapi, Config.MobKey!, level, Config.Tier, Config.Faction, out error) is not { } agent) break;
                agents.Add(agent);
            }
            return agents;
        }

        private bool PlayerNear(ICoreServerAPI sapi)
        {
            if (canrpgmobsModSystem.For(sapi) is not { } mod) return false;
            double rangeSq = Config.ActivationRange * Config.ActivationRange;
            foreach (var spot in mod.PlayerSpots(sapi))
            {
                if (spot.Dimension != Pos.dimension) continue;
                double dx = spot.X - (Pos.X + 0.5), dy = spot.Y - Pos.Y, dz = spot.Z - (Pos.Z + 0.5);
                if (dx * dx + dy * dy + dz * dz <= rangeSq) return true;
            }
            return false;
        }

        // mobs in unloaded chunks are forgotten too, like vanilla; leash makes it rare
        private bool Prune(ICoreServerAPI sapi)
        {
            int before = spawned.Count;
            spawned.RemoveWhere(id => !sapi.World.LoadedEntities.TryGetValue(id, out var entity) || !entity.Alive);
            return spawned.Count != before;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            Config.ToTree(tree);
            tree[SpawnedAttr] = new LongArrayAttribute(spawned.ToArray());
            tree.SetBool(FilledAttr, filled);
            tree.SetString(ErrorAttr, lastError ?? "");
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            Config = SpawnerConfig.FromTree(tree);
            spawned.Clear();
            if (tree[SpawnedAttr] is LongArrayAttribute ids && ids.value != null)
                foreach (long id in ids.value) spawned.Add(id);
            filled = tree.GetBool(FilledAttr);
            string? error = tree.GetString(ErrorAttr);
            lastError = string.IsNullOrEmpty(error) ? null : error;
        }

        // pasted from a schematic, entity ids are from another world
        public override void OnLoadCollectibleMappings(IWorldAccessor worldForNewMappings, Dictionary<int, AssetLocation> oldBlockIdMapping,
            Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
        {
            base.OnLoadCollectibleMappings(worldForNewMappings, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
            spawned.Clear();
            filled = false;
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            if (!Config.IsSet)
            {
                dsc.AppendLine(Lang.Get("canrpgmobs:spawner-off"));
                return;
            }
            string what = Config.FromZone
                ? ZoneHere(Api) is { } zone ? Lang.Get("canrpgmobs:spawner-from-zone", ZoneText.Name(zone.Model, Lang.CurrentLocale)) : Lang.Get("canrpgmobs:spawner-no-zone")
                : Config.GroupId ?? Config.MobKey ?? "";
            dsc.AppendLine(Lang.Get("canrpgmobs:spawner-spawns", what));
            dsc.AppendLine(Lang.Get("canrpgmobs:spawner-alive", spawned.Count, Config.MaxAlive, Config.RespawnSeconds.ToString("0"), Config.ActivationRange.ToString("0")));
        }
    }
}
