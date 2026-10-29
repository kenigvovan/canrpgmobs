using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canrpgclasses.Core;
using canrpgclasses.Core.Control;
using canrpgclasses.Core.EB;
using canrpgclasses.Core.Spells;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Actions;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Audio;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Skill brain: picks a ready rule of the top priority by weight and casts it.
    // Own slot, so movement keeps running unless the rule has stopToCast.
    public class AiTaskRpgCast : AiTaskBaseTargetable, IOffensiveAiTask
    {
        public const string Code = "rpgmobcast";

        private const int MaxCandidates = 8;
        private const double MoveInterruptDistanceSq = 0.36;

        private enum Phase { Idle, Casting, Recover }

        private readonly record struct Check(IMobCondition Test, SkillConditionModel Model);

        // registry lookups cached per rule list; self checks run before the selector
        private sealed class CompiledRule
        {
            public IMobAction? Action;
            public ITargetSelector? Selector;
            public Check[] SelfChecks = Array.Empty<Check>();
            public Check[] TargetChecks = Array.Empty<Check>();
            // unknown action/condition, rule never fires
            public string? Broken;
            public bool NeedSight;
        }

        private readonly int evalMs;
        private readonly float seekingRange;
        private readonly Dictionary<string, AnimationMetaData> animCache = new();
        private readonly BrainScan scan = new();
        private readonly int[] candidateRules = new int[MaxCandidates];
        private readonly Entity?[] candidateTargets = new Entity?[MaxCandidates];
        private readonly Vec3d castStartPos = new();

        private EBRpgMob? mobBehavior;
        private EBSpellCaster? caster;
        private EBSpellCooldowns? cooldowns;
        private IReadOnlyList<SkillRuleModel>? rules;
        private CompiledRule[] compiled = Array.Empty<CompiledRule>();
        private long[] ruleReadyMs = Array.Empty<long>();
        private bool[] phaseUsed = Array.Empty<bool>();
        private int lifeEpoch = -1;
        private long nextEvalMs;
        private long gcdUntilMs;
        private int chosen = -1;
        private Phase phase;
        private long recoverUntilMs;
        // committed when a timed cast lands
        private long pendingReadyMs;
        private long pendingGcdMs;
        private bool pendingPhase;
        private int castInterruptCount;
        private string? lastTrace;
        private long lastTraceMs;

        // for /canrpgmobs inspect
        public CastFailReason LastFailReason { get; private set; }
        public string? LastSkillId { get; private set; }
        public string PhaseName => phase.ToString();

        public AiTaskRpgCast(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            evalMs = Math.Max(50, taskConfig["evalMs"].AsInt(300));
            seekingRange = taskConfig["seekingRange"].AsFloat(20f);
        }

        public override void AfterInitialize()
        {
            base.AfterInitialize();
            mobBehavior = entity.GetBehavior<EBRpgMob>();
            caster = entity.GetBehavior<EBSpellCaster>();
            cooldowns = entity.GetBehavior<EBSpellCooldowns>();
        }

        private bool IsOnCooldown(string key) => cooldowns?.IsOnCooldown(key) ?? false;

        // for the hpPhase condition
        public bool IsPhaseAvailable(int ruleIndex) => ruleIndex >= 0 && ruleIndex < phaseUsed.Length && !phaseUsed[ruleIndex];

        public override bool ShouldExecute()
        {
            long now = entity.World.ElapsedMilliseconds;
            if (now < nextEvalMs) return false;
            nextEvalMs = now + evalMs + entity.World.Rand.Next(evalMs / 3 + 1);

            var mob = mobBehavior?.Mob;
            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            if (mob == null || catalog == null || mob.Skills.Count == 0 || caster == null) return false;
            if (mobBehavior!.IsLeashing || mobBehavior.IsFleeing || caster.IsCasting || RpgCombatApi.PreventsCasting(entity)) return false;
            SyncRules(mob.Skills, now);

            // only collect reasons when watched via /canrpgmobs brain
            var why = BrainTrace.IsWatched(entity.EntityId) ? new StringBuilder() : null;
            var ctx = new BrainContext(entity, mobBehavior, catalog, scan, this, mobBehavior.AcquireEnemy(seekingRange), null, now, -1);

            int count = 0;
            float bestPriority = 0f;
            double totalWeight = 0;
            for (int i = 0; i < rules!.Count; i++)
            {
                var rule = rules[i];
                // sorted by priority
                if (count > 0 && rule.priority < bestPriority) break;

                string? refusal = Evaluate(rule, i, in ctx, now, out var target);
                if (refusal != null)
                {
                    why?.Append(rule.id).Append(": ").Append(refusal).Append("; ");
                    continue;
                }
                if (count == MaxCandidates) continue;
                if (count == 0) bestPriority = rule.priority;
                candidateRules[count] = i;
                candidateTargets[count] = target;
                totalWeight += Math.Max(0f, rule.weight);
                count++;
            }

            if (count == 0)
            {
                Trace(why, "nothing to cast", now);
                return false;
            }

            int pick = count > 1 && totalWeight > 0 ? PickWeighted(count, totalWeight) : 0;
            chosen = candidateRules[pick];
            targetEntity = candidateTargets[pick];
            Array.Clear(candidateTargets, 0, count);
            if (why != null) Trace(why, $"casts {rules[chosen].id} at {targetEntity?.GetName()}", now);
            return true;
        }

        private int PickWeighted(int count, double totalWeight)
        {
            double roll = entity.World.Rand.NextDouble() * totalWeight;
            int pick = 0;
            for (; pick < count - 1; pick++)
            {
                roll -= Math.Max(0f, rules![candidateRules[pick]].weight);
                if (roll < 0) break;
            }
            return pick;
        }

        private SkillRuleModel? ChosenRule => rules != null && chosen >= 0 && chosen < rules.Count ? rules[chosen] : null;

        // seconds, jittered per use
        private float RollCooldown(SkillRuleModel rule)
        {
            float jitter = 1f + (float)(entity.World.Rand.NextDouble() * 2.0 - 1.0) * rule.cooldownJitter;
            return Math.Max(0f, rule.cooldown * jitter);
        }

        private static long GcdMs(SkillRuleModel rule) => (long)(Math.Max(0f, rule.gcd) * 1000f);

        // null = can fire; reasons are constants to avoid per-tick allocs
        private string? Evaluate(SkillRuleModel rule, int index, in BrainContext ctx, long now, out Entity? target)
        {
            target = null;
            if (now < ruleReadyMs[index]) return "cooldown";
            if (now < gcdUntilMs) return "gcd";

            var plan = compiled[index];
            if (plan.Broken != null) return plan.Broken;

            if (plan.Action == null)
            {
                // pre-check what the core would refuse anyway
                var spell = RpgCombatApi.GetSpell(entity.Api, rule.spell);
                if (spell == null) return "unknown spell";
                if (IsOnCooldown(spell.CooldownKey)) return "spell cooldown";
                if (spell.TriggersGlobalCooldown && IsOnCooldown(Spell.GlobalCooldownKey)) return "global cooldown";
            }

            // cheap self checks before the selector and raytrace
            if (plan.SelfChecks.Length > 0)
            {
                var alone = ctx.WithTarget(null, index);
                foreach (var check in plan.SelfChecks)
                    if (check.Test.Test(in alone, check.Model) == check.Model.not) return check.Model.type;
            }

            target = plan.Selector?.Select(in ctx, rule);
            if (target == null) return "no target";
            if (target != entity)
            {
                double distance = entity.Pos.DistanceTo(target.Pos);
                if (distance < rule.minRange || distance > rule.maxRange) return "out of range";
            }

            if (plan.TargetChecks.Length > 0)
            {
                var withTarget = ctx.WithTarget(target, index);
                foreach (var check in plan.TargetChecks)
                    if (check.Test.Test(in withTarget, check.Model) == check.Model.not) return check.Model.type;
            }

            if (plan.Action is { } action)
            {
                var actionCtx = new MobActionContext(entity, ctx.Mob, ctx.Catalog, target);
                if (action.Refusal(in actionCtx, rule.action!) is { } refusal) return refusal;
            }

            if (plan.NeedSight && target != entity && !HasLineOfSight(target)) return "no line of sight";
            return null;
        }

        public override void StartExecute()
        {
            base.StartExecute();
            phase = Phase.Idle;
            if (ChosenRule is not { } rule || targetEntity == null || caster == null || mobBehavior == null) return;

            long now = entity.World.ElapsedMilliseconds;

            if (rule.stopToCast)
            {
                pathTraverser.Stop();
                entity.Controls.StopAllMovement();
                mobBehavior.HoldPosition = true;
            }
            if (rule.faceTarget) FaceTarget();

            if (rule.action != null)
            {
                RunAction(rule, now);
                return;
            }

            var interrupt = rule.interrupt;
            var health = mobBehavior.Health;
            caster.InterruptFlags = interrupt?.onDamage == false ? CastInterruptFlags.None : CastInterruptFlags.Damage;
            caster.MinInterruptDamage = interrupt != null && health != null ? Math.Max(0f, interrupt.damageFraction) * health.MaxHealth : 0f;

            float ruleCooldown = RollCooldown(rule);
            if (rule.cooldownMode == CooldownModes.Rule) caster.CooldownOverride = (_, _) => ruleCooldown;
            else caster.CooldownOverride = null;

            var aim = new AimContext { TargetEntity = targetEntity, AimPosition = targetEntity.Pos.XYZ, AimAtTarget = true };
            PlayAnim(rule.animation?.cast);
            if (!string.IsNullOrEmpty(rule.sound?.start)) PlaySound(rule.sound!.start);
            else if (mobBehavior.Mob?.Sounds.TryGetValue(SoundKeys.CastStart, out var castSound) == true) MobAudio.Play(entity, castSound);
            LastSkillId = rule.id;
            castStartPos.Set(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z);

            if (!caster.TryCast(RpgCombatApi.NormalizeSpellId(rule.spell), aim))
            {
                LastFailReason = caster.LastFailReason;
                ruleReadyMs[chosen] = now + 1000;
                StopAnim(rule.animation?.cast);
                caster.CooldownOverride = null;
                mobBehavior.HoldPosition = false;
                return;
            }

            LastFailReason = CastFailReason.None;
            // rule is committed only when the spell lands, interrupted casts can retry
            pendingReadyMs = rule.cooldownMode == CooldownModes.Spell ? now : now + (long)(ruleCooldown * 1000f);
            pendingGcdMs = now + GcdMs(rule);
            pendingPhase = HasPhaseCondition(rule);
            castInterruptCount = caster.InterruptCount;

            if (caster.IsCasting) phase = Phase.Casting;
            else
            {
                CommitRule();
                Release(rule, now);
            }
        }

        private void CommitRule()
        {
            ruleReadyMs[chosen] = pendingReadyMs;
            gcdUntilMs = pendingGcdMs;
            if (pendingPhase) phaseUsed[chosen] = true;
        }

        private void RetryRuleSoon(long now)
        {
            if (chosen >= 0 && chosen < ruleReadyMs.Length) ruleReadyMs[chosen] = Math.Max(ruleReadyMs[chosen], now + 1000);
        }

        // after an AI rebuild, keep cooldowns and phase flags
        public void CopyStateFrom(AiTaskRpgCast old)
        {
            if (old.rules == null) return;
            rules = old.rules;
            // SyncRules skips a list it already has
            compiled = old.compiled;
            ruleReadyMs = (long[])old.ruleReadyMs.Clone();
            phaseUsed = (bool[])old.phaseUsed.Clone();
            lifeEpoch = old.lifeEpoch;
            gcdUntilMs = old.gcdUntilMs;
        }

        // action rules run instantly, then recover
        private void RunAction(SkillRuleModel rule, long now)
        {
            LastSkillId = rule.id;
            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            var ctx = catalog == null ? default : new MobActionContext(entity, mobBehavior!, catalog, targetEntity!);
            if (catalog == null || !MobActions.TryRun(in ctx, rule.action!))
            {
                ruleReadyMs[chosen] = now + 1000;
                mobBehavior!.HoldPosition = false;
                return;
            }

            PlaySound(rule.sound?.start);
            ruleReadyMs[chosen] = now + (long)(RollCooldown(rule) * 1000f);
            gcdUntilMs = now + GcdMs(rule);
            if (HasPhaseCondition(rule)) phaseUsed[chosen] = true;

            Release(rule, now);
            // no release anim: keep the cast anim, FinishExecute stops it
            if (string.IsNullOrEmpty(rule.animation?.release)) PlayAnim(rule.animation?.cast);
        }

        public override bool ContinueExecute(float dt)
        {
            if (ChosenRule is not { } rule) return false;
            long now = entity.World.ElapsedMilliseconds;

            switch (phase)
            {
                case Phase.Casting:
                    bool targetLost = !IsValidTarget(targetEntity) || entity.Pos.DistanceTo(targetEntity!.Pos) > rule.maxRange + 4f;
                    if (targetLost && rule.interrupt?.onTargetLost != "finish") return Abort(rule, now, true);
                    // knocked back
                    if (rule.interrupt?.onMove == true && entity.Pos.SquareDistanceTo(castStartPos) > MoveInterruptDistanceSq) return Abort(rule, now, true);
                    if (!targetLost && rule.faceTarget) FaceTarget();
                    if (caster != null && !caster.IsCasting)
                    {
                        // interrupted, no cooldown
                        if (caster.InterruptCount != castInterruptCount) return Abort(rule, now, false);
                        CommitRule();
                        Release(rule, now);
                    }
                    return true;

                case Phase.Recover:
                    return now < recoverUntilMs;

                default:
                    return false;
            }
        }

        // false, for ContinueExecute
        private bool Abort(SkillRuleModel rule, long now, bool interrupt)
        {
            if (interrupt) caster?.Interrupt(true);
            StopAnim(rule.animation?.cast);
            RetryRuleSoon(now);
            return false;
        }

        public override void FinishExecute(bool cancelled)
        {
            base.FinishExecute(cancelled);
            StopAnim(ChosenRule?.animation?.cast);
            if (caster != null)
            {
                // preempted or CC'd mid-cast
                if (cancelled && caster.IsCasting) caster.Interrupt(true);
                caster.CooldownOverride = null;
                caster.InterruptFlags = CastInterruptFlags.None;
                caster.MinInterruptDamage = 0f;
            }
            if (mobBehavior != null) mobBehavior.HoldPosition = false;
            if (phase == Phase.Casting) RetryRuleSoon(entity.World.ElapsedMilliseconds);
            phase = Phase.Idle;
            chosen = -1;
        }

        public override bool CanSense(Entity e, double range) => (mobBehavior?.IsEnemy(e) ?? false) && base.CanSense(e, range);

        // hpPhase flags reset on a new life (leash)
        private void SyncRules(IReadOnlyList<SkillRuleModel> skills, long now)
        {
            if (mobBehavior != null && mobBehavior.LifeEpoch != lifeEpoch)
            {
                lifeEpoch = mobBehavior.LifeEpoch;
                Array.Clear(phaseUsed, 0, phaseUsed.Length);
            }
            if (ReferenceEquals(rules, skills)) return;

            // new list: carry state over by rule id
            var oldRules = rules;
            var oldReady = ruleReadyMs;
            var oldPhase = phaseUsed;
            rules = skills;
            ruleReadyMs = new long[skills.Count];
            phaseUsed = new bool[skills.Count];
            compiled = new CompiledRule[skills.Count];
            for (int i = 0; i < skills.Count; i++)
            {
                compiled[i] = Compile(skills[i]);
                int previous = IndexOfRule(oldRules, skills[i].id);
                if (previous >= 0 && previous < oldReady.Length)
                {
                    ruleReadyMs[i] = oldReady[previous];
                    phaseUsed[i] = previous < oldPhase.Length && oldPhase[previous];
                    continue;
                }
                var initial = skills[i].initialCooldown;
                if (initial is not { Length: >= 2 }) continue;
                double seconds = initial[0] + entity.World.Rand.NextDouble() * Math.Max(0f, initial[1] - initial[0]);
                ruleReadyMs[i] = now + (long)(seconds * 1000.0);
            }
        }

        private static CompiledRule Compile(SkillRuleModel rule)
        {
            var plan = new CompiledRule();
            if (rule.action != null)
            {
                plan.Action = MobsApi.Actions.Get(rule.action.type);
                if (plan.Action == null) plan.Broken = "unknown action";
            }

            plan.Selector = MobsApi.TargetSelectors.Get(rule.target) ?? MobsApi.TargetSelectors.Get(SkillTargets.CurrentEnemy);
            plan.NeedSight = rule.requireLineOfSight ?? !(plan.Selector?.TargetsAllies ?? false);

            if (rule.conditions == null) return plan;
            List<Check>? self = null, withTarget = null;
            foreach (var condition in rule.conditions)
            {
                if (condition == null) continue;
                var test = MobsApi.Conditions.Get(condition.type);
                if (test == null)
                {
                    plan.Broken ??= "unknown condition";
                    continue;
                }
                var list = test.NeedsTarget ? withTarget ??= new List<Check>() : self ??= new List<Check>();
                list.Add(new Check(test, condition));
            }
            if (self != null) plan.SelfChecks = self.ToArray();
            if (withTarget != null) plan.TargetChecks = withTarget.ToArray();
            return plan;
        }

        // after a catalog change, stop a cast whose rule was removed
        public void OnSkillsChanged(IReadOnlyList<SkillRuleModel> skills)
        {
            if (phase != Phase.Casting || ChosenRule is not { } rule || IndexOfRule(skills, rule.id) >= 0) return;
            caster?.Interrupt(true);
        }

        private static int IndexOfRule(IReadOnlyList<SkillRuleModel>? list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].id == id) return i;
            return -1;
        }

        private static bool HasPhaseCondition(SkillRuleModel rule)
        {
            if (rule.conditions == null) return false;
            foreach (var condition in rule.conditions)
                if (condition?.type == Brain.Builtin.HpPhaseCondition.Id) return true;
            return false;
        }

        // blocks only, via the task's collision-box supplier
        private bool HasLineOfSight(Entity target)
        {
            rayTraceFrom.Set(entity.Pos.X, entity.Pos.InternalY + entity.Properties.EyeHeight, entity.Pos.Z);
            rayTraceTo.Set(target.Pos.X, target.Pos.InternalY + target.SelectionBox.Y2 * 0.8f, target.Pos.Z);
            blockSel = null;
            entitySel = null;
            entity.World.RayTraceForSelection(this, rayTraceFrom, rayTraceTo, ref blockSel, ref entitySel);
            return blockSel == null;
        }

        private bool IsValidTarget(Entity? e)
            => e != null && e.Alive && e.Pos.Dimension == entity.Pos.Dimension
               && entity.Pos.DistanceTo(e.Pos) <= seekingRange * 1.5f;

        private void FaceTarget()
        {
            if (targetEntity == null || targetEntity == entity) return;
            float yaw = (float)Math.Atan2(targetEntity.Pos.X - entity.Pos.X, targetEntity.Pos.Z - entity.Pos.Z);
            entity.Pos.Yaw = yaw;
            entity.BodyYaw = yaw;
        }

        private void Release(SkillRuleModel rule, long now)
        {
            StopAnim(rule.animation?.cast);
            PlayAnim(rule.animation?.release);
            PlaySound(rule.sound?.release);
            phase = Phase.Recover;
            recoverUntilMs = now + Math.Max(0, rule.recoverMs);
        }

        private void Trace(StringBuilder? why, string decision, long now)
        {
            if (why == null) return;
            string line = why.Length == 0 ? decision : decision + " | " + why.ToString().TrimEnd(' ', ';');
            if (line == lastTrace && now - lastTraceMs < 3000) return;
            lastTrace = line;
            lastTraceMs = now;
            BrainTrace.Log(entity, line);
        }

        private void PlayAnim(string? code)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (!animCache.TryGetValue(code!, out var meta)) animCache[code!] = meta = MobAnimations.Meta(entity, code!);
            entity.AnimManager?.StartAnimation(meta);
        }

        private void StopAnim(string? code)
        {
            if (!string.IsNullOrEmpty(code)) entity.AnimManager?.StopAnimation(code);
        }

        private void PlaySound(string? path)
        {
            if (!string.IsNullOrEmpty(path)) MobAudio.PlayPath(entity, path!, 24f);
        }
    }
}
