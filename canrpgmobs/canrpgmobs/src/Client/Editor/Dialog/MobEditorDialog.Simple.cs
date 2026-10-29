using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Simple mode. Edits the same draft as the advanced tree, so switching modes loses nothing.
    public partial class MobEditorDialog
    {
        private const double SimpleLabelW = 170;
        private const double SimpleControlW = 200;
        private static readonly string[] SimpleSections = { "mobs", "races", "roles", "lootTables", "zones" };

        private bool simpleMode = true;
        private string simpleTab = "main";
        private string simplePreset = "";
        private int simplePage;
        private readonly DraftResolver resolver = new();

        private bool SimpleAvailable => SimpleSections.Contains(session.Section);
        private bool SimpleActive => simpleMode && page == Page.Editor && session.Draft != null && SimpleAvailable;

        private string[] SimpleTabs => Section(session.Section)?.Tabs ?? PlainTabs;

        private bool effectiveKnown;
        private ResolvedMob? effectiveMob;
        private string? effectiveError;

        // used several times per compose, resolve once
        private ResolvedMob? Effective(out string? error)
        {
            if (!effectiveKnown)
            {
                if (page == Page.Wizard && wizard != null)
                {
                    string id = WizardTemplateId(wizard);
                    effectiveMob = resolver.ResolveBundle(mod.Catalog, BuildWizardBundle(wizard, id, out _), id, out effectiveError);
                }
                else effectiveMob = resolver.Resolve(mod.Catalog, session.Section, session.Draft, out effectiveError);
                effectiveKnown = true;
            }
            error = effectiveError;
            return effectiveMob;
        }

        // also bumps the session version (preview and IsDirty rely on it)
        private void ForgetEffective()
        {
            InvalidateEffective();
            session.Touch();
        }

        // no version bump, used by live texts which aren't edits
        private void InvalidateEffective() => effectiveKnown = false;

        private void AddModeToggle(GuiComposer compo, double x, double y)
        {
            if (!SimpleAvailable) return;
            compo.AddSmallButton(simpleMode ? L("canrpgmobs:editor-mode-advanced", "Advanced mode") : L("canrpgmobs:editor-mode-simple", "Simple mode"),
                () => { simpleMode = !simpleMode; ClearPending(); Compose(); return true; }, B(x, y, 130), EnumButtonStyle.Small);
        }

        private void AddSimplePanel(GuiComposer compo, double x, double y)
        {
            var draft = session.Draft!;
            var tabIds = SimpleTabs;
            if (Array.IndexOf(tabIds, simpleTab) < 0) simpleTab = tabIds[0];

            bool canReturn = session.Section == "lootTables" && lootReturn != null;
            bool problems = !canReturn && session.Problems.Count > 0;
            bool narrow = canReturn || problems;
            PanelInset(compo, x, y, 58);
            LiveText(compo, B(x, y + 2, PanelW - (narrow ? 280 : 140), 24), UiKit.SectionFont(),
                () => Clip((session.IsDirty ? "* " : "") + SimpleTitle(draft), narrow ? 36 : 56));
            if (canReturn)
                compo.AddSmallButton(L("canrpgmobs:editor-loot-back", "Back to {0}", Short(lootReturn!.Value.Id)),
                    () => ReturnFromLoot(null), B(x + PanelW - 270, y, 134), EnumButtonStyle.Small);
            else if (problems)
                compo.AddSmallButton(simpleProblems ? L("canrpgmobs:editor-simple-problems-back", "Back to fields") : L("canrpgmobs:editor-simple-problems", "Problems: {0}", session.Problems.Count),
                    () => { simpleProblems = !simpleProblems; Compose(); return true; }, B(x + PanelW - 270, y, 134), EnumButtonStyle.Small);
            if (!problems) simpleProblems = false;
            AddModeToggle(compo, x + PanelW - 130, y);

            var tabs = tabIds.Select((t, i) => new GuiTab { Name = L("canrpgmobs:editor-stab-" + t, t), DataInt = i }).ToArray();
            compo.AddHorizontalTabs(tabs, B(x, y + 30, PanelW, 26), i => { simpleTab = tabIds[i]; simpleProblems = false; ClearPending(); CloseSkillScreens(); Compose(); },
                CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor), "stabs");
            int active = Array.IndexOf(tabIds, simpleTab);
            afterCompose.Add(() => SingleComposer.GetHorizontalTabs("stabs").activeElement = active);

            double bodyY = y + 66;
            if (simpleProblems)
            {
                AddSimpleProblems(compo, x, bodyY);
                return;
            }
            var eff = Effective(out string? error);
            if (simpleTab == "gear")
            {
                AddSimpleGear(compo, x, bodyY, draft, eff);
                return;
            }
            if (simpleTab == "skills" && spellBrowser)
            {
                AddSpellBrowser(compo, x, bodyY, draft);
                return;
            }

            var rows = new List<FormRow>();
            switch (simpleTab)
            {
                case "main": SimpleMainRows(rows, x, draft); break;
                case "look": SimpleLookRows(rows, x, draft, eff); break;
                case "combat": SimpleCombatRows(rows, x, draft, eff, error); break;
                case "skills": SimpleSkillRows(rows, x, draft, eff); break;
                case "loot": SimpleLootRows(rows, x, draft, eff); break;
                case "table": LootTableRows(rows, x, draft); break;
                case "test": LootTestRows(rows, x, draft); break;
                case "zspawns": ZoneSpawnRows(rows, x, draft); break;
                case "zdensity": ZoneDensityRows(rows, x, draft); break;
                case "zshape": ZoneShapeRows(rows, x, draft); break;
            }
            DrawPaged(compo, x, bodyY, rows, FormRows, simplePage, p => simplePage = p);
        }

        private bool simpleProblems;

        // two lines each, long validator messages fit; the advanced tab can still jump to the field
        private void AddSimpleProblems(GuiComposer compo, double x, double y)
        {
            var rows = session.Problems.Select(problem => new FormRow(2, (c, ry) =>
            {
                string tail = Tail(problem.Path);
                string text = (tail.Length > 0 ? tail + ": " : "") + problem.Message;
                c.AddStaticText(problem.Severity == Severity.Error ? L("canrpgmobs:editor-problem-error", "error") : L("canrpgmobs:editor-problem-warning", "warning"),
                        EditorStyle.Status(problem.Severity == Severity.Error), B(x, ry + 4, 80))
                    .AddStaticText(Clip(text, 180), EditorStyle.Body(), B(x + 84, ry + 4, PanelW - 84, RowStep * 2 - 6));
            })).ToList();
            DrawPaged(compo, x, y, rows, FormRows, problemPage, p => problemPage = p);
        }

        // after a failed save or a validate
        private void ShowProblems()
        {
            tab = Tab.Problems;
            simpleProblems = true;
        }

        private void SimpleMainRows(List<FormRow> rows, double x, JObject d)
        {
            double cx = x + SimpleLabelW;
            double half = SimpleControlW / 2 - 2;
            rows.Add(CardRow(x, L("canrpgmobs:editor-card-identity", "Name and id")));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-name", "Name"), "name");
                TextInput(c, B(cx, y, SimpleControlW), (d["name"] as JObject)?["en"]?.ToString() ?? "", v => SetLocText(d, "name", "en", v));
                // translations come from lang files under this key
                if (TranslationKey(d) is { } key)
                {
                    string status = Lang.HasTranslation(key, false, false)
                        ? L("canrpgmobs:editor-name-translated", "{0}: {1}", Lang.CurrentLocale, Lang.GetUnformatted(key))
                        : L("canrpgmobs:editor-name-key", "translate via {0}", key);
                    c.AddFitText(status, HelpFont(), B(cx + SimpleControlW + 6, y + 6, PanelW - SimpleLabelW - SimpleControlW - 6, 16));
                }
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-id", "Id"), "id");
                TextInput(c, B(cx, y, SimpleControlW), ReadString(d, "id"), v => SetString(d, "id", v, true));
            }));

            var data = mod.Catalog.Data;
            switch (session.Section)
            {
                case "mobs":
                {
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-what", "What it is")));
                    rows.Add(PickRow(x, d, "race", "racesNamed", "races", L("canrpgmobs:editor-simple-race", "Race"), open: true));
                    rows.Add(PickRow(x, d, "role", "rolesNamed", "roles", L("canrpgmobs:editor-simple-role", "Role"), open: true));
                    rows.Add(new FormRow(1, (c, y) =>
                    {
                        SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-levels", "Levels (min / max)"), "levelRange");
                        RangeInputs(c, d, "levelRange", new FieldSpec("levelRange", FieldKind.IntRangePair) { Min = 1, Max = 1000 }, cx, y, half, 4);
                    }));
                    var tiers = data.Tiers.Keys.OrderBy(k => k).ToArray();
                    rows.Add(ChoiceRow(x, d, "defaultTier", L("canrpgmobs:editor-simple-tier", "Tier"), tiers, tiers.Select(t => NameOf("tiers", t)).ToArray(),
                        L("canrpgmobs:editor-simple-normal-tier", "(normal)")));
                    var factions = FactionIds();
                    // with no own faction the resolved one is the race's
                    string? raceFaction = d["faction"] == null ? Effective(out _)?.Faction : null;
                    rows.Add(ChoiceRow(x, d, "faction", L("canrpgmobs:editor-simple-faction", "Faction"), factions, factions.Select(f => NameOf("factions", f)).ToArray(),
                        raceFaction != null ? L("canrpgmobs:editor-simple-race-faction-of", "(race's: {0})", NameOf("factions", raceFaction)) : L("canrpgmobs:editor-simple-race-faction", "(race's)")));
                    if (session.OpenedId != null)
                        rows.Add(new FormRow(1, (c, y) =>
                        {
                            // the copy button sits in the same card
                            SimpleLabel(c, x, y, L("canrpgmobs:editor-copy-full", "Independent copy"), "copyFull");
                            c.AddSmallButton(L("canrpgmobs:editor-copy-full-button", "Copy with race and role"), OnCopyWithRaceRole, B(cx, y, SimpleControlW), EnumButtonStyle.Small);
                        }));
                    break;
                }
                case "races":
                {
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-body-side", "Body and side")));
                    rows.Add(new FormRow(1, (c, y) =>
                    {
                        SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-body", "Body"), "body");
                        var bodies = MobBodies.All.Keys.ToArray();
                        c.AddDropDown(bodies, bodies.Select(b => L("canrpgmobs:wizard-body-" + b, b)).ToArray(), Math.Max(0, Array.IndexOf(bodies, ReadString(d, "body"))),
                            (code, _) =>
                            {
                                d["body"] = code;
                                // validator expects appearance kind to match the body
                                if (d["appearance"] is not JObject look) d["appearance"] = look = NewAppearance(d);
                                bool creature = MobBodies.AppearanceKind(code) == AppearanceKinds.Shape;
                                look["kind"] = creature ? AppearanceKinds.Shape : AppearanceKinds.Plm;
                                look.Remove(creature ? "model" : "shape");
                                Compose();
                            }, B(cx, y, SimpleControlW), NextKey());
                    }));
                    var factions = FactionIds();
                    rows.Add(ChoiceRow(x, d, "faction", L("canrpgmobs:editor-simple-faction", "Faction"), factions, factions.Select(f => NameOf("factions", f)).ToArray(),
                        L("canrpgmobs:editor-simple-default", "(default)")));
                    rows.Add(UsedByRow(x, t => t.race));
                    break;
                }
                case "zones":
                    ZoneMainRows(rows, x, d);
                    break;
                default:
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-fight", "How it fights")));
                    rows.Add(BehaviorRow(x, d));
                    rows.Add(PickRow(x, d, "aiProfile", "aiProfilesNamed", "aiProfiles", L("canrpgmobs:editor-simple-aiProfile", "Behavior (AI profile)")));
                    rows.Add(UsedByRow(x, t => t.role));
                    break;
            }
        }

        // the wizard's fighting styles; bosses are a mob thing (tier and phases), not a role
        private FormRow BehaviorRow(double x, JObject d) => new(1, (c, y) =>
        {
            c.AddStaticText(L("canrpgmobs:wiz-fight-help", "How does it fight?"), EditorStyle.FieldLabel(), B(x, y + 4, SimpleLabelW - 6));
            var data = mod.Catalog.Data;
            string profile = Q(ReadString(d, "aiProfile"));
            string archetype = data.AiProfiles.TryGetValue(profile, out var ai) ? ai.archetype : MobValidator.DefaultArchetype;
            var styles = WizardRoles.Where(r => !r.Boss).ToArray();
            double each = (PanelW - SimpleLabelW - (styles.Length - 1) * 3) / styles.Length;
            double bx = x + SimpleLabelW;
            foreach (var style in styles)
            {
                var role = style;
                c.AddSmallButton((role.Archetype == archetype ? "> " : "") + L("canrpgmobs:wiz-name-" + role.Code, role.Code), () =>
                {
                    d["aiProfile"] = AiProfileFor(role);
                    Compose();
                    return true;
                }, B(bx, y, each), EnumButtonStyle.Small);
                bx += each + 3;
            }
        });

        // who a race or role edit reaches
        private FormRow UsedByRow(double x, Func<MobTemplateModel, string> link) => new(1, (c, y) =>
        {
            string id = Q(session.OpenedId ?? session.DraftId);
            var users = mod.Catalog.Templates.Values.Where(t => link(t) == id).Select(t => Named(t.name) ?? Short(t.id)).OrderBy(n => n).ToList();
            string text = users.Count == 0
                ? L("canrpgmobs:editor-used-by-none", "No named mob uses it yet.")
                : L("canrpgmobs:editor-used-by", "Used by {0} mob(s): {1}", users.Count, string.Join(", ", users.Take(4)) + (users.Count > 4 ? ", ..." : ""));
            c.AddFitText(text, HelpFont(), B(x, y + 6, PanelW, 24));
        });

        private void SimpleLookRows(List<FormRow> rows, double x, JObject d, ResolvedMob? eff)
        {
            double cx = x + SimpleLabelW;
            bool isRace = session.Section == "races";
            var own = d["appearance"] as JObject;
            string kind = isRace ? own?["kind"]?.ToString() ?? AppearanceKinds.Plm : eff?.Appearance.kind ?? AppearanceKinds.Plm;

            rows.Add(CardRow(x, L("canrpgmobs:editor-card-body", "Body")));
            if (kind == AppearanceKinds.Shape)
                rows.Add(LookPickRow(x, d, "shape", "shapes", L("canrpgmobs:editor-simple-shape", "Shape"), isRace ? null : eff?.Appearance.shape));
            else
                rows.Add(LookPickRow(x, d, "model", "plmModels", L("canrpgmobs:editor-simple-model", "Model"), isRace ? null : eff?.Appearance.model));

            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-size", "Size"), "size");
                var spec = new FieldSpec("size", FieldKind.Number) { Min = 0.1f, Max = 5f };
                NumberInput(c, B(cx, y, 120), ReadNumber((d["appearance"] as JObject)?["size"]), v =>
                {
                    if (d["appearance"] is not JObject look)
                    {
                        if (v.Trim().Length == 0) return;
                        d["appearance"] = look = NewAppearance(d);
                    }
                    SetNumber(look, "size", v, spec);
                    CleanAppearance(d);
                });
            }));

            var effPresets = eff?.Appearance.presets;
            rows.Add(CardRow(x, L("canrpgmobs:editor-card-variants", "Look variants")));
            if (!isRace && own?["presets"] is not JArray)
            {
                rows.Add(new FormRow(1, (c, y) =>
                {
                    c.AddFitText(L("canrpgmobs:editor-simple-race-presets", "Look variants come from the race ({0}).", effPresets?.Count ?? 0),
                            UiKit.LabelFont(), B(x, y + 4, PanelW - 260))
                        .AddSmallButton(L("canrpgmobs:editor-simple-own-presets", "Own look variants for this mob"), () =>
                        {
                            if (d["appearance"] is not JObject look) d["appearance"] = look = NewAppearance(d);
                            look["presets"] = JArray.FromObject(effPresets ?? new List<AppearancePresetModel>(), Serializer);
                            Compose();
                            return true;
                        }, B(x + PanelW - 254, y, 250), EnumButtonStyle.Small);
                }));
                return;
            }

            var presets = (d["appearance"] as JObject)?["presets"] as JArray;
            var presetObjects = presets?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (presetObjects.Count == 0)
            {
                rows.Add(TextRow(x, L("canrpgmobs:editor-simple-no-presets", "No look variants."), false));
                return;
            }
            var ids = presetObjects.Select(p => p["id"]?.ToString() ?? "").ToArray();
            if (Array.IndexOf(ids, simplePreset) < 0) simplePreset = ids[0];
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-simple-preset", "Look variant"), "preset");
                c.AddDropDown(ids, ids, Math.Max(0, Array.IndexOf(ids, simplePreset)), (code, _) => { simplePreset = code; Compose(); }, B(cx, y, SimpleControlW), NextKey());
            }));
            if (kind != AppearanceKinds.Plm) return;

            var preset = presetObjects.First(p => (p["id"]?.ToString() ?? "") == simplePreset);
            var partCodes = PickLists.Get("plmParts", capi, mod, session, null).Select(o => o.Value).ToList();
            if (partCodes.Count == 0 && preset["parts"] is JObject known) partCodes = known.Properties().Select(p => p.Name).ToList();
            foreach (var part in partCodes)
            {
                string partCode = part;
                rows.Add(new FormRow(1, (c, y) =>
                {
                    string raw = (preset["parts"] as JObject)?[partCode]?.ToString() ?? "";
                    string value = raw.Trim() == AppearanceParts.Any ? L("canrpgmobs:editor-part-any", "any (random)")
                        : AppearanceParts.IsRandom(raw) ? L("canrpgmobs:editor-part-random", "random: {0}", string.Join(", ", AppearanceParts.Options(raw)))
                        : raw.Length > 0 ? raw : "-";
                    c.AddFitText(partCode, UiKit.LabelFont(), B(x + 16, y + 4, SimpleLabelW - 22))
                        .AddFitText(value, UiKit.ValueFont(), B(cx, y + 4, SimpleControlW))
                        .AddIcon("left", L("canrpgmobs:editor-tip-prev-variant", "Previous variant"), () => CyclePart(preset, partCode, -1), B(cx + SimpleControlW + 4, y, 30))
                        .AddIcon("right", L("canrpgmobs:editor-tip-next-variant", "Next variant"), () => CyclePart(preset, partCode, 1), B(cx + SimpleControlW + 38, y, 30))
                        // plus adds a variant to the random list, dice = any variant
                        .AddIcon("plus", L("canrpgmobs:editor-tip-add-variant", "Add a variant to pick from at random"),
                            () => OpenPick("plmVariants", partCode, partCode, v => AddPartOption(preset, partCode, v)), B(cx + SimpleControlW + 72, y, 30))
                        .AddIcon("dice", L("canrpgmobs:editor-tip-any-variant", "Any variant at random / back to one"), () => TogglePartAny(preset, partCode),
                            B(cx + SimpleControlW + 106, y, 30));
                }));
            }
        }

        private bool CyclePart(JObject preset, string part, int step)
        {
            var map = EnsureObject(preset, "parts");
            return CycleVariant(map, new KeyHolder(part), step);
        }

        private void AddPartOption(JObject preset, string part, string variant)
        {
            var map = EnsureObject(preset, "parts");
            string raw = map[part]?.ToString() ?? "";
            var options = raw.Trim() == AppearanceParts.Any ? new List<string>() : AppearanceParts.Options(raw).ToList();
            if (!options.Contains(variant)) options.Add(variant);
            map[part] = string.Join(AppearanceParts.Separator.ToString(), options);
        }

        private bool TogglePartAny(JObject preset, string part)
        {
            var map = EnsureObject(preset, "parts");
            if (map[part]?.ToString().Trim() == AppearanceParts.Any)
            {
                var first = PickLists.Get("plmVariants", capi, mod, session, part).FirstOrDefault();
                if (first != null) map[part] = first.Value;
                else map.Remove(part);
            }
            else map[part] = AppearanceParts.Any;
            Compose();
            return true;
        }

        private FormRow LookPickRow(double x, JObject d, string key, string list, string label, string? inherited) => new(1, (c, y) =>
        {
            string value = (d["appearance"] as JObject)?[key]?.ToString() ?? "";
            SimpleLabel(c, x, y, label, key, () =>
                string.IsNullOrEmpty(inherited) || (d["appearance"] as JObject)?[key]?.ToString() is { Length: > 0 }
                    ? null
                    : L("canrpgmobs:editor-simple-effective", "Result: {0}", inherited!));
            c.AddFitButton(value.Length > 0 ? value : L("canrpgmobs:editor-simple-choose", "Choose..."), () => OpenPick(list, label, null, v =>
            {
                if (d["appearance"] is not JObject look) d["appearance"] = look = NewAppearance(d);
                look[key] = v;
            }), B(x + SimpleLabelW, y, SimpleControlW), EnumButtonStyle.Small);
        });

        private void SimpleCombatRows(List<FormRow> rows, double x, JObject d, ResolvedMob? eff, string? error)
        {
            if (session.Section == "mobs" && eff == null)
                rows.Add(TextRow(x, L("canrpgmobs:editor-simple-unresolved", "Can't build this mob: {0}", error ?? "?"), true));

            FieldSpec Num(string key, float min, float max) => new(key, FieldKind.Number) { Min = min, Max = max };
            // re-evaluated while typing
            string? Result(Func<ResolvedMob, string> read) => Effective(out _) is { } mob ? read(mob) : null;

            switch (session.Section)
            {
                case "mobs":
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-stats", "Stats at level 1")));
                    rows.Add(StatRow(x, d, "stats", "maxHealth", L("canrpgmobs:editor-simple-hp", "Health"), Num("maxHealth", 1, 100000),
                        () => Result(m => Display(m.MaxHealth))));
                    rows.Add(StatRow(x, d, "stats", "meleeDamage", L("canrpgmobs:editor-simple-damage", "Melee damage"), Num("meleeDamage", 0, 10000),
                        () => Result(m => WeaponNote(m) ?? Display(m.MeleeDamage))));
                    rows.Add(PresetStatRow(x, d, "stats", "moveSpeed", L("canrpgmobs:editor-simple-speed", "Move speed"), Num("moveSpeed", 0, 0.2f), SpeedPresets,
                        () => Result(m => Display(m.MoveSpeed))));
                    rows.Add(StatRow(x, d, "stats", "damageReduction", L("canrpgmobs:editor-simple-dr", "Damage reduction"), Num("damageReduction", 0, 0.9f),
                        () => Result(m => Display(m.DamageReduction))));
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-behavior", "Behavior")));
                    rows.Add(PickRow(x, d, "aiProfile", "aiProfilesNamed", "aiProfiles", L("canrpgmobs:editor-simple-aiProfile", "Behavior (AI profile)"),
                        inherited: eff?.Role.aiProfile is { Length: > 0 } roleAi ? roleAi : eff?.Ai.id is { Length: > 0 } aiId ? aiId : null));
                    break;
                case "races":
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-base-stats", "Base stats")));
                    rows.Add(StatRow(x, d, "stats", "maxHealth", L("canrpgmobs:editor-simple-hp", "Health"), Num("maxHealth", 1, 100000), null));
                    rows.Add(StatRow(x, d, "stats", "meleeDamage", L("canrpgmobs:editor-simple-damage", "Melee damage"), Num("meleeDamage", 0, 10000), null));
                    rows.Add(StatRow(x, d, "stats", "meleeTier", L("canrpgmobs:editor-simple-meleeTier", "Melee tier"),
                        new FieldSpec("meleeTier", FieldKind.Int) { Min = 0, Max = 10 }, null));
                    rows.Add(PresetStatRow(x, d, "stats", "moveSpeed", L("canrpgmobs:editor-simple-speed", "Move speed"), Num("moveSpeed", 0, 0.2f), SpeedPresets, null));
                    rows.Add(StatRow(x, d, "stats", "damageReduction", L("canrpgmobs:editor-simple-dr", "Damage reduction"), Num("damageReduction", 0, 0.9f), null));
                    break;
                default:
                    rows.Add(CardRow(x, L("canrpgmobs:editor-card-multipliers", "Multipliers over the race")));
                    rows.Add(PresetStatRow(x, d, "statMul", "maxHealth", L("canrpgmobs:editor-simple-hp", "Health") + " ×", Num("maxHealth", 0.01f, 100), MulPresets, null));
                    rows.Add(PresetStatRow(x, d, "statMul", "meleeDamage", L("canrpgmobs:editor-simple-damage", "Melee damage") + " ×", Num("meleeDamage", 0, 100), MulPresets, null));
                    rows.Add(PresetStatRow(x, d, "statMul", "moveSpeed", L("canrpgmobs:editor-simple-speed", "Move speed") + " ×", Num("moveSpeed", 0, 10), SpeedMulPresets, null));
                    rows.Add(StatRow(x, d, "statMul", "spellPower", L("canrpgmobs:editor-simple-spellPower", "Spell power") + " ×", Num("spellPower", 0.01f, 100), null, "mul"));
                    rows.Add(StatRow(x, d, "statMul", "healingPower", L("canrpgmobs:editor-simple-healingPower", "Healing power") + " ×", Num("healingPower", 0, 100), null, "mul"));
                    break;
            }
        }

        private FormRow StatRow(double x, JObject d, string objectKey, string key, string label, FieldSpec spec, Func<string?>? result, string? helpKey = null) => new(1, (c, y) =>
        {
            Func<string?>? effective = null;
            if (result != null) effective = () => result() is { } value ? ResultText(d, objectKey, key, value) : null;
            SimpleLabel(c, x, y, label, helpKey ?? key, effective);
            if (Inherits(objectKey)) ResetButton(c, x + SimpleLabelW + SimpleControlW - ResetW, y, d, objectKey, key);
            NumberInput(c, B(x + SimpleLabelW, y, 120), ReadNumber((d[objectKey] as JObject)?[key]), v =>
            {
                if (d[objectKey] is not JObject target)
                {
                    if (v.Trim().Length == 0) return;
                    d[objectKey] = target = new JObject();
                }
                SetNumber(target, key, v, spec);
                RemoveIfEmpty(d, objectKey);
            });
        });

        // a mob's own stats override what race and role give it
        private bool Inherits(string objectKey) => session.Section == "mobs" && objectKey == "stats";

        private static bool HasOwn(JObject d, string objectKey, string key) => (d[objectKey] as JObject)?[key] is { Type: not JTokenType.Null };

        // read while typing, so it follows the input
        private string ResultText(JObject d, string objectKey, string key, string value)
            => Inherits(objectKey) && !HasOwn(d, objectKey, key)
                ? L("canrpgmobs:editor-simple-inherited", "From race and role: {0}", value)
                : L("canrpgmobs:editor-simple-effective", "Result: {0}", value);

        private const double ResetW = 56;

        // only shown when the mob has its own value; undo icon at the right of the ResetW slot
        private void ResetButton(GuiComposer c, double x, double y, JObject d, string objectKey, string key)
        {
            if (!HasOwn(d, objectKey, key)) return;
            c.AddIcon("undo", L("canrpgmobs:editor-tip-reset", "Back to the race's and role's value"), () =>
            {
                (d[objectKey] as JObject)?.Remove(key);
                RemoveIfEmpty(d, objectKey);
                Compose();
                return true;
            }, B(x + ResetW - 26, y, 26));
        }

        // right hand weapon damage at level 1, same as the server (MobGear.Weapon, StatMath)
        private string? WeaponNote(ResolvedMob eff)
        {
            if (eff.MeleeOverridden) return null;
            var weapon = ItemCatalog.Find(capi, eff.Appearance.rightHand);
            if (weapon == null || weapon.AttackPower <= 0.5f) return null;
            return L("canrpgmobs:editor-simple-weapon", "{0}: {1} at level 1", weapon.Name, Display(weapon.AttackPower * eff.MeleeMul + eff.MeleeAdd));
        }

        // extra is a func so "Result: ..." can update while typing
        private void SimpleLabel(GuiComposer compo, double x, double y, string label, string? helpKey, Func<string?>? extra = null)
        {
            compo.AddFitText(label, UiKit.LabelFont(), B(x, y + 4, SimpleLabelW - 6));
            string help = helpKey == null ? "" : FieldHelp(null, helpKey);
            var bounds = B(x + SimpleLabelW + SimpleControlW + 10, y, PanelW - SimpleLabelW - SimpleControlW - 10, RowStep);

            string Text()
            {
                string? result = extra?.Invoke();
                return result != null && help.Length > 0 ? result + "  ·  " + help : result ?? help;
            }

            if (extra == null)
            {
                if (help.Length > 0) compo.AddStaticText(help, HelpFont(), bounds);
                return;
            }
            // results read as values, like the claims cards
            LiveText(compo, bounds, UiKit.SmallValueFont(), Text);
        }

        // open: an arrow jumps to the picked entry; inherited: what an empty value falls back to, it gets a reset when set
        private FormRow PickRow(double x, JObject d, string key, string list, string section, string label, bool open = false, string? inherited = null) => new(1, (c, y) =>
        {
            SimpleLabel(c, x, y, label, key);
            string value = ReadString(d, key);
            string text = value.Length > 0 ? NameOf(section, value)
                : inherited != null ? L("canrpgmobs:editor-simple-from-role", "From role: {0}", NameOf(section, inherited))
                : L("canrpgmobs:editor-simple-choose", "Choose...");
            bool reset = inherited != null && value.Length > 0;
            bool jump = open && value.Length > 0;
            double extra = reset || jump ? 30 : 0;
            double cx = x + SimpleLabelW;
            c.AddFitButton(text, () => OpenPick(list, label, null, v => d[key] = v), B(cx, y, SimpleControlW - extra), EnumButtonStyle.Small);
            if (reset)
                c.AddIcon("undo", L("canrpgmobs:editor-tip-reset-role", "Back to the role's value"), () => { d.Remove(key); Compose(); return true; },
                    B(cx + SimpleControlW - 26, y, 26));
            else if (jump)
                c.AddIcon("right", L("canrpgmobs:editor-tip-open", "Open {0}", NameOf(section, value)), () => { ShowEntry(section, Q(value)); return true; },
                    B(cx + SimpleControlW - 26, y, 26));
        });

        private FormRow ChoiceRow(double x, JObject d, string key, string label, string[] values, string[] names, string? emptyName) => new(1, (c, y) =>
        {
            SimpleLabel(c, x, y, label, key);
            var allValues = emptyName != null ? values.Prepend("").ToArray() : values;
            var allNames = emptyName != null ? names.Prepend(emptyName).ToArray() : names;
            string current = ReadString(d, key);
            int selected = Math.Max(0, Array.FindIndex(allValues, v => v.Length == 0 ? current.Length == 0 : current.Length > 0 && Same(v, current)));
            c.AddDropDown(allValues, allNames, selected, (code, _) =>
            {
                if (code.Length == 0) d.Remove(key);
                else d[key] = code;
                Compose();
            }, B(x + SimpleLabelW, y, SimpleControlW), NextKey());
        });

        private FormRow TextRow(double x, string text, bool problem)
            => new(1, (c, y) => c.AddFitText(text, problem ? EditorStyle.Status(true) : EditorStyle.Body(), B(x, y + 4, PanelW, 24)));

        private static CairoFont HelpFont() => CairoFont.WhiteDetailText().WithFontSize(11f).WithColor(EditorStyle.Muted);

        private string SimpleTitle(JObject draft)
        {
            string id = Short(session.DraftId);
            var name = draft["name"] as JObject;
            string text = "";
            if (name?["langKey"] is JValue langKey && Lang.HasTranslation(langKey.ToString(), false, false)) text = Lang.GetUnformatted(langKey.ToString());
            else text = name?[Lang.CurrentLocale]?.ToString() ?? name?["en"]?.ToString() ?? "";
            return text.Length > 0 ? $"{text}  ({id})" : id;
        }

        private string NameOf(string section, string id)
        {
            string qualified = Q(id);
            var info = Section(section);
            var model = info?.Find(mod.Catalog.Data, qualified);
            // AI profiles have no name, show the archetype
            string? name = model == null ? null : (info!.Name?.Invoke(model) ?? (model as AiProfileModel)?.archetype);
            return string.IsNullOrEmpty(name) ? Short(qualified) : $"{name} ({Short(qualified)})";
        }

        private string[] FactionIds() => FactionMatrix.WithBuiltIns(mod.Catalog.Data.Factions.Keys);

        // races need an explicit kind, otherwise the loader falls back to a plm seraph
        private JObject NewAppearance(JObject d)
            => session.Section == "races"
                ? new JObject { ["kind"] = MobBodies.AppearanceKind(ReadString(d, "body")) }
                : new JObject();

        // drop an empty appearance override on mobs
        private void CleanAppearance(JObject d)
        {
            if (session.Section == "mobs" && d["appearance"] is JObject { Count: 0 }) d.Remove("appearance");
        }
    }
}
