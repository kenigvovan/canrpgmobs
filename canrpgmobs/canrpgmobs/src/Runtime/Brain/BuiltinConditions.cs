using System;
using canrpgclasses.Core;
using canrpgmobs.Data;
using Vintagestory.API.Common;

namespace canrpgmobs.Runtime.Brain.Builtin
{
    [MobConditionType("selfHpBelow", "The mob's own health fraction is below value")]
    public sealed class SelfHpBelowCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c) => c.value is { } v && ctx.SelfHp < v;
    }

    [MobConditionType("selfHpAbove", "The mob's own health fraction is above value")]
    public sealed class SelfHpAboveCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c) => c.value is { } v && ctx.SelfHp > v;
    }

    [MobConditionType("targetHpBelow", "The rule's target health fraction is below value")]
    public sealed class TargetHpBelowCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool Test(in BrainContext ctx, SkillConditionModel c) => ctx.Target != null && c.value is { } v && BrainContext.HpFraction(ctx.Target) < v;
    }

    [MobConditionType("targetHpAbove", "The rule's target health fraction is above value")]
    public sealed class TargetHpAboveCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool Test(in BrainContext ctx, SkillConditionModel c) => ctx.Target != null && c.value is { } v && BrainContext.HpFraction(ctx.Target) > v;
    }

    [MobConditionType("distanceBetween", "The target is between min and max blocks away")]
    public sealed class DistanceBetweenCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Blocks("min", required: false), ParamInfo.Blocks("max") };

        public bool Test(in BrainContext ctx, SkillConditionModel c)
        {
            if (ctx.Target == null || c.max is not { } max) return false;
            double distance = ctx.DistanceTo(ctx.Target);
            return distance >= (c.min ?? 0f) && distance <= max;
        }
    }

    [MobConditionType("targetHasEffect", "The target carries the effectshud effect")]
    public sealed class TargetHasEffectCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Effect("effect") };
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => ctx.Target != null && !string.IsNullOrEmpty(c.effect) && RpgCombatApi.HasEffect(ctx.Target, c.effect!);
    }

    [MobConditionType("targetHasNoEffect", "The target does not carry the effectshud effect")]
    public sealed class TargetHasNoEffectCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Effect("effect") };
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => ctx.Target != null && !string.IsNullOrEmpty(c.effect) && !RpgCombatApi.HasEffect(ctx.Target, c.effect!);
    }

    [MobConditionType("selfHasEffect", "The mob carries the effectshud effect")]
    public sealed class SelfHasEffectCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Effect("effect") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => !string.IsNullOrEmpty(c.effect) && RpgCombatApi.HasEffect(ctx.Self, c.effect!);
    }

    [MobConditionType("selfHasNoEffect", "The mob does not carry the effectshud effect")]
    public sealed class SelfHasNoEffectCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Effect("effect") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => !string.IsNullOrEmpty(c.effect) && !RpgCombatApi.HasEffect(ctx.Self, c.effect!);
    }

    [MobConditionType("targetIsCasting", "The target is casting or channelling a spell")]
    public sealed class TargetIsCastingCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool Test(in BrainContext ctx, SkillConditionModel c) => ctx.Target != null && RpgCombatApi.IsCasting(ctx.Target);
    }

    [MobConditionType("targetControlled", "The target is stunned, rooted, transmuted or otherwise unable to act or move")]
    public sealed class TargetControlledCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool Test(in BrainContext ctx, SkillConditionModel c) => ctx.Target != null && RpgCombatApi.IsControlled(ctx.Target);
    }

    [MobConditionType("alliesInRange", "At least min allies (default 1, the mob not counted) within range blocks")]
    public sealed class AlliesInRangeCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Blocks("range", max: BrainScan.Radius), ParamInfo.Count("min", required: false) };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => c.range is { } range && BrainContext.CountWithin(ctx.Allies, ctx.Self, range) >= (int)(c.min ?? 1f);
    }

    [MobConditionType("enemiesInRange", "At least min enemies (default 1) within range blocks")]
    public sealed class EnemiesInRangeCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Blocks("range", max: BrainScan.Radius), ParamInfo.Count("min", required: false) };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => c.range is { } range && BrainContext.CountWithin(ctx.Enemies, ctx.Self, range) >= (int)(c.min ?? 1f);
    }

    [MobConditionType("alliesBelowHp", "At least min allies (default 1) within range (default 16) are below the value health fraction")]
    public sealed class AlliesBelowHpCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[]
        {
            ParamInfo.Fraction("value"), ParamInfo.Blocks("range", required: false, max: BrainScan.Radius), ParamInfo.Count("min", required: false)
        };

        public bool NeedsTarget => false;

        public bool Test(in BrainContext ctx, SkillConditionModel c)
        {
            if (c.value is not { } threshold) return false;
            float range = c.range ?? 16f;
            float rangeSq = range * range;
            int needed = (int)(c.min ?? 1f), count = 0;
            foreach (var ally in ctx.Allies)
            {
                if (!ally.Alive || ctx.Self.Pos.SquareDistanceTo(ally.Pos) > rangeSq || BrainContext.HpFraction(ally) >= threshold) continue;
                if (++count >= needed) return true;
            }
            return needed <= 0;
        }
    }

    [MobConditionType("chance", "Passes with the value probability on each evaluation")]
    public sealed class ChanceCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c) => c.value is { } v && ctx.Rand.NextDouble() < v;
    }

    [MobConditionType("inCombatFor", "The mob has been fighting for at least value seconds")]
    public sealed class InCombatForCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Seconds("value") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c) => c.value is { } v && ctx.Mob.CombatSeconds >= v;
    }

    [MobConditionType("hpPhase", "Once per life: the mob's health fell to value or below (resets when it walks home and heals)")]
    public sealed class HpPhaseCondition : IMobCondition
    {
        public const string Id = "hpPhase";
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Fraction("value") };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c)
            => c.value is { } v && ctx.Brain.IsPhaseAvailable(ctx.RuleIndex) && ctx.SelfHp <= v;
    }

    [MobConditionType("phaseAtLeast", "The mob has entered at least value boss phases in this life")]
    public sealed class PhaseAtLeastCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = new[] { ParamInfo.Count("value", max: 20f) };
        public bool NeedsTarget => false;
        public bool Test(in BrainContext ctx, SkillConditionModel c) => c.value is { } v && ctx.Mob.PhaseIndex >= (int)v;
    }

    [MobConditionType("targetIsPlayer", "The target is a player")]
    public sealed class TargetIsPlayerCondition : IMobCondition
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool Test(in BrainContext ctx, SkillConditionModel c) => ctx.Target is EntityPlayer;
    }
}
