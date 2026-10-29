using System;
using System.Collections.Generic;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Runtime.Ai
{
    // Separate slots so casting can run while moving.
    public static class TaskSpecs
    {
        public const int MoveSlot = 0;
        public const int MeleeSlot = 1;
        public const int CastSlot = 2;

        // also used by canrpgclasses fear
        public static JObject Flee(ResolvedMob mob, float fleeBelowHp) => new()
        {
            ["code"] = AiTaskRpgFlee.Code, ["priority"] = 3.0, ["slot"] = MoveSlot,
            // dummy code so the vanilla scan never starts a flee; only fear or low hp do
            ["entityCodes"] = new JArray("canrpgmobs-nothing"), ["retaliateAttacks"] = false,
            ["movespeed"] = mob.MoveSpeed * 1.35f, ["seekingRange"] = mob.Ai.aggroRange,
            ["fleeingDistance"] = mob.Ai.aggroRange + 8f, ["fleeDurationMs"] = 5000, ["fleeBelowHp"] = fleeBelowHp,
            ["animation"] = AnimKeys.Flee
        };

        public static JObject Cast(ResolvedMob mob, AiBuildContext ctx) => new()
        {
            ["code"] = AiTaskRpgCast.Code, ["priority"] = 2.5, ["slot"] = CastSlot,
            ["seekingRange"] = mob.Ai.aggroRange, ["evalMs"] = ctx.BrainEvalMs
        };

        public static JObject Melee(ResolvedMob mob, AiBuildContext ctx)
        {
            var melee = mob.Ai.melee ?? new MeleeModel();
            int[] cooldown = melee.cooldownMs is { Length: >= 2 } ? melee.cooldownMs : new[] { 1000, 1500 };
            return new JObject
            {
                ["code"] = AiTaskRpgMelee.Code, ["priority"] = 2.0, ["slot"] = MeleeSlot, ["entityCodes"] = new JArray("*"),
                ["damage"] = ctx.Stats.MeleeDamage * melee.damageMul, ["damageTier"] = ctx.Stats.MeleeTier, ["damageType"] = melee.damageType,
                ["attackDurationMs"] = melee.attackDurationMs, ["damagePlayerAtMs"] = melee.hitAtMs,
                ["mincooldown"] = cooldown[0], ["maxcooldown"] = cooldown[1], ["animation"] = melee.animation, ["animationSpeed"] = 1.4,
                // weapon reach instead of the vanilla 2 blocks
                ["minDist"] = ctx.Stats.MeleeReach
            };
        }

        // stop short of full reach so mobs don't walk into the target
        public static float StandOff(AiBuildContext ctx) => Math.Max(0f, ctx.Stats.MeleeReach * StandOffFraction);

        private const float StandOffFraction = 0.6f;

        // "*" here, CanSense filters by faction and threat
        public static JObject Seek(ResolvedMob mob, AiBuildContext ctx) => new()
        {
            ["code"] = AiTaskRpgSeek.Code, ["priority"] = 1.5, ["slot"] = MoveSlot, ["entityCodes"] = new JArray("*"),
            ["movespeed"] = mob.MoveSpeed, ["seekingRange"] = mob.Ai.aggroRange, ["animation"] = AnimKeys.Walk, ["animationSpeed"] = 1.2,
            ["extraTargetDistance"] = StandOff(ctx)
        };

        public static JObject Kite(ResolvedMob mob, float[] preferredRange) => new()
        {
            ["code"] = AiTaskRpgKite.Code, ["priority"] = 1.6, ["slot"] = MoveSlot,
            ["movespeed"] = mob.MoveSpeed * 1.1f, ["seekingRange"] = mob.Ai.aggroRange,
            ["preferredRange"] = new JArray(preferredRange[0], preferredRange[1]), ["animation"] = AnimKeys.Walk, ["animationSpeed"] = 1.2
        };

        // damage is computed on release, the held bow may change
        public static JObject Shoot(ResolvedMob mob)
        {
            var ranged = mob.Ai.ranged ?? new RangedModel();
            int[] cooldown = ranged.cooldownMs is { Length: >= 2 } ? ranged.cooldownMs : new[] { 1500, 2500 };
            int aimMs = Math.Max(100, ranged.aimMs);
            return new JObject
            {
                ["code"] = AiTaskRpgShoot.Code, ["priority"] = 1.9, ["slot"] = MeleeSlot, ["executionChance"] = 1.0,
                ["seekingRange"] = mob.Ai.aggroRange, ["throwAtMs"] = aimMs, ["minDurationMs"] = aimMs + 400, ["maxDurationMs"] = aimMs + 400,
                ["minCooldownMs"] = cooldown[0], ["maxCooldownMs"] = Math.Max(cooldown[0], cooldown[1]),
                ["animation"] = ranged.animation, ["animationSpeed"] = 1.0,
                ["arrow"] = ranged.arrow, ["damageMul"] = ranged.damageMul, ["damageType"] = ranged.damageType,
                ["speed"] = ranged.speed, ["dispersion"] = ranged.dispersion, ["minRange"] = ranged.minRange,
                ["sound"] = ranged.sound, ["gravityFactor"] = ranged.gravityFactor, ["useMeleeAdd"] = ranged.useMeleeAdd
            };
        }

        public static JObject Follow(ResolvedMob mob) => new()
        {
            ["code"] = AiTaskRpgSupportFollow.Code, ["priority"] = 1.1, ["slot"] = MoveSlot,
            ["movespeed"] = mob.MoveSpeed, ["seekingRange"] = mob.Ai.aggroRange, ["followRange"] = mob.Ai.followRange,
            ["animation"] = AnimKeys.Walk, ["animationSpeed"] = 1.2
        };

        public static void Idle(ResolvedMob mob, List<JObject> tasks)
        {
            // strolls around where it spawned instead of standing on one block; vanilla walks back past maxDistanceToSpawn
            tasks.Add(new JObject
            {
                ["code"] = "wander", ["priority"] = 1.0, ["slot"] = MoveSlot, ["movespeed"] = mob.MoveSpeed * 0.6f,
                ["wanderRangeMin"] = 3, ["wanderRangeMax"] = 10, ["maxDistanceToSpawn"] = 14, ["animation"] = AnimKeys.Walk, ["animationSpeed"] = 1.0
            });
            tasks.Add(new JObject { ["code"] = "lookaround", ["priority"] = 0.5 });
            tasks.Add(new JObject
            {
                ["code"] = "idle", ["priority"] = 0.3, ["animation"] = AnimKeys.Idle,
                ["mincooldown"] = 5000, ["maxcooldown"] = 10000, ["minduration"] = 2000, ["maxduration"] = 6000
            });
        }

        public static float[] PreferredRange(ResolvedMob mob, float min, float max)
            => mob.Ai.preferredRange is { Length: >= 2 } band ? band : new[] { min, max };

        // flee, cast and melee, the start of every built-in archetype
        public static void AddCore(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks, float fleeBelowHp)
        {
            tasks.Add(Flee(mob, fleeBelowHp));
            if (mob.Skills.Count > 0) tasks.Add(Cast(mob, ctx));
            tasks.Add(Melee(mob, ctx));
        }

        public static void Brute(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks, float fleeBelowHp)
        {
            AddCore(mob, ctx, tasks, fleeBelowHp);
            tasks.Add(Seek(mob, ctx));
            Idle(mob, tasks);
        }

        public static void Kiter(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks)
        {
            AddCore(mob, ctx, tasks, mob.Ai.fleeBelowHp);
            tasks.Add(Kite(mob, PreferredRange(mob, 8f, 14f)));
            Idle(mob, tasks);
        }

        private static readonly ParamInfo[] BaseParams =
        {
            ParamInfo.Blocks("aggroRange", max: 64f), ParamInfo.Blocks("leashRange", required: false, max: 256f),
            ParamInfo.Fraction("fleeBelowHp", required: false), ParamInfo.Blocks("callForHelpRange", required: false, max: 64f)
        };

        // lets the validator warn about sections the chosen archetype ignores
        public static readonly ParamInfo[] CommonParams = Add(BaseParams, ParamInfo.Section("melee"), ParamInfo.Section("targeting"));

        public static readonly ParamInfo[] KiteParams = Add(CommonParams, ParamInfo.Section("preferredRange"));

        public static readonly ParamInfo[] ArcherParams = Add(KiteParams, ParamInfo.Section("ranged"));

        public static readonly ParamInfo[] SupportParams = Add(KiteParams, ParamInfo.Blocks("followRange", required: false, max: 32f));

        // custom ignores melee and range sections
        public static readonly ParamInfo[] CustomParams = Add(BaseParams, ParamInfo.Section("tasks"));

        private static ParamInfo[] Add(ParamInfo[] baseParams, params ParamInfo[] extra)
        {
            var all = new ParamInfo[baseParams.Length + extra.Length];
            baseParams.CopyTo(all, 0);
            extra.CopyTo(all, baseParams.Length);
            return all;
        }
    }

    [AiArchetype(MobValidator.DefaultArchetype, "Charges the enemy and fights up close")]
    public sealed class MeleeBruteArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.CommonParams;
        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks) => TaskSpecs.Brute(mob, ctx, tasks, mob.Ai.fleeBelowHp);
    }

    [AiArchetype("rangedKiter", "Keeps preferredRange (default 8 to 14) from its enemy and casts; melee only when cornered")]
    public sealed class RangedKiterArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.KiteParams;
        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks) => TaskSpecs.Kiter(mob, ctx, tasks);
    }

    [AiArchetype(MobValidator.ArcherArchetype, "Keeps preferredRange (default 10 to 18) and shoots its enemy with the bow in its right hand (see ranged); casts, and fights in melee when cornered")]
    public sealed class RangedArcherArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.ArcherParams;

        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks)
        {
            TaskSpecs.AddCore(mob, ctx, tasks, mob.Ai.fleeBelowHp);
            tasks.Add(TaskSpecs.Shoot(mob));
            tasks.Add(TaskSpecs.Kite(mob, TaskSpecs.PreferredRange(mob, 10f, 18f)));
            TaskSpecs.Idle(mob, tasks);
        }
    }

    [AiArchetype("rangedCaster", "Old name of rangedKiter")]
    public sealed class RangedCasterArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.KiteParams;
        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks) => TaskSpecs.Kiter(mob, ctx, tasks);
    }

    [AiArchetype("support", "Keeps preferredRange (default 6 to 12) from enemies, heals and buffs, and stays near allies out of combat")]
    public sealed class SupportArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.SupportParams;

        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks)
        {
            TaskSpecs.AddCore(mob, ctx, tasks, mob.Ai.fleeBelowHp);
            tasks.Add(TaskSpecs.Kite(mob, TaskSpecs.PreferredRange(mob, 6f, 12f)));
            tasks.Add(TaskSpecs.Follow(mob));
            TaskSpecs.Idle(mob, tasks);
        }
    }

    [AiArchetype("cowardLooter", "Fights up close but runs away when hurt (fleeBelowHp, 0.3 when not set)")]
    public sealed class CowardLooterArchetype : IAiArchetype
    {
        public const float DefaultFleeBelowHp = 0.3f;
        public ParamInfo[] Params => TaskSpecs.CommonParams;

        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks)
            => TaskSpecs.Brute(mob, ctx, tasks, mob.Ai.fleeBelowHp > 0f ? mob.Ai.fleeBelowHp : DefaultFleeBelowHp);
    }

    [AiArchetype(MobValidator.CustomArchetype, "Runs the profile's tasks list as written")]
    public sealed class CustomArchetype : IAiArchetype
    {
        public ParamInfo[] Params => TaskSpecs.CustomParams;

        public void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks)
        {
            if (mob.Ai.tasks is not { Count: > 0 })
            {
                TaskSpecs.Brute(mob, ctx, tasks, mob.Ai.fleeBelowHp);
                return;
            }

            foreach (var task in mob.Ai.tasks)
            {
                if (task == null) continue;
                var copy = (JObject)task.DeepClone();
                // "@walk" means "walk", anim set keys are plain codes for now
                foreach (var property in copy.Properties())
                    if (property.Value.Type == JTokenType.String && ((string?)property.Value)?.StartsWith("@") == true)
                        property.Value = ((string)property.Value!).Substring(1);
                tasks.Add(copy);
            }
        }
    }
}
