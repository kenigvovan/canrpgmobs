using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Skills in simple mode: list, setup page, conditions.
    public partial class MobEditorDialog
    {
        private int? skillEdit;

        // coming back to the tab should show the skill list, not the last sub-screen
        private void CloseSkillScreens()
        {
            skillEdit = null;
            spellBrowser = false;
            spellReplace = null;
            spellFilter = spellFilterDraft = "";
            spellSelected = null;
            spellPage = 0;
        }

        private void SimpleSkillRows(List<FormRow> rows, double x, JObject d, ResolvedMob? eff)
        {
            if (skillEdit is { } editing && d["skills"] is JArray editList && editing >= 0 && editing < editList.Count && editList[editing] is JObject edited)
            {
                SkillConfigRows(rows, x, d, edited, editing);
                return;
            }
            skillEdit = null;

            bool mob = session.Section == "mobs";
            bool replace = ReadString(d, "skillsMode") == MergeModes.Replace;
            if (mob)
            {
                rows.Add(new FormRow(1, (c, y) => InheritToggle(c, x, y, L("canrpgmobs:editor-skill-inherit", "Race and role skills"), "skillsMode", !replace, () =>
                {
                    // off just drops them; copying is its own button below the list
                    if (replace) ShowInheritedSkills(d, eff);
                    else d["skillsMode"] = MergeModes.Replace;
                })));
            }

            rows.Add(new FormRow(1, (c, y) =>
            {
                c.AddStaticText(L("canrpgmobs:editor-skill-own", "Own skills"), UiKit.SectionFont(), B(x, y + 2, 250))
                    .AddSmallButton(L("canrpgmobs:editor-skill-add-spell", "+ Spell"), () => OpenSpellBrowser(null), B(x + PanelW - 300, y, 146), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-skill-add-action", "+ Action"), () => OpenPick("actionsNamed", L("canrpgmobs:editor-skill-pick-action", "Action"), null,
                        v => AddSkill(d, SkillPresets.Action(v, NextSkillId(d)))), B(x + PanelW - 150, y, 146), EnumButtonStyle.Small);
            }));

            var own = d["skills"] as JArray;
            if (own == null || own.Count == 0) rows.Add(TextRow(x, L("canrpgmobs:editor-skill-none", "No own skills yet."), false));
            for (int i = 0; own != null && i < own.Count; i++)
            {
                if (own[i] is not JObject rule) continue;
                int index = i;
                rows.Add(new FormRow(1, (c, y) =>
                {
                    c.AddFitText(SkillTitle(rule), EditorStyle.Body(), B(x + 4, y + 4, 230))
                        .AddStaticText(SkillSummary(rule), HelpFont(), B(x + 238, y, PanelW - 238 - 136, RowStep))
                        .AddSmallButton(L("canrpgmobs:editor-skill-configure", "Set up"), () => { skillEdit = index; Compose(); return true; },
                            B(x + PanelW - 132, y, 88), EnumButtonStyle.Small);
                    RemoveButton(c, x, y, () =>
                    {
                        own.RemoveAt(index);
                        RemoveIfEmpty(d, "skills");
                    });
                }));
            }

            if (!mob || replace || eff == null) return;
            var inherited = InheritedSkills(eff);
            if (inherited.Count == 0) return;
            rows.Add(new FormRow(1, (c, y) =>
                c.AddStaticText(L("canrpgmobs:editor-skill-inherited", "From race and role:"), EditorStyle.FieldLabel(), B(x, y + 4, PanelW - 260))
                    .AddSmallButton(L("canrpgmobs:editor-skill-copy-own", "Copy to own to edit"), () => { HideInheritedSkills(d, eff); Compose(); return true; },
                        B(x + PanelW - 254, y, 250), EnumButtonStyle.Small)));
            foreach (var (rule, source) in inherited)
            {
                var shown = rule;
                string from = source;
                rows.Add(new FormRow(1, (c, y) =>
                    c.AddFitText(SkillTitle(shown) + "  (" + from + ")", UiKit.LabelFont(), B(x + 4, y + 4, 230))
                        .AddStaticText(SkillSummary(shown), HelpFont(), B(x + 238, y, PanelW - 238, RowStep))));
            }
        }

        private ResolvedMob? inheritedFor;
        private List<(JObject rule, string source)>? inheritedSkills;

        // cached per resolved mob instance; callers must clone before editing
        private List<(JObject rule, string source)> InheritedSkills(ResolvedMob eff)
        {
            if (ReferenceEquals(eff, inheritedFor) && inheritedSkills != null) return inheritedSkills;
            var list = new List<(JObject, string)>();
            foreach (var rule in eff.Race.skills ?? new List<SkillRuleModel>())
                if (rule != null) list.Add((JObject.FromObject(rule, Serializer), FromLabel(true)));
            foreach (var rule in eff.Role.skills ?? new List<SkillRuleModel>())
                if (rule != null) list.Add((JObject.FromObject(rule, Serializer), FromLabel(false)));
            inheritedFor = eff;
            return inheritedSkills = list;
        }

        // replace mode drops race/role skills, so copy them into own skills first (non-default fields only)
        private void HideInheritedSkills(JObject d, ResolvedMob? eff)
        {
            d["skillsMode"] = MergeModes.Replace;
            if (eff == null) return;
            var list = EnsureArray(d, "skills");
            var defaults = JObject.FromObject(new SkillRuleModel(), Serializer);
            int at = 0;
            foreach (var (rule, _) in InheritedSkills(eff))
            {
                var copy = StripDefaults(rule, defaults);
                string ruleId = copy["id"]?.ToString() ?? "";
                if (ruleId.Length == 0 || list.OfType<JObject>().Any(r => r["id"]?.ToString() == ruleId)) copy["id"] = NextSkillId(d);
                list.Insert(at++, copy);
            }
        }

        // remove unchanged copies made by HideInheritedSkills so they don't run twice; edited ones stay
        private void ShowInheritedSkills(JObject d, ResolvedMob? eff)
        {
            d.Remove("skillsMode");
            if (eff == null || d["skills"] is not JArray list) return;
            var defaults = JObject.FromObject(new SkillRuleModel(), Serializer);
            var inherited = InheritedSkills(eff).Select(t => WithoutId(StripDefaults(t.rule, defaults))).ToList();
            foreach (var own in list.OfType<JObject>().ToList())
            {
                var plain = WithoutId(StripDefaults(own, defaults));
                int match = inherited.FindIndex(r => JToken.DeepEquals(r, plain));
                if (match < 0) continue;
                inherited.RemoveAt(match);
                own.Remove();
            }
            RemoveIfEmpty(d, "skills");
        }

        private static JObject StripDefaults(JObject rule, JObject defaults)
        {
            var copy = (JObject)rule.DeepClone();
            foreach (var property in copy.Properties().ToList())
                if (property.Name != "id" && defaults[property.Name] is { } fallback && JToken.DeepEquals(property.Value, fallback)) property.Remove();
            return copy;
        }

        private static JObject WithoutId(JObject rule)
        {
            rule.Remove("id");
            return rule;
        }

        private void AddSkill(JObject d, JObject rule)
        {
            var list = EnsureArray(d, "skills");
            list.Add(rule);
            skillEdit = list.Count - 1;
        }

        private static string NextSkillId(JObject d)
        {
            var taken = new HashSet<string>((d["skills"] as JArray)?.OfType<JObject>().Select(r => r["id"]?.ToString() ?? "") ?? Enumerable.Empty<string>());
            for (int n = 1; ; n++)
                if (!taken.Contains("skill" + n)) return "skill" + n;
        }

        private string SkillTitle(JObject rule)
        {
            if (rule["action"] is JObject action) return ActionName(ReadString(action, "type"));
            string spell = ReadString(rule, "spell");
            return spell.Length == 0 ? L("canrpgmobs:editor-skill-no-spell", "(no spell)") : $"{PickLists.SpellTag(mod, spell)} {SpellDisplayName(spell)}";
        }

        private string SkillSummary(JObject rule)
        {
            string target = ReadString(rule, "target");
            string text = L("canrpgmobs:editor-skill-summary", "{0} · {1}-{2} blocks · cooldown {3} s · priority {4}",
                SelectorName(target.Length > 0 ? target : SkillTargets.CurrentEnemy), Display(ReadFloat(rule, "minRange", 0f)), Display(ReadFloat(rule, "maxRange", 16f)),
                Display(ReadFloat(rule, "cooldown", 5f)), Display(ReadFloat(rule, "priority", 50f)));
            int conditions = (rule["conditions"] as JArray)?.Count ?? 0;
            return conditions > 0 ? text + " · " + L("canrpgmobs:editor-skill-conditions-count", "conditions: {0}", conditions) : text;
        }

        private void SkillConfigRows(List<FormRow> rows, double x, JObject d, JObject rule, int index)
        {
            double cx = x + SimpleLabelW;
            rows.Add(new FormRow(1, (c, y) =>
            {
                c.AddFitText(SkillTitle(rule), UiKit.SectionFont(), B(x, y + 2, PanelW - 300))
                    .AddSmallButton(L("canrpgmobs:editor-skill-back", "Back to skills"), () => { skillEdit = null; Compose(); return true; },
                        B(x + PanelW - 296, y, 150), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-mode-advanced", "Advanced mode"), () => OpenSkillAdvanced(index), B(x + PanelW - 142, y, 138), EnumButtonStyle.Small);
            }));

            if (rule["action"] is JObject action)
            {
                string type = ReadString(action, "type");
                rows.Add(TextRow(x, L("canrpgmobs:editor-skill-action", "Action: {0}", ActionName(type)), false));
                if (MobsApi.Vocabulary.Actions.TryGetValue(type, out var entry)) AddParamRows(rows, x, action, entry.Params);
            }
            else
            {
                rows.Add(new FormRow(1, (c, y) =>
                {
                    string spell = ReadString(rule, "spell");
                    SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-spell", "Spell"), null, () => SpellReasons(ReadString(rule, "spell")));
                    c.AddFitButton(spell.Length > 0 ? SpellDisplayName(spell) : L("canrpgmobs:editor-simple-choose", "Choose..."), () => OpenSpellBrowser(index),
                        B(cx, y, SimpleControlW), EnumButtonStyle.Small);
                }));
            }

            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-target", "Target"), "target");
                var ids = MobsApi.Vocabulary.Selectors.Keys.OrderBy(k => k).ToArray();
                string current = ReadString(rule, "target");
                if (current.Length == 0) current = SkillTargets.CurrentEnemy;
                c.AddDropDown(ids, ids.Select(SelectorName).ToArray(), Math.Max(0, Array.IndexOf(ids, current)), (code, _) =>
                {
                    if (code == SkillTargets.CurrentEnemy) rule.Remove("target");
                    else rule["target"] = code;
                    Compose();
                }, B(cx, y, SimpleControlW), NextKey());
            }));
            string targetId = ReadString(rule, "target");
            if (MobsApi.Vocabulary.Selectors.TryGetValue(targetId.Length > 0 ? targetId : SkillTargets.CurrentEnemy, out var selector))
                AddParamRows(rows, x, rule, selector.Params.Where(p => p.Name is not ("minRange" or "maxRange")).ToList());

            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-range", "Range (min / max)"), "skillRange");
                var spec = new FieldSpec("range", FieldKind.Number) { Min = 0, Max = 128 };
                NumberInput(c, B(cx, y, 96), ReadNumber(rule["minRange"]), v => SetNumber(rule, "minRange", v, spec));
                NumberInput(c, B(cx + 104, y, 96), ReadNumber(rule["maxRange"]), v => SetNumber(rule, "maxRange", v, spec));
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-cooldown", "Cooldown, seconds"), "cooldown");
                NumberInput(c, B(cx, y, 96), ReadNumber(rule["cooldown"]), v => SetNumber(rule, "cooldown", v, new FieldSpec("cooldown", FieldKind.Number) { Min = 0, Max = 3600 }));
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-initial", "First use after, s"), "initialCooldown");
                RangeInputs(c, rule, "initialCooldown", new FieldSpec("initialCooldown", FieldKind.RangePair) { Min = 0, Max = 3600 }, cx, y, 96, 8);
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-priority", "Priority"), "priority");
                NumberInput(c, B(cx, y, 96), ReadNumber(rule["priority"]), v => SetNumber(rule, "priority", v, new FieldSpec("priority", FieldKind.Number) { Min = -10000, Max = 10000 }));
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-skill-stop", "Stands still to cast"), "stopToCast");
                YesNoToggle(c, rule, "stopToCast", true, cx, y, 96);
            }));

            rows.Add(new FormRow(1, (c, y) =>
                c.AddStaticText(L("canrpgmobs:editor-skill-conditions", "Only when"), UiKit.SectionFont(), B(x, y + 2, 260))
                    .AddSmallButton(L("canrpgmobs:editor-skill-add-condition", "+ Condition"), () => OpenPick("conditionsNamed", L("canrpgmobs:editor-skill-pick-condition", "Condition"), null,
                        v => AddCondition(rule, v)), B(x + PanelW - 150, y, 146), EnumButtonStyle.Small)));
            if (rule["conditions"] is JArray { Count: > 0 } conditions)
            {
                foreach (var condition in conditions.OfType<JObject>().ToList())
                {
                    var shown = condition;
                    rows.Add(new FormRow(1, (c, y) => ConditionRow(c, x, y, rule, conditions, shown)));
                }
            }
            else rows.Add(TextRow(x, L("canrpgmobs:editor-skill-no-conditions", "No conditions: used whenever it is ready."), false));
        }

        private void ConditionRow(GuiComposer c, double x, double y, JObject rule, JArray list, JObject condition)
        {
            string type = ReadString(condition, "type");
            bool not = condition["not"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
            c.AddSmallButton(not ? L("canrpgmobs:editor-skill-not", "NOT") : "-", () =>
            {
                if (not) condition.Remove("not");
                else condition["not"] = true;
                Compose();
                return true;
            }, B(x, y, 44), EnumButtonStyle.Small)
                .AddFitText(ConditionName(type), EditorStyle.Body(), B(x + 50, y + 4, 190));
            if (MobsApi.Vocabulary.Conditions.TryGetValue(type, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.Help)) c.AddHoverText(entry.Help, CairoFont.WhiteDetailText(), 300, B(x + 50, y, 190));
                double px = x + 244;
                foreach (var param in entry.Params.Take(3))
                {
                    c.AddFitText(ParamLabel(param.Name), UiKit.LabelFont(), B(px, y + 4, 48));
                    ParamInput(c, px + 50, y, condition, param, 50);
                    px += 104;
                }
            }
            RemoveButton(c, x, y, () =>
            {
                list.Remove(condition);
                RemoveIfEmpty(rule, "conditions");
            });
        }

        private static void AddCondition(JObject rule, string type)
        {
            var list = EnsureArray(rule, "conditions");
            var condition = new JObject { ["type"] = type };
            if (MobsApi.Vocabulary.Conditions.TryGetValue(type, out var entry))
                foreach (var param in entry.Params.Where(p => p.Required))
                    if (SkillPresets.DefaultParam(param) is { } value) condition[param.Name] = value;
            list.Add(condition);
        }

        // three per row
        private void AddParamRows(List<FormRow> rows, double x, JObject target, IReadOnlyList<ParamInfo> parameters)
        {
            for (int i = 0; i < parameters.Count; i += 3)
            {
                var chunk = parameters.Skip(i).Take(3).ToList();
                rows.Add(new FormRow(1, (c, y) =>
                {
                    double px = x + 16;
                    foreach (var param in chunk)
                    {
                        c.AddFitText(ParamLabel(param.Name), UiKit.LabelFont(), B(px, y + 4, 96))
                            .AddHoverText(ParamUnit(param), CairoFont.WhiteDetailText(), 240, B(px, y, 96));
                        ParamInput(c, px + 98, y, target, param, 84);
                        px += 196;
                    }
                }));
            }
        }

        private void ParamInput(GuiComposer c, double x, double y, JObject target, ParamInfo param, double width)
        {
            switch (param.Kind)
            {
                case ParamKind.Effect:
                case ParamKind.Mob:
                {
                    string value = ReadString(target, param.Name);
                    bool mob = param.Kind == ParamKind.Mob;
                    // mobs: named ones in the list, race then role from the icon next to it
                    c.AddFitButton(value.Length > 0 ? Short(value) : "...", () => OpenPick(mob ? "mobsNamed" : param.PickList ?? "", ParamLabel(param.Name), null, v => target[param.Name] = v),
                        B(x, y, mob ? width - 30 : width), EnumButtonStyle.Small);
                    if (mob)
                        c.AddIcon(UiKit.ClassesIcon("dark-squad"), L("canrpgmobs:zones-spawn-add-pair", "+ Race and role"),
                            () => PickRaceRole(key => target[param.Name] = key), B(x + width - 26, y, 26));
                    break;
                }
                case ParamKind.Fraction:
                    // shown as percent
                    NumberInput(c, B(x, y, width), target[param.Name] is JValue { Type: JTokenType.Integer or JTokenType.Float } ? Display(ReadFloat(target, param.Name, 0f) * 100f) : "", v =>
                    {
                        if (v.Trim().Length == 0) target.Remove(param.Name);
                        else if (TryParse(v, out float percent)) target[param.Name] = Math.Clamp(percent, 0f, 100f) / 100f;
                    });
                    break;
                default:
                    NumberInput(c, B(x, y, width), ReadNumber(target[param.Name]), v => SetNumber(target, param.Name, v,
                        new FieldSpec(param.Name, param.Kind == ParamKind.Count ? FieldKind.Int : FieldKind.Number) { Min = param.Min, Max = param.Max }));
                    break;
            }
        }

        private static string ParamUnit(ParamInfo param) => param.Kind switch
        {
            ParamKind.Fraction => L("canrpgmobs:editor-param-unit-fraction", "percent, 0-100"),
            ParamKind.Blocks => L("canrpgmobs:editor-param-unit-blocks", "blocks"),
            ParamKind.Seconds => L("canrpgmobs:editor-param-unit-seconds", "seconds"),
            ParamKind.Count => L("canrpgmobs:editor-param-unit-count", "how many"),
            ParamKind.Multiplier => L("canrpgmobs:editor-param-unit-multiplier", "multiplier, 1 = unchanged"),
            _ => L("canrpgmobs:editor-param-unit-pick", "pick from the list")
        };

        private bool OpenSkillAdvanced(int index)
        {
            simpleMode = false;
            session.PopTo(0);
            if (session.Child("skills") is { } child) session.OpenItem(child, index);
            tab = Tab.Fields;
            Compose();
            return true;
        }

        private static string VocabName(string kind, string id)
        {
            string key = $"canrpgmobs:vocab-{kind}-{id}";
            return Lang.HasTranslation(key, false, false) ? Lang.Get(key) : id;
        }

        private static string SelectorName(string id) => VocabName("selector", id);
        private static string ConditionName(string id) => VocabName("condition", id);
        private static string ActionName(string id) => VocabName("action", id);
        private static string ParamLabel(string name) => L("canrpgmobs:editor-param-" + name, name);
    }
}
