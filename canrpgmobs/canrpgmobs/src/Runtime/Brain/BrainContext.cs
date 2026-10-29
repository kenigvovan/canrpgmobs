using System;
using System.Collections.Generic;
using canrpgmobs.Catalog;
using canrpgmobs.Runtime.Ai;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Factions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Brain
{
    // refreshed once a second and shared by all rules, entries may be dead by now
    public sealed class BrainScan
    {
        public const float Radius = 32f;
        private const long RefreshMs = 1000;

        private readonly List<Entity> allies = new();
        private readonly List<Entity> enemies = new();
        private Entity[] around = Array.Empty<Entity>();
        private long aroundAtMs = long.MinValue / 2;
        private long alliesAtMs = long.MinValue / 2;
        private long enemiesAtMs = long.MinValue / 2;

        internal List<Entity> Allies(EntityAgent self, MobCatalog catalog, long now)
        {
            if (now - alliesAtMs < RefreshMs) return allies;
            alliesAtMs = now;
            allies.Clear();
            foreach (var e in Around(self, now))
                if (e.Alive && FactionService.AreAllies(self, e, catalog)) allies.Add(e);
            return allies;
        }

        internal List<Entity> Enemies(EntityAgent self, EBRpgMob mob, long now)
        {
            if (now - enemiesAtMs < RefreshMs) return enemies;
            enemiesAtMs = now;
            enemies.Clear();
            foreach (var e in Around(self, now))
                if (mob.IsEnemy(e)) enemies.Add(e);
            return enemies;
        }

        private Entity[] Around(EntityAgent self, long now)
        {
            if (now - aroundAtMs < RefreshMs) return around;
            aroundAtMs = now;
            around = self.World.GetEntitiesAround(self.Pos.XYZ, Radius, Radius / 2f + 4f,
                c => c is EntityAgent && c.Alive && c != self && c.Pos.Dimension == self.Pos.Dimension);
            return around;
        }
    }

    // Target is null while the selector runs
    public readonly struct BrainContext
    {
        public BrainContext(EntityAgent self, EBRpgMob mob, MobCatalog catalog, BrainScan scan, AiTaskRpgCast brain,
            Entity? enemy, Entity? target, long nowMs, int ruleIndex)
        {
            Self = self;
            Mob = mob;
            Catalog = catalog;
            Scan = scan;
            Brain = brain;
            Enemy = enemy;
            Target = target;
            NowMs = nowMs;
            RuleIndex = ruleIndex;
        }

        public EntityAgent Self { get; }
        public EBRpgMob Mob { get; }
        public MobCatalog Catalog { get; }
        public BrainScan Scan { get; }
        public AiTaskRpgCast Brain { get; }
        public Entity? Enemy { get; }
        public Entity? Target { get; }
        public long NowMs { get; }
        public int RuleIndex { get; }

        public BrainContext WithTarget(Entity? target, int ruleIndex) => new(Self, Mob, Catalog, Scan, Brain, Enemy, target, NowMs, ruleIndex);

        public List<Entity> Allies => Scan.Allies(Self, Catalog, NowMs);
        public List<Entity> Enemies => Scan.Enemies(Self, Mob, NowMs);
        public Random Rand => Self.World.Rand;

        public double DistanceTo(Entity e) => Self.Pos.DistanceTo(e.Pos);

        public float SelfHp => Mob.HpFraction;

        public static float HpFraction(Entity? e)
        {
            var health = e?.GetBehavior<EntityBehaviorHealth>();
            return health == null || health.MaxHealth <= 0f ? 1f : health.Health / health.MaxHealth;
        }

        public static int CountWithin(List<Entity> entities, Entity center, float range)
        {
            int count = 0;
            float rangeSq = range * range;
            foreach (var e in entities)
                if (e.Alive && e != center && e.Pos.SquareDistanceTo(center.Pos) <= rangeSq) count++;
            return count;
        }
    }
}
