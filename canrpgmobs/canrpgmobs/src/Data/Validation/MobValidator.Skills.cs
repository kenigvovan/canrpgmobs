using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public static partial class MobValidator
    {
        // only warnings, a broken rule just never fires
        private static void CheckSkills(List<SkillRuleModel>? skills, string owner, Scope scope)
        {
            if (skills == null) return;
            var report = scope.Report;
            var spells = scope.Spells;
            var ids = new HashSet<string>();
            foreach (var rule in skills)
            {
                if (rule == null) continue;
                string path = $"{owner} > skill {(string.IsNullOrEmpty(rule.id) ? "(no id)" : rule.id)}";

                if (string.IsNullOrEmpty(rule.id)) report.Warning(path, "rule has no id; ids keep cooldowns stable across reloads");
                else if (!ids.Add(rule.id)) report.Warning(path, "rule id is used twice in this list");

                if (rule.action != null)
                {
                    if (!string.IsNullOrWhiteSpace(rule.spell)) report.Info(path, "the rule has an action, its spell is ignored");
                    CheckAction(rule.action, $"{path} > action ({rule.action.type})", scope);
                }
                else if (string.IsNullOrWhiteSpace(rule.spell)) report.Warning(path, "no spell or action set, the rule is skipped");
                else if (!spells.Exists(rule.spell)) report.Warning(path, $"unknown spell '{rule.spell}', the rule is skipped");
                else
                {
                    var support = spells.Rate(rule.spell, out var reasons);
                    if (support == SpellSupport.Unsupported)
                        report.Warning(path, $"spell '{rule.spell}' can't work on an NPC ({string.Join("; ", reasons)}), the rule is skipped");
                    else if (support == SpellSupport.Degraded)
                        report.Info(path, $"spell '{rule.spell}' partly works on an NPC: {string.Join("; ", reasons)}");
                }

                var selectors = scope.Vocabulary.Selectors;
                if (!selectors.TryGetValue(rule.target ?? "", out var selector))
                    report.Warning(path, $"target '{rule.target}' is not one of: {Known(selectors)}, currentEnemy is used");
                else
                    CheckParams(selector.Params, rule.Number, rule.targetEffect, $"{path} > target {rule.target}", report);

                if (!float.IsFinite(rule.priority)) report.Warning(path, "priority must be a number");
                if (!NonNegative(rule.weight)) report.Warning(path, "weight must be 0 or more");
                if (!NonNegative(rule.minRange) || !float.IsFinite(rule.maxRange) || rule.maxRange < rule.minRange)
                    report.Warning(path, "ranges must be 0 or more, with minRange not above maxRange, otherwise the rule never fires");
                if (!NonNegative(rule.cooldown)) report.Warning(path, "cooldown must be 0 or more");
                if (!InRange(rule.cooldownJitter, 0f, 1f)) report.Warning(path, "cooldownJitter must be between 0 and 1");
                if (!CooldownModes.All.Contains(rule.cooldownMode))
                    report.Warning(path, $"cooldownMode '{rule.cooldownMode}' is not max, rule or spell, max is used");
                if (!NonNegative(rule.gcd) || rule.gcd > 30f) report.Warning(path, "gcd must be between 0 and 30 seconds");
                if (rule.initialCooldown != null && (rule.initialCooldown.Length != 2 || !NonNegative(rule.initialCooldown[0]) || rule.initialCooldown[1] < rule.initialCooldown[0]))
                    report.Warning(path, "initialCooldown must be [min, max] seconds");

                var interrupt = rule.interrupt;
                if (interrupt != null)
                {
                    if (interrupt.onTargetLost != "cancel" && interrupt.onTargetLost != "finish")
                        report.Warning(path, $"interrupt.onTargetLost '{interrupt.onTargetLost}' is not cancel or finish, cancel is used");
                    if (!InRange(interrupt.damageFraction, 0f, 1f)) report.Warning(path, "interrupt.damageFraction must be between 0 and 1");
                    if (interrupt.onMove && !rule.stopToCast)
                        report.Warning(path, "interrupt.onMove with stopToCast false breaks the cast as soon as the mob walks");
                }
                if (rule.recoverMs < 0 || rule.recoverMs > 10000) report.Warning(path, "recoverMs should be between 0 and 10000");

                CheckConditions(rule, path, scope);
            }
        }

        private static void CheckConditions(SkillRuleModel rule, string rulePath, Scope scope)
        {
            if (rule.conditions == null) return;
            var report = scope.Report;
            for (int i = 0; i < rule.conditions.Count; i++)
            {
                var condition = rule.conditions[i];
                if (condition == null) continue;
                string path = $"{rulePath} > condition {i + 1} ({condition.type})";

                if (!scope.Vocabulary.Conditions.TryGetValue(condition.type ?? "", out var entry))
                {
                    report.Warning(path, $"unknown condition '{condition.type}', the rule never fires (known: {Known(scope.Vocabulary.Conditions)})");
                    continue;
                }

                CheckParams(entry.Params, condition.Number, condition.effect, path, report);
                ReportUnread(SkillConditionModel.NumberFields, condition.Number, entry, condition.type, path, report);
                if (!string.IsNullOrEmpty(condition.effect) && !entry.Params.Any(p => p.Kind == ParamKind.Effect))
                    report.Info(path, $"'effect' is not read by {condition.type}");
            }
        }

        private static void CheckAction(SkillActionModel action, string path, Scope scope)
        {
            var report = scope.Report;
            if (!scope.Vocabulary.Actions.TryGetValue(action.type ?? "", out var entry))
            {
                report.Warning(path, $"unknown action '{action.type}', it never runs (known: {Known(scope.Vocabulary.Actions)})");
                return;
            }

            CheckParams(entry.Params, action.Number, null, path, report, name => name == "mob" ? action.mob : null);
            ReportUnread(SkillActionModel.NumberFields, action.Number, entry, action.type, path, report);
            if (string.IsNullOrWhiteSpace(action.mob)) return;
            if (!entry.Params.Any(p => p.Kind == ParamKind.Mob)) report.Info(path, $"'mob' is not read by {action.type}");
            else if (MobResolver.Resolve(action.mob!, scope.Data, null, out var error) == null) report.Warning(path, $"mob '{action.mob}' does not resolve: {error}");
        }

        // only warnings, bad phases never start and bad multipliers count as 1
        private static void CheckPhases(List<BossPhaseModel>? phases, string owner, Scope scope)
        {
            if (phases == null) return;
            var report = scope.Report;
            var ids = new HashSet<string>();
            var thresholds = new HashSet<float>();
            foreach (var phase in phases)
            {
                if (phase == null) continue;
                string path = $"{owner} > phase {(string.IsNullOrEmpty(phase.id) ? "(no id)" : phase.id)}";

                if (string.IsNullOrEmpty(phase.id)) report.Warning(path, "phase has no id");
                else if (!ids.Add(phase.id)) report.Warning(path, "phase id is used twice");
                if (!float.IsFinite(phase.hpBelow) || phase.hpBelow <= 0f || phase.hpBelow >= 1f)
                    report.Warning(path, "hpBelow must be between 0 and 1 (both excluded), otherwise the phase never starts");
                else if (!thresholds.Add(phase.hpBelow))
                    report.Warning(path, "another phase starts at the same hpBelow, they start together");
                if (!NonNegative(phase.ccDurationMul) || !NonNegative(phase.damageMul) || !Positive(phase.spellPowerMul) || !NonNegative(phase.damageTakenMul))
                    report.Warning(path, "multipliers must be 0 or more (spellPowerMul above 0); invalid ones count as 1");
                if (phase.ccImmune != null)
                    foreach (var name in phase.ccImmune)
                        if (!ControlNames.All.Contains(name)) report.Warning(path, $"ccImmune '{name}' is not one of: {string.Join(", ", ControlNames.All)}");

                CheckText(phase.announce, path + " > announce", report);
                if (phase.actions == null) continue;
                for (int i = 0; i < phase.actions.Count; i++)
                    if (phase.actions[i] is { } action) CheckAction(action, $"{path} > action {i + 1} ({action.type})", scope);
            }
        }

        private static void CheckParams(IReadOnlyList<ParamInfo> parameters, Func<string, float?> number, string? effect, string path, ValidationReport report,
            Func<string, string?>? text = null)
        {
            foreach (var p in parameters)
            {
                bool missing = p.Kind switch
                {
                    ParamKind.Effect => string.IsNullOrWhiteSpace(effect),
                    ParamKind.Mob => string.IsNullOrWhiteSpace(text?.Invoke(p.Name)),
                    _ => number(p.Name) == null
                };
                if (missing)
                {
                    if (p.Required) report.Warning(path, $"'{p.Name}' is required, the rule never fires");
                    continue;
                }
                if (p.Kind is ParamKind.Effect or ParamKind.Mob) continue;
                if (!InRange(number(p.Name)!.Value, p.Min, p.Max))
                    report.Warning(path, $"'{p.Name}' must be between {Num(p.Min)} and {Num(p.Max)}");
            }
        }

        private static void ReportUnread(string[] fields, Func<string, float?> number, VocabularyEntry entry, string? type, string path, ValidationReport report)
        {
            foreach (var field in fields)
                if (number(field) != null && !entry.Params.Any(p => p.Name == field))
                    report.Info(path, $"'{field}' is not read by {type}");
        }
    }
}
