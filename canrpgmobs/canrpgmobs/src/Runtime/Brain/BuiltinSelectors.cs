using System;
using canrpgclasses.Core;
using canrpgmobs.Data;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Brain.Builtin
{
    [MobTargetSelector(SkillTargets.CurrentEnemy, "The enemy the mob is fighting")]
    public sealed class CurrentEnemySelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool TargetsAllies => false;
        public Entity? Select(in BrainContext ctx, SkillRuleModel rule) => ctx.Enemy;
    }

    [MobTargetSelector(SkillTargets.Self, "The mob itself, only while it has an enemy")]
    public sealed class SelfSelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool TargetsAllies => true;
        public Entity? Select(in BrainContext ctx, SkillRuleModel rule) => ctx.Enemy != null ? ctx.Self : null;
    }

    [MobTargetSelector(SkillTargets.LowestHpAlly, "The most hurt ally within maxRange below allyHpBelow, the mob itself included")]
    public sealed class LowestHpAllySelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("allyHpBelow") };
        public bool TargetsAllies => true;

        public Entity? Select(in BrainContext ctx, SkillRuleModel rule)
        {
            Entity? best = null;
            float bestFraction = rule.allyHpBelow;
            float own = ctx.SelfHp;
            if (own < bestFraction) { best = ctx.Self; bestFraction = own; }

            float rangeSq = rule.maxRange * rule.maxRange;
            foreach (var ally in ctx.Allies)
            {
                if (!ally.Alive || ctx.Self.Pos.SquareDistanceTo(ally.Pos) > rangeSq) continue;
                float fraction = BrainContext.HpFraction(ally);
                if (fraction >= bestFraction) continue;
                best = ally;
                bestFraction = fraction;
            }
            return best;
        }
    }

    [MobTargetSelector(SkillTargets.RandomEnemyInRange, "A random enemy between minRange and maxRange")]
    public sealed class RandomEnemyInRangeSelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool TargetsAllies => false;

        public Entity? Select(in BrainContext ctx, SkillRuleModel rule)
        {
            var enemies = ctx.Enemies;
            int count = 0;
            foreach (var e in enemies) if (InRange(in ctx, e, rule)) count++;
            if (count == 0) return null;

            int pick = ctx.Rand.Next(count);
            foreach (var e in enemies)
                if (InRange(in ctx, e, rule) && pick-- == 0) return e;
            return null;
        }

        private static bool InRange(in BrainContext ctx, Entity e, SkillRuleModel rule)
        {
            if (!e.Alive) return false;
            double distanceSq = ctx.Self.Pos.SquareDistanceTo(e.Pos);
            return distanceSq >= rule.minRange * rule.minRange && distanceSq <= rule.maxRange * rule.maxRange;
        }
    }

    [MobTargetSelector(SkillTargets.AllyWithoutEffect, "While fighting: the nearest ally within maxRange (the mob itself first) missing targetEffect")]
    public sealed class AllyWithoutEffectSelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Effect("targetEffect") };
        public bool TargetsAllies => true;

        public Entity? Select(in BrainContext ctx, SkillRuleModel rule)
        {
            string? effect = rule.targetEffect;
            if (ctx.Enemy == null || string.IsNullOrEmpty(effect)) return null;
            if (!RpgCombatApi.HasEffect(ctx.Self, effect!)) return ctx.Self;

            Entity? best = null;
            double bestSq = rule.maxRange * rule.maxRange;
            foreach (var ally in ctx.Allies)
            {
                if (!ally.Alive) continue;
                double distanceSq = ctx.Self.Pos.SquareDistanceTo(ally.Pos);
                if (distanceSq > bestSq || RpgCombatApi.HasEffect(ally, effect!)) continue;
                best = ally;
                bestSq = distanceSq;
            }
            return best;
        }
    }

    [MobTargetSelector(SkillTargets.EnemyCluster, "The enemy within maxRange with the most other enemies inside clusterRadius")]
    public sealed class EnemyClusterSelector : ITargetSelector
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Blocks("clusterRadius", max: 16f) };
        public bool TargetsAllies => false;

        public Entity? Select(in BrainContext ctx, SkillRuleModel rule)
        {
            var enemies = ctx.Enemies;
            Entity? best = null;
            int bestCount = -1;
            double bestDistanceSq = double.MaxValue;
            double minSq = rule.minRange * rule.minRange, maxSq = rule.maxRange * rule.maxRange;
            foreach (var e in enemies)
            {
                if (!e.Alive) continue;
                double distanceSq = ctx.Self.Pos.SquareDistanceTo(e.Pos);
                if (distanceSq < minSq || distanceSq > maxSq) continue;
                int around = BrainContext.CountWithin(enemies, e, rule.clusterRadius);
                if (around < bestCount || (around == bestCount && distanceSq >= bestDistanceSq)) continue;
                best = e;
                bestCount = around;
                bestDistanceSq = distanceSq;
                // can't do better than everyone
                if (bestCount >= enemies.Count - 1) break;
            }
            return best;
        }
    }
}
