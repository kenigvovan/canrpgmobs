using System.Collections.Generic;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Appearance;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Gear tab. A mob inherits race/role gear until the first change, which copies the worn list into the mob.
    public partial class MobEditorDialog
    {
        private const double GearRowH = 32;
        private const double ChipW = 54;

        private void AddSimpleGear(GuiComposer compo, double x, double y, JObject d, ResolvedMob? eff)
        {
            string section = session.Section;
            string body = section == "races" ? ReadString(d, "body") : eff?.Race.body ?? MobBodies.Humanoid;
            if (section != "roles" && MobBodies.AppearanceKind(body) == AppearanceKinds.Shape)
            {
                compo.AddStaticText(L("canrpgmobs:editor-gear-creature", "Creatures (shape bodies) don't show gear."), EditorStyle.Body(), B(x, y + 4, PanelW, 40));
                return;
            }

            var target = GearTarget(d, false);
            var ownWorn = (target?["gear"] as JArray)?.Select(t => t.ToString()).ToList();
            bool mob = section == "mobs";
            bool inherited = mob && ownWorn == null;
            // show what is actually worn, "none" entries already applied
            var worn = ownWorn ?? (mob ? MobGear.Effective(eff?.Appearance.gear) : null) ?? new List<string>();

            double row = y;
            string header = section switch
            {
                "mobs" => inherited
                    ? L("canrpgmobs:editor-gear-inherited", "Gear of race {0} and role {1}. A change makes it this mob's own.", NameOf("races", ReadString(d, "race")), NameOf("roles", ReadString(d, "role")))
                    : L("canrpgmobs:editor-gear-own", "This mob has its own gear."),
                "races" => L("canrpgmobs:editor-gear-race", "Worn by every mob of this race; roles add theirs."),
                _ => L("canrpgmobs:editor-gear-role", "Added to every race with this role.")
            };
            bool canReset = mob && (!inherited || target?["rightHand"] != null || target?["leftHand"] != null);
            compo.AddFitText(header, UiKit.LabelFont(), B(x, row + 4, PanelW - (canReset ? 210 : 0), 24));
            if (canReset)
                compo.AddSmallButton(L("canrpgmobs:editor-inherit-reset", "Back to race and role"), () => { ResetGear(d); return true; }, B(x + PanelW - 204, row, 200), EnumButtonStyle.Small);
            row += RowStep;

            var presetWithGear = section == "roles" ? null : eff?.Appearance.presets?.FirstOrDefault(p => p.gear != null || p.rightHand != null || p.leftHand != null);
            if (presetWithGear != null)
            {
                compo.AddFitText(L("canrpgmobs:editor-gear-preset", "Look variant {0} has its own gear: changes here don't apply to it.", presetWithGear.id),
                        EditorStyle.Status(true), B(x, row + 4, PanelW - 210, 24))
                    .AddSmallButton(L("canrpgmobs:editor-gear-preset-fix", "Use this gear for all variants"), () => { StripPresetGear(d, eff); return true; },
                        B(x + PanelW - 204, row, 200), EnumButtonStyle.Small);
                row += RowStep;
            }

            var bySlot = worn.GroupBy(code => ItemCatalog.Find(capi, code)?.GearSlot ?? "other").ToDictionary(g => g.Key, g => g.ToList());
            var slots = bySlot.ContainsKey("other") ? ItemCatalog.GearSlots.Append("other") : ItemCatalog.GearSlots;
            foreach (var slot in slots)
            {
                string slotCode = slot;
                string slotName = L("canrpgmobs:editor-slot-" + slot, slot);
                compo.AddStaticText(slotName, EditorStyle.FieldLabel(), B(x, row + 8, 108));
                double cx = x + 110;
                foreach (var code in bySlot.GetValueOrDefault(slot) ?? new List<string>())
                {
                    if (cx + ChipW > x + PanelW - 44) break;
                    string itemCode = code;
                    ItemChip(compo, cx, row, itemCode, ChipHover(itemCode, inherited, eff), () =>
                    {
                        var list = worn.ToList();
                        list.Remove(itemCode);
                        WriteWorn(d, list);
                    });
                    cx += ChipW;
                }
                if (slot != "other")
                    compo.AddIcon("plus", L("canrpgmobs:editor-tip-add", "Add"), () => OpenItemPick(slotName, new[] { "slot:" + slotCode, "wearable", "all" }, v =>
                    {
                        if (v == null) return;
                        var list = worn.ToList();
                        if (!list.Contains(v)) list.Add(v);
                        WriteWorn(d, list);
                    }, false), B(x + PanelW - 30, row + 3, 26));
                row += GearRowH;
            }

            HandRow(compo, x, row, d, eff, "rightHand", L("canrpgmobs:editor-gear-right", "Right hand"));
            row += GearRowH;
            HandRow(compo, x, row, d, eff, "leftHand", L("canrpgmobs:editor-gear-left", "Left hand"));
            row += GearRowH;
            if (row < y + BodyH - 90)
                compo.AddStaticText(L("canrpgmobs:editor-gear-weaponnote", "A weapon in the right hand sets the melee damage; archers need a bow. Gear is looks only and never drops."),
                    HelpFont(), B(x, row + 2, PanelW, 30));
        }

        private void HandRow(GuiComposer compo, double x, double y, JObject d, ResolvedMob? eff, string key, string label)
        {
            compo.AddStaticText(label, EditorStyle.FieldLabel(), B(x, y + 8, 108));
            string own = GearTarget(d, false)?[key]?.ToString() ?? "";
            string effective = session.Section == "mobs" ? (key == "rightHand" ? eff?.Appearance.rightHand : eff?.Appearance.leftHand) ?? "" : "";
            // inherited "none" = empty hand
            if (MobGear.IsNone(effective)) effective = "";
            string shown = own.Length > 0 ? own : effective;
            double cx = x + 110;
            if (shown.Length > 0)
            {
                string hover = ItemName(shown) + (own.Length == 0 ? "  (" + HandSource(eff, key) + ")" : "");
                ItemChip(compo, cx, y, shown, hover, own.Length > 0
                    ? () =>
                    {
                        GearTarget(d, true)!.Remove(key);
                        CleanAppearance(d);
                    }
                    : null);
                cx += ChipW;
            }
            string[] categories = key == "leftHand" ? new[] { "shield", "hand", "all" } : new[] { "hand", "all" };
            compo.AddSmallButton(L("canrpgmobs:editor-simple-choose", "Choose..."), () => OpenItemPick(label, categories, v =>
            {
                if (v == null)
                {
                    GearTarget(d, true)!.Remove(key);
                    CleanAppearance(d);
                }
                else GearTarget(d, true)![key] = v;
            }, true), B(cx, y + 3, 140), EnumButtonStyle.Small);
        }

        // roles hold gear directly, races/mobs in appearance
        private JObject? GearTarget(JObject d, bool create)
        {
            if (session.Section == "roles") return d;
            if (d["appearance"] is JObject look) return look;
            if (!create) return null;
            d["appearance"] = look = NewAppearance(d);
            return look;
        }

        // for mobs an empty list means "no gear", so keep it; races/roles just drop the key
        private void WriteWorn(JObject d, List<string> list)
        {
            var target = GearTarget(d, true)!;
            if (list.Count == 0 && session.Section != "mobs") target.Remove("gear");
            else target["gear"] = new JArray(list.Cast<object>().ToArray());
            CleanAppearance(d);
        }

        private void ResetGear(JObject d)
        {
            if (GearTarget(d, false) is { } target)
            {
                target.Remove("gear");
                target.Remove("rightHand");
                target.Remove("leftHand");
            }
            CleanAppearance(d);
            Compose();
        }

        // preset gear overrides the look's (MobGear.Write); copy inherited presets into the mob first
        private void StripPresetGear(JObject d, ResolvedMob? eff)
        {
            var look = GearTarget(d, true)!;
            if (session.Section == "mobs" && look["presets"] is not JArray)
                look["presets"] = JArray.FromObject(eff?.Appearance.presets ?? new List<AppearancePresetModel>(), Serializer);
            if (look["presets"] is JArray presets)
            {
                foreach (var preset in presets.OfType<JObject>())
                {
                    preset.Remove("gear");
                    preset.Remove("rightHand");
                    preset.Remove("leftHand");
                }
            }
            Compose();
        }

        private string ChipHover(string code, bool inherited, ResolvedMob? eff)
        {
            string name = ItemName(code);
            if (!inherited || eff == null) return name;
            bool fromRace = eff.Race.appearance?.gear?.Contains(code) == true;
            return $"{name}  ({FromLabel(fromRace)})";
        }

        private string HandSource(ResolvedMob? eff, string key)
        {
            string? fromRole = key == "rightHand" ? eff?.Role.rightHand : eff?.Role.leftHand;
            return FromLabel(string.IsNullOrEmpty(fromRole));
        }
    }
}
