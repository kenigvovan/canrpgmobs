using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Vintagestory.API.Client;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // the wizard's steps
    public partial class MobEditorDialog
    {
        private const double WizardLabelW = 170;
        private const int WizardCardRows = 4;

        private static readonly WizardStep[] WizardSteps = (WizardStep[])Enum.GetValues(typeof(WizardStep));
        private int wizardRacePage;

        private void AddWizardPage(GuiComposer compo, double x, double y)
        {
            var w = wizard ??= NewWizard();
            PanelInset(compo, x, y, -4)
                .AddStaticText(L("canrpgmobs:wiz-title", "New mob"), UiKit.SectionFont(), B(x, y, PanelW, 24));

            double stepW = (PanelW - 5 * 4) / 6;
            for (int i = 0; i < WizardSteps.Length; i++)
            {
                var step = WizardSteps[i];
                string text = $"{i + 1} {L("canrpgmobs:wiz-step-" + step.ToString().ToLowerInvariant(), step.ToString())}";
                compo.AddSmallButton((step == w.Step ? "> " : "") + text, () =>
                {
                    if (step > w.Reached) return true;
                    w.Step = step;
                    Compose();
                    return true;
                }, B(x + i * (stepW + 4), y + 28, stepW), EnumButtonStyle.Small);
            }

            double body = y + 62;
            switch (w.Step)
            {
                case WizardStep.Who: WizardWho(compo, x, body, w); break;
                case WizardStep.Fight: WizardFight(compo, x, body, w); break;
                case WizardStep.Strength: WizardStrength(compo, x, body, w); break;
                case WizardStep.Gear: WizardGear(compo, x, body, w); break;
                case WizardStep.Loot: WizardLoot(compo, x, body, w); break;
                default: WizardDone(compo, x, body, w); break;
            }

            double nav = y + BodyH - 30;
            double bw = 140;
            if (w.Step > WizardStep.Who)
                compo.AddSmallButton(L("canrpgmobs:wiz-back", "Back"), () => { w.Step--; Compose(); return true; }, B(x, nav, bw), EnumButtonStyle.Small);
            compo.AddSmallButton(L("canrpgmobs:wizard-cancel", "Start over"), () =>
            {
                wizard = NewWizard();
                wizardVersion++;
                SetStatus("", false);
                Compose();
                return true;
            }, B(x + bw + 6, nav, bw), EnumButtonStyle.Small);
            if (w.Step < WizardStep.Done)
                compo.AddSmallButton(L("canrpgmobs:wiz-next", "Next"), () => WizardNext(w), B(x + PanelW - bw, nav, bw), EnumButtonStyle.Small);
        }

        private bool WizardNext(WizardDraft w)
        {
            if (w.Step == WizardStep.Who && w.RaceId == null)
            {
                if (w.RaceNameEn.Length == 0) return WizardStay(L("canrpgmobs:wiz-need-race-name", "Name the new race on the first step."));
                if (w.Body == MobBodies.Creature && w.Shape.Length == 0) return WizardStay(L("canrpgmobs:wizard-need-shape", "A creature needs a shape."));
            }
            w.Step++;
            if (w.Step > w.Reached) w.Reached = w.Step;
            SetStatus("", false);
            Compose();
            return true;
        }

        private bool WizardStay(string message)
        {
            SetStatus(message, true);
            Compose();
            return true;
        }

        private void WizardWho(GuiComposer compo, double x, double y, WizardDraft w)
        {
            compo.AddStaticText(L("canrpgmobs:wiz-who-help", "Who is it? Pick a people that already exists, or make a new one."), EditorStyle.FieldLabel(), B(x, y, PanelW, 22));
            y += 26;
            var races = mod.Catalog.Races.Keys.OrderBy(k => NameOf("races", k), StringComparer.CurrentCulture).ToList();
            int perPage = WizardCardRows * 2;
            int pages = Math.Max(1, (races.Count + perPage - 1) / perPage);
            wizardRacePage = Math.Clamp(wizardRacePage, 0, pages - 1);
            double cardW = (PanelW - 6) / 2;
            int index = 0;
            foreach (var raceId in races.Skip(wizardRacePage * perPage).Take(perPage))
            {
                var race = mod.Catalog.Races[raceId];
                string id = raceId;
                string text = $"{Named(race.name) ?? Short(raceId)} · {L("canrpgmobs:wizard-body-" + race.body, race.body)} · {L("canrpgmobs:wiz-mobs-count", "{0} mobs", MobsUsingRace(raceId))}";
                compo.AddFitButton((w.RaceId == id ? "> " : "") + text, () =>
                {
                    w.RaceId = id;
                    WizardBodyChanged(w);
                    Compose();
                    return true;
                }, B(x + index % 2 * (cardW + 6), y + index / 2 * RowStep, cardW), EnumButtonStyle.Small);
                index++;
            }
            y += WizardCardRows * RowStep;
            if (pages > 1) Pager(compo, x, y, wizardRacePage, pages, p => wizardRacePage = p);
            compo.AddSmallButton((w.RaceId == null ? "> " : "") + L("canrpgmobs:wiz-new-race", "+ New race"), () =>
            {
                w.RaceId = null;
                WizardBodyChanged(w);
                Compose();
                return true;
            }, B(x + PanelW - cardW, y, cardW), EnumButtonStyle.Small);
            y += RowStep + 6;
            if (w.RaceId != null) return;

            double cw = PanelW - WizardLabelW;
            WizardLabel(compo, x, y, L("canrpgmobs:wiz-race-name", "Race name"));
            TextInput(compo, B(x + WizardLabelW, y, cw), w.RaceNameEn, v => { w.RaceNameEn = v.Trim(); wizardVersion++; });
            y += RowStep;
            WizardLabel(compo, x, y, L("canrpgmobs:wiz-body", "Body"));
            var bodies = MobBodies.All.Keys.ToArray();
            compo.AddDropDown(bodies, bodies.Select(b => L("canrpgmobs:wizard-body-" + b, b)).ToArray(), Math.Max(0, Array.IndexOf(bodies, w.Body)), (code, _) =>
            {
                w.Body = code;
                WizardBodyChanged(w);
                Compose();
            }, B(x + WizardLabelW, y, 200), NextKey());
            y += RowStep;
            if (w.Body == MobBodies.Creature)
            {
                WizardChoose(compo, x, y, L("canrpgmobs:wizard-shape", "Shape"), w.Shape, "shapes", v => w.Shape = v);
                y += RowStep;
                WizardChoose(compo, x, y, L("canrpgmobs:wizard-texture", "Texture (optional)"), w.Texture, "textures", v => w.Texture = v);
            }
            else WizardChoose(compo, x, y, L("canrpgmobs:wizard-model", "Model"), w.Model, "plmModels", v => w.Model = v);
            y += RowStep;
            WizardLabel(compo, x, y, L("canrpgmobs:wizard-faction", "Faction"));
            var factions = FactionIds();
            compo.AddDropDown(factions, factions.Select(f => NameOf("factions", f)).ToArray(), Math.Max(0, Array.IndexOf(factions, w.Faction)),
                (code, _) => { w.Faction = code; wizardVersion++; }, B(x + WizardLabelW, y, 260), NextKey());
        }

        // creatures carry nothing
        private void WizardBodyChanged(WizardDraft w)
        {
            if (WizardCreature(w))
            {
                w.RightHand = w.LeftHand = "";
                w.Gear.Clear();
            }
            else if (w.Behavior == "archer" && w.RightHand.Length == 0) w.RightHand = WizardBow;
            wizardVersion++;
        }

        private bool WizardCreature(WizardDraft w)
            => w.RaceId != null && mod.Catalog.Races.TryGetValue(w.RaceId, out var race) ? race.body == MobBodies.Creature : w.Body == MobBodies.Creature;

        private void WizardFight(GuiComposer compo, double x, double y, WizardDraft w)
        {
            compo.AddStaticText(L("canrpgmobs:wiz-fight-help", "How does it fight?"), EditorStyle.FieldLabel(), B(x, y, PanelW, 22));
            y += 26;
            double cardW = (PanelW - 6) / 2;
            for (int i = 0; i < WizardRoles.Length; i++)
            {
                string code = WizardRoles[i].Code;
                compo.AddSmallButton((w.Behavior == code ? "> " : "") + L("canrpgmobs:wiz-name-" + code, code), () =>
                {
                    SetBehavior(w, code);
                    Compose();
                    return true;
                }, B(x + i % 2 * (cardW + 6), y + i / 2 * RowStep, cardW), EnumButtonStyle.Small);
            }
            y += (WizardRoles.Length + 1) / 2 * RowStep + 4;
            compo.AddStaticText(L("canrpgmobs:wiz-behavior-" + w.Behavior, ""), EditorStyle.Body(), B(x, y, PanelW, 3 * RowStep));
            y += 3 * RowStep;

            var role = BehaviorOf(w);
            var existing = RolesForArchetype(role.Archetype);
            var values = existing.Append("").ToArray();
            var names = existing.Select(r => NameOf("roles", r))
                .Append(L("canrpgmobs:wiz-role-new", "New shared role: {0}", L("canrpgmobs:wiz-name-" + role.Code, role.Code))).ToArray();
            WizardLabel(compo, x, y, L("canrpgmobs:wiz-role", "Role"));
            compo.AddDropDown(values, names, Math.Max(0, Array.IndexOf(values, w.RoleId ?? "")), (code, _) =>
            {
                w.RoleId = code.Length == 0 ? null : code;
                wizardVersion++;
            }, B(x + WizardLabelW, y, PanelW - WizardLabelW), NextKey());
            y += RowStep;
            compo.AddStaticText(L("canrpgmobs:wiz-role-help", "A role is shared by every mob that fights this way, so the next archer reuses it."), HelpFont(), B(x, y + 2, PanelW, 22));
        }

        private void WizardStrength(GuiComposer compo, double x, double y, WizardDraft w)
        {
            double cx = x + WizardLabelW;
            WizardLabel(compo, x, y, L("canrpgmobs:wiz-levels", "Levels (from / to)"));
            NumberInput(compo, B(cx, y, 70), w.LevelMin.ToString(CultureInfo.InvariantCulture), v => { if (int.TryParse(v, out int n)) w.LevelMin = n; wizardVersion++; });
            NumberInput(compo, B(cx + 76, y, 70), w.LevelMax.ToString(CultureInfo.InvariantCulture), v => { if (int.TryParse(v, out int n)) w.LevelMax = n; wizardVersion++; });
            y += RowStep + 4;

            WizardLabel(compo, x, y, L("canrpgmobs:wiz-difficulty", "How strong"));
            WizardChoices(compo, cx, y, WizardDifficulties.Select(d => d.Code).ToArray(), w.Difficulty, "wiz-difficulty-", code =>
            {
                w.Difficulty = code;
                w.StatsTouched = false;
                WizardStats(w);
            });
            y += RowStep;
            WizardLabel(compo, x, y, L("canrpgmobs:wiz-pace", "How fast"));
            WizardChoices(compo, cx, y, WizardPaces.Select(p => p.Code).ToArray(), w.Pace, "wiz-pace-", code =>
            {
                w.Pace = code;
                w.StatsTouched = false;
                WizardStats(w);
            });
            y += RowStep + 4;

            compo.AddSmallButton(L(w.Exact ? "canrpgmobs:wiz-exact-hide" : "canrpgmobs:wiz-exact-show", w.Exact ? "Hide exact numbers" : "Exact numbers..."), () =>
            {
                w.Exact = !w.Exact;
                Compose();
                return true;
            }, B(x, y, 200), EnumButtonStyle.Small);
            y += RowStep;
            if (!w.Exact)
            {
                compo.AddStaticText(L("canrpgmobs:wiz-strength-summary", "Health {0}, damage {1} at level 1, they grow with the level.", Display(w.Health), Display(w.Damage)),
                    EditorStyle.Body(), B(x, y + 4, PanelW, 22));
                return;
            }
            WizardNumber(compo, x, y, L("canrpgmobs:wiz-health", "Health"), w.Health, v => w.Health = v, w);
            y += RowStep;
            WizardNumber(compo, x, y, L("canrpgmobs:wiz-damage", "Melee damage"), w.Damage, v => w.Damage = v, w);
            y += RowStep;
            WizardNumber(compo, x, y, L("canrpgmobs:wiz-speed", "Move speed (0.03 = player walking)"), w.Speed, v => w.Speed = v, w);
        }

        private void WizardNumber(GuiComposer compo, double x, double y, string label, float value, Action<float> set, WizardDraft w)
        {
            WizardLabel(compo, x, y, label);
            NumberInput(compo, B(x + WizardLabelW, y, 100), Num(value), v =>
            {
                if (!TryParse(v, out float f)) return;
                set(f);
                w.StatsTouched = true;
                wizardVersion++;
            });
        }

        // presets as buttons, the chosen one marked
        private void WizardChoices(GuiComposer compo, double x, double y, string[] codes, string current, string langPrefix, Action<string> pick)
        {
            double each = Math.Min(104, (PanelW - WizardLabelW - (codes.Length - 1) * 4) / codes.Length);
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                compo.AddSmallButton((code == current ? "> " : "") + L("canrpgmobs:" + langPrefix + code, code), () =>
                {
                    pick(code);
                    wizardVersion++;
                    Compose();
                    return true;
                }, B(x + i * (each + 4), y, each), EnumButtonStyle.Small);
            }
        }

        private void WizardGear(GuiComposer compo, double x, double y, WizardDraft w)
        {
            if (WizardCreature(w))
                compo.AddStaticText(L("canrpgmobs:wiz-gear-creature", "Creatures carry nothing, only skills."), EditorStyle.FieldLabel(), B(x, y, PanelW, 22));
            else
            {
                WizardHand(compo, x, y, L("canrpgmobs:wiz-right-hand", "Right hand"), w.RightHand, v => w.RightHand = v);
                WizardHand(compo, x + PanelW / 2, y, L("canrpgmobs:wiz-left-hand", "Left hand"), w.LeftHand, v => w.LeftHand = v);
                y += 40;
                WizardLabel(compo, x, y + 4, L("canrpgmobs:wiz-worn", "Worn"));
                double cx = x + WizardLabelW;
                foreach (var code in w.Gear.ToList())
                {
                    string item = code;
                    ItemChip(compo, cx, y, item, ItemName(item), () => { w.Gear.Remove(item); wizardVersion++; });
                    cx += 56;
                    if (cx > x + PanelW - 90) break;
                }
                compo.AddIcon("plus", L("canrpgmobs:editor-tip-add", "Add"), () => OpenItemPick(L("canrpgmobs:wiz-worn", "Worn"), ItemCategoriesFor("items:wearable"), code =>
                {
                    if (code != null && !w.Gear.Contains(code)) w.Gear.Add(code);
                    wizardVersion++;
                }, false), B(cx, y + 4, 30));
                y += 40;
                if (w.Behavior == "archer" && w.RightHand.Length == 0)
                {
                    compo.AddStaticText(L("canrpgmobs:wiz-archer-needs-bow", "An archer shoots only with a bow, crossbow, sling or spear in the right hand."), EditorStyle.Status(true),
                        B(x, y, PanelW, 22));
                    y += 24;
                }
            }

            y += 8;
            compo.AddStaticText(L("canrpgmobs:wiz-skills", "Skills (optional)"), UiKit.SectionFont(), B(x, y, PanelW, 22));
            y += 26;
            // an existing race or role brings its own skills
            var data = mod.Catalog.Data;
            var inherited = (w.RaceId != null && data.Races.TryGetValue(w.RaceId, out var wr) ? wr.skills ?? new List<SkillRuleModel>() : new List<SkillRuleModel>())
                .Concat(w.RoleId != null && data.Roles.TryGetValue(w.RoleId, out var wro) ? wro.skills ?? new List<SkillRuleModel>() : new List<SkillRuleModel>())
                .Where(r => r != null).ToList();
            if (inherited.Count > 0)
            {
                string names = string.Join(", ", inherited.Select(r => r.action != null ? r.action.type : SpellDisplayName(r.spell)));
                WizardLabel(compo, x, y, L("canrpgmobs:wiz-inherit-skills", "Race and role skills"));
                compo.AddSmallButton(w.InheritSkills ? L("canrpgmobs:wiz-inherit-on", "Kept ({0})", inherited.Count) : L("canrpgmobs:wiz-inherit-off", "Off: only the spells below"),
                    () => { w.InheritSkills = !w.InheritSkills; wizardVersion++; Compose(); return true; },
                    B(x + WizardLabelW, y, PanelW - WizardLabelW), EnumButtonStyle.Small);
                y += RowStep;
                compo.AddFitText(names, HelpFont(), B(x + WizardLabelW, y, PanelW - WizardLabelW, 16));
                y += 20;
            }
            var spells = PickLists.Get("spells", capi, mod, session, null);
            for (int i = 0; i < w.Spells.Length; i++)
            {
                int slot = i;
                string current = w.Spells[slot];
                string label = current.Length == 0 ? L("canrpgmobs:editor-simple-choose", "Choose...") : spells.FirstOrDefault(o => o.Value == current)?.Label ?? current;
                WizardLabel(compo, x, y, L("canrpgmobs:wizard-spell", "Spell {0} (optional)", i + 1));
                compo.AddFitButton(label, () => OpenPick("spells", L("canrpgmobs:wizard-spell", "Spell {0} (optional)", slot + 1), null, v =>
                {
                    w.Spells[slot] = v;
                    wizardVersion++;
                }), B(x + WizardLabelW, y, PanelW - WizardLabelW - 40), EnumButtonStyle.Small);
                if (current.Length > 0) WizardRemove(compo, x, y, () => w.Spells[slot] = "");
                y += RowStep;
            }
        }

        // RemoveButton that also tells the preview the wizard changed
        private void WizardRemove(GuiComposer compo, double x, double y, Action remove) => RemoveButton(compo, x, y, () =>
        {
            remove();
            wizardVersion++;
        });

        private void WizardHand(GuiComposer compo, double x, double y, string label, string code, Action<string> set)
        {
            compo.AddStaticText(label, EditorStyle.FieldLabel(), B(x, y + 8, 100));
            if (code.Length > 0) ItemChip(compo, x + 104, y, code, ItemName(code), () => { set(""); wizardVersion++; });
            compo.AddSmallButton(L("canrpgmobs:editor-simple-choose", "Choose..."), () => OpenItemPick(label, ItemCategoriesFor("items:hand"), picked =>
            {
                set(picked ?? "");
                wizardVersion++;
            }, true), B(x + 164, y + 4, 110), EnumButtonStyle.Small);
        }

        private void WizardLoot(GuiComposer compo, double x, double y, WizardDraft w)
        {
            compo.AddStaticText(L("canrpgmobs:wiz-loot-help", "What it drops. You can skip this step."), EditorStyle.FieldLabel(), B(x, y, PanelW, 22));
            y += 26;
            compo.AddStaticText(L("canrpgmobs:wiz-loot-quick", "Items, each with its own chance"), UiKit.SectionFont(), B(x, y, PanelW, 22));
            y += 26;
            for (int i = 0; i < w.QuickLoot.Count; i++)
            {
                int index = i;
                var entry = w.QuickLoot[i];
                ItemChip(compo, x, y, entry.Code, ItemName(entry.Code), null);
                compo.AddFitText(ItemName(entry.Code), EditorStyle.Body(), B(x + 36, y + 6, 240));
                NumberInput(compo, B(x + 290, y + 2, 60), entry.Chance.ToString(CultureInfo.InvariantCulture), v =>
                {
                    if (!int.TryParse(v, out int chance)) return;
                    w.QuickLoot[index] = (entry.Code, entry.Block, Math.Clamp(chance, 1, 100));
                    wizardVersion++;
                });
                compo.AddStaticText("%", EditorStyle.Body(), B(x + 354, y + 6, 20));
                WizardRemove(compo, x, y + 2, () => w.QuickLoot.RemoveAt(index));
                y += 34;
            }
            if (w.QuickLoot.Count < 4)
            {
                compo.AddSmallButton(L("canrpgmobs:wiz-loot-add-item", "+ Item"), () => OpenItemPick(L("canrpgmobs:wiz-loot-quick", "Items, each with its own chance"),
                    new[] { "all", "block" }, code =>
                    {
                        if (code == null) return;
                        w.QuickLoot.Add((code, ItemCatalog.Find(capi, code)?.IsBlock == true, 50));
                        wizardVersion++;
                    }, false), B(x, y, 140), EnumButtonStyle.Small);
                y += RowStep;
            }

            y += 8;
            compo.AddStaticText(L("canrpgmobs:wiz-loot-tables", "Existing loot tables"), UiKit.SectionFont(), B(x, y, PanelW, 22));
            y += 26;
            foreach (var table in w.Loot.ToList())
            {
                string id = table;
                compo.AddStaticText(Short(id), EditorStyle.Body(), B(x, y + 4, PanelW - 50));
                WizardRemove(compo, x, y, () => w.Loot.Remove(id));
                y += RowStep;
            }
            compo.AddSmallButton(L("canrpgmobs:wiz-loot-add-table", "+ Table"), () => OpenPick("lootTables", L("canrpgmobs:wiz-loot-tables", "Existing loot tables"), null, v =>
            {
                if (!w.Loot.Contains(v)) w.Loot.Add(v);
                wizardVersion++;
            }), B(x, y, 140), EnumButtonStyle.Small);
        }

        private void WizardDone(GuiComposer compo, double x, double y, WizardDraft w)
        {
            double cw = PanelW - WizardLabelW;
            WizardLabel(compo, x, y, L("canrpgmobs:wizard-name", "Name"));
            TextInput(compo, B(x + WizardLabelW, y, cw), w.NameEn, v => { w.NameEn = v.Trim(); wizardVersion++; });
            y += RowStep;
            compo.AddSmallButton(L(w.ShowId ? "canrpgmobs:wiz-details-hide" : "canrpgmobs:wiz-details-show", w.ShowId ? "Hide details" : "Details..."), () =>
            {
                w.ShowId = !w.ShowId;
                Compose();
                return true;
            }, B(x, y, 160), EnumButtonStyle.Small);
            if (w.ShowId)
                TextInput(compo, B(x + WizardLabelW, y, cw), w.IdTouched ? w.Id : "", v =>
                {
                    w.Id = v.Trim();
                    w.IdTouched = w.Id.Length > 0;
                    wizardVersion++;
                });
            y += RowStep + 6;

            WizardLabel(compo, x, y, L("canrpgmobs:wiz-zone", "Also spawn in zone"));
            var zones = mod.Catalog.Zones.Keys.OrderBy(k => k).ToArray();
            var zoneValues = zones.Prepend("").ToArray();
            var zoneNames = zones.Select(z => NameOf("zones", z)).Prepend(L("canrpgmobs:wiz-zone-none", "(no)")).ToArray();
            compo.AddDropDown(zoneValues, zoneNames, Math.Max(0, Array.IndexOf(zoneValues, w.Zone)), (code, _) => w.Zone = code, B(x + WizardLabelW, y, cw), NextKey());
            y += RowStep + 8;

            foreach (var line in WizardSummary(w))
            {
                compo.AddStaticText(line, EditorStyle.Body(), B(x, y, PanelW, 22));
                y += 24;
            }

            double bw = (PanelW - 12) / 3;
            double by = y + 10;
            compo.AddSmallButton(L("canrpgmobs:wizard-check", "Check"), () => { ClearPending(); CheckWizard(out _, out _); Compose(); return true; }, B(x, by, bw), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:wiz-create", "Create"), () => OnWizardCreate(false), B(x + bw + 6, by, bw), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:wiz-create-show", "Create and show"), () => OnWizardCreate(true), B(x + (bw + 6) * 2, by, bw), EnumButtonStyle.Small);
        }

        // what will be made and what is reused
        private List<string> WizardSummary(WizardDraft w)
        {
            var lines = new List<string>();
            string mobName = w.NameEn.Length > 0 ? w.NameEn : "?";
            lines.Add(L("canrpgmobs:wiz-sum-mob", "New mob: {0}, levels {1}-{2}", mobName, Math.Min(w.LevelMin, w.LevelMax), Math.Max(w.LevelMin, w.LevelMax)));
            lines.Add(w.RaceId != null
                ? L("canrpgmobs:wiz-sum-race-old", "Race: {0} (exists)", NameOf("races", w.RaceId))
                : L("canrpgmobs:wiz-sum-race-new", "Race: {0} (new)", RaceNameFor(w)));
            var role = BehaviorOf(w);
            lines.Add(w.RoleId != null
                ? L("canrpgmobs:wiz-sum-role-old", "Role: {0} (exists)", NameOf("roles", w.RoleId))
                : L("canrpgmobs:wiz-sum-role-new", "Role: {0} (new, shared)", L("canrpgmobs:wiz-name-" + role.Code, role.Code)));
            int skills = w.Spells.Count(s => s.Length > 0);
            lines.Add(L("canrpgmobs:wiz-sum-rest", "Health {0}, damage {1}, skills {2}, loot {3}", Display(w.Health), Display(w.Damage), skills, w.Loot.Count + (w.QuickLoot.Count > 0 ? 1 : 0)));
            return lines;
        }

        private void WizardLabel(GuiComposer compo, double x, double y, string text)
            => compo.AddFitText(text, UiKit.LabelFont(), B(x, y + 4, WizardLabelW - 6));

        // picker button showing the current value
        private void WizardChoose(GuiComposer compo, double x, double y, string label, string value, string list, Action<string> set)
        {
            WizardLabel(compo, x, y, label);
            compo.AddFitButton(value.Length > 0 ? value : L("canrpgmobs:editor-simple-choose", "Choose..."), () => OpenPick(list, label, null, v =>
            {
                set(v);
                wizardVersion++;
            }), B(x + WizardLabelW, y, PanelW - WizardLabelW), EnumButtonStyle.Small);
        }
    }
}
