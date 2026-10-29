using System;
using System.Collections.Generic;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Brain
{
    public sealed class MobSpawnedEvent
    {
        public MobSpawnedEvent(EntityAgent entity, ResolvedMob mob, int level, string tierId)
        {
            Entity = entity;
            Mob = mob;
            Level = level;
            TierId = tierId;
        }

        public EntityAgent Entity { get; }
        public ResolvedMob Mob { get; }
        public int Level { get; }
        public string TierId { get; }
    }

    public sealed class MobDiedEvent
    {
        public MobDiedEvent(Entity entity, ResolvedMob mob, MobStats stats, DamageSource? cause, List<LootDrop> drops)
        {
            Entity = entity;
            Mob = mob;
            Stats = stats;
            Cause = cause;
            Drops = drops;
        }

        public Entity Entity { get; }
        public ResolvedMob Mob { get; }
        public MobStats Stats { get; }
        public DamageSource? Cause { get; }
        // mutable
        public List<LootDrop> Drops { get; }
    }

    public sealed class MobPhaseEnteredEvent
    {
        public MobPhaseEnteredEvent(Entity entity, ResolvedMob mob, BossPhaseModel phase, int phaseNumber)
        {
            Entity = entity;
            Mob = mob;
            Phase = phase;
            PhaseNumber = phaseNumber;
        }

        public Entity Entity { get; }
        public ResolvedMob Mob { get; }
        public BossPhaseModel Phase { get; }
        // 1-based, per life
        public int PhaseNumber { get; }
    }

    public sealed class MobLoadedEvent
    {
        public MobLoadedEvent(Entity entity, ResolvedMob mob, MobStats stats, bool firstSpawn)
        {
            Entity = entity;
            Mob = mob;
            Stats = stats;
            FirstSpawn = firstSpawn;
        }

        public Entity Entity { get; }
        public ResolvedMob Mob { get; }
        public MobStats Stats { get; }
        // false when loaded with a chunk
        public bool FirstSpawn { get; }
    }

    public sealed class MobDespawnedEvent
    {
        public MobDespawnedEvent(Entity entity, ResolvedMob mob, EnumDespawnReason reason)
        {
            Entity = entity;
            Mob = mob;
            Reason = reason;
        }

        public Entity Entity { get; }
        public ResolvedMob Mob { get; }
        public EnumDespawnReason Reason { get; }
    }

    public sealed class ThreatAddedEvent
    {
        public ThreatAddedEvent(Entity entity, Entity source, float amount, float total)
        {
            Entity = entity;
            Source = source;
            Amount = amount;
            Total = total;
        }

        public Entity Entity { get; }
        // the attacker, also for a call for help
        public Entity Source { get; }
        public float Amount { get; }
        public float Total { get; }
    }

    public sealed class EnemyChangedEvent
    {
        public EnemyChangedEvent(Entity entity, Entity? previous, Entity? current)
        {
            Entity = entity;
            Previous = previous;
            Current = current;
        }

        public Entity Entity { get; }
        public Entity? Previous { get; }
        public Entity? Current { get; }
    }

    public sealed class LeashEvent
    {
        public LeashEvent(Entity entity, Vec3d anchor, bool started, bool reachedHome)
        {
            Entity = entity;
            Anchor = anchor;
            Started = started;
            ReachedHome = reachedHome;
        }

        public Entity Entity { get; }
        public Vec3d Anchor { get; }
        public bool Started { get; }
        // only when Started is false
        public bool ReachedHome { get; }
    }

    public sealed class LootRollingEvent
    {
        public LootRollingEvent(Entity entity, ResolvedMob mob, MobStats stats, List<string> tables)
        {
            Entity = entity;
            Mob = mob;
            Stats = stats;
            Tables = tables;
        }

        public Entity Entity { get; }
        public ResolvedMob Mob { get; }
        public MobStats Stats { get; }
        // mutable
        public List<string> Tables { get; }
    }

    public sealed class CatalogReloadedEvent
    {
        public CatalogReloadedEvent(EnumAppSide side, int version, ValidationReport report)
        {
            Side = side;
            Version = version;
            Report = report;
        }

        public EnumAppSide Side { get; }
        public int Version { get; }
        public ValidationReport Report { get; }
    }

    public sealed class SpawnerSpawnedEvent
    {
        public SpawnerSpawnedEvent(BlockPos position, SpawnerConfig config, IReadOnlyList<EntityAgent> spawned)
        {
            Position = position;
            Config = config;
            Spawned = spawned;
        }

        // includes dimension
        public BlockPos Position { get; }
        public SpawnerConfig Config { get; }
        public IReadOnlyList<EntityAgent> Spawned { get; }
    }

    // null side = outside every zone
    public sealed class ZoneCrossedEvent
    {
        public ZoneCrossedEvent(IPlayer player, ZoneModel? from, ZoneModel? to)
        {
            Player = player;
            From = from;
            To = to;
        }

        public IPlayer Player { get; }
        public ZoneModel? From { get; }
        public ZoneModel? To { get; }
    }

    public static partial class MobsApi
    {
        // server side except CatalogReloaded
        // checked every 2 s, zone mobs: ZoneSpawner.ZoneOf(entity)
        public static event Action<ZoneCrossedEvent>? ZoneCrossed;
        // after MobSpawned for each mob
        public static event Action<SpawnerSpawnedEvent>? SpawnerSpawned;
        public static event Action<MobSpawnedEvent>? MobSpawned;
        // before loot spawns
        public static event Action<MobDiedEvent>? MobDied;
        // after the phase multipliers and actions
        public static event Action<MobPhaseEnteredEvent>? MobPhaseEntered;
        // new spawn or loaded with a chunk
        public static event Action<MobLoadedEvent>? MobLoaded;
        // removed or unloaded without dying
        public static event Action<MobDespawnedEvent>? MobDespawned;
        // every hit, keep handlers cheap
        public static event Action<ThreatAddedEvent>? ThreatAdded;
        public static event Action<EnemyChangedEvent>? EnemyChanged;
        public static event Action<LeashEvent>? Leashing;
        public static event Action<LootRollingEvent>? LootRolling;
        // both sides
        public static event Action<CatalogReloadedEvent>? CatalogReloaded;

        internal static void RaiseZoneCrossed(ZoneCrossedEvent args, ILogger logger) => Raise(ZoneCrossed, args, logger);
        internal static void RaiseSpawnerSpawned(SpawnerSpawnedEvent args, ILogger logger) => Raise(SpawnerSpawned, args, logger);
        internal static void RaiseMobSpawned(MobSpawnedEvent args, ILogger logger) => Raise(MobSpawned, args, logger);
        internal static void RaiseMobDied(MobDiedEvent args, ILogger logger) => Raise(MobDied, args, logger);
        internal static void RaiseMobPhaseEntered(MobPhaseEnteredEvent args, ILogger logger) => Raise(MobPhaseEntered, args, logger);
        internal static void RaiseMobLoaded(MobLoadedEvent args, ILogger logger) => Raise(MobLoaded, args, logger);
        internal static void RaiseMobDespawned(MobDespawnedEvent args, ILogger logger) => Raise(MobDespawned, args, logger);
        internal static void RaiseThreatAdded(ThreatAddedEvent args, ILogger logger) => Raise(ThreatAdded, args, logger);
        internal static void RaiseEnemyChanged(EnemyChangedEvent args, ILogger logger) => Raise(EnemyChanged, args, logger);
        internal static void RaiseLeashing(LeashEvent args, ILogger logger) => Raise(Leashing, args, logger);
        internal static void RaiseLootRolling(LootRollingEvent args, ILogger logger) => Raise(LootRolling, args, logger);
        internal static void RaiseCatalogReloaded(CatalogReloadedEvent args, ILogger logger) => Raise(CatalogReloaded, args, logger);

        // hot paths skip building the event when nobody listens
        internal static bool HasThreatListeners => ThreatAdded != null;
        internal static bool HasEnemyListeners => EnemyChanged != null;

        // isolate handler exceptions
        private static void Raise<T>(Action<T>? handlers, T args, ILogger logger)
        {
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<T>)handler)(args); }
                catch (Exception e) { logger.Error("[canrpgmobs] {0} handler {1} failed: {2}", typeof(T).Name, handler.Method.Name, e); }
            }
        }
    }
}
