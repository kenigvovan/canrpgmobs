using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using canrpgclasses;
using canrpgclasses.Client;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Read-only list of spells and mob actions, with who uses them.
    public partial class MobEditorDialog
    {
        private const int RefRows = 12;
        private const int RefUsersShown = 6;

        private bool refSpells = true;
        private string refFilter = "";
        private string refFilterDraft = "";
        private int refPage;
        private string? refSelected;

        private sealed record RefItem(string Id, string Name, string Tag, string Description, string Extra);

        private bool OpenReference()
        {
            refSelected = null;
            refPage = 0;
            return OpenPage(Page.Reference);
        }

        private void AddReferencePage(GuiComposer compo, double x, double y)
        {
            var spells = canrpgclassesModSystem.ClientInstance?.Spells.All;
            int spellCount = spells?.Count ?? 0;
            compo.AddSmallButton((refSpells ? "> " : "") + L("canrpgmobs:editor-ref-spells", "Spells ({0})", spellCount), () => RefTab(true), B(x, y, 140), EnumButtonStyle.Small)
                .AddSmallButton((!refSpells ? "> " : "") + L("canrpgmobs:editor-ref-actions", "Mob actions ({0})", MobsApi.Vocabulary.Actions.Count), () => RefTab(false),
                    B(x + 146, y, 146), EnumButtonStyle.Small);
            SearchRow(compo, x + 300, y, 200, refFilterDraft, v => refFilterDraft = v,
                L("canrpgmobs:editor-itempick-search", "Search"), x + 504, PanelW - 504, () => { refFilter = refFilterDraft.Trim(); refPage = 0; });
            PanelInset(compo, x, y, 30);

            var items = RefItems().Where(i => refFilter.Length == 0 || i.Name.IndexOf(refFilter, StringComparison.OrdinalIgnoreCase) >= 0
                                              || i.Id.IndexOf(refFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            int pages = Math.Max(1, (items.Count + RefRows - 1) / RefRows);
            refPage = Math.Clamp(refPage, 0, pages - 1);

            double row = y + 36;
            foreach (var item in items.Skip(refPage * RefRows).Take(RefRows))
            {
                string id = item.Id;
                string tag = item.Tag.Length > 0 ? item.Tag + " " : "";
                compo.AddFitButton((refSelected == id ? "> " : "") + tag + item.Name, () => { refSelected = id; Compose(); return true; },
                    B(x, row, 290), EnumButtonStyle.Small);
                row += RowStep;
            }
            if (items.Count == 0)
                compo.AddStaticText(spells == null && refSpells ? L("canrpgmobs:editor-ref-no-spells", "The spell system is not loaded.") : L("canrpgmobs:editor-list-empty", "Nothing matches."),
                    HelpFont(), B(x + 4, row + 6, 286));
            Pager(compo, x, y + 36 + RefRows * RowStep + 2, refPage, pages, p => refPage = p, items.Count);

            double rx = x + 300, rw = PanelW - 300;
            var selected = items.FirstOrDefault(i => i.Id == refSelected);
            if (selected == null)
            {
                compo.AddStaticText(L("canrpgmobs:editor-ref-note", "Pick something on the left to read what it does and who uses it. To give it to a mob, open the mob, race or role and use its Skills tab."),
                    EditorStyle.Body(), B(rx, y + 40, rw, 90));
                return;
            }

            compo.AddFitText(selected.Name, UiKit.SectionFont(), B(rx, y + 36, rw, 24))
                .AddStaticText(selected.Id + (selected.Extra.Length > 0 ? "  ·  " + selected.Extra : ""), HelpFont(), B(rx, y + 60, rw, 30))
                .AddStaticText(selected.Description.Length > 0 ? selected.Description : L("canrpgmobs:editor-skill-no-desc", "No description."), EditorStyle.Body(), B(rx, y + 92, rw, 150));

            var users = RefUsers(selected.Id);
            double uy = y + 248;
            compo.AddStaticText(users.Count == 0 ? L("canrpgmobs:editor-ref-unused", "Nobody uses it yet.") : L("canrpgmobs:editor-ref-used-by", "Used by ({0}):", users.Count),
                EditorStyle.FieldLabel(), B(rx, uy, rw, 20));
            uy += 22;
            foreach (var (section, id, label) in users.Take(RefUsersShown))
            {
                compo.AddFitButton(label, () => { ShowEntry(section, id); return true; }, B(rx, uy, rw), EnumButtonStyle.Small);
                uy += RowStep;
            }
            if (users.Count > RefUsersShown)
                compo.AddStaticText(L("canrpgmobs:editor-ref-more", "...and {0} more", users.Count - RefUsersShown), HelpFont(), B(rx, uy + 4, rw, 16));
        }

        private bool RefTab(bool spells)
        {
            refSpells = spells;
            refSelected = null;
            refPage = 0;
            Compose();
            return true;
        }

        private IEnumerable<RefItem> RefItems()
        {
            if (!refSpells)
                return MobsApi.Vocabulary.Actions.Values.Select(a =>
                {
                    string nameKey = $"canrpgmobs:vocab-action-{a.Id}";
                    string helpKey = nameKey + "-help";
                    string name = Lang.HasTranslation(nameKey, false, false) ? Lang.Get(nameKey) : a.Id;
                    string help = Lang.HasTranslation(helpKey, false, false) ? Lang.Get(helpKey) : a.Help;
                    string args = string.Join(", ", a.Params.Select(p => p.Required ? p.Name : p.Name + "?"));
                    return new RefItem(a.Id, name, "", help, args.Length > 0 ? L("canrpgmobs:editor-ref-params", "settings: {0}", args) : "");
                }).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);

            var all = canrpgclassesModSystem.ClientInstance?.Spells.All.Values;
            if (all == null) return Enumerable.Empty<RefItem>();
            return all.Select(s =>
            {
                string extra = PickLists.SpellTag(mod, s.Id) + " " + SpellReasons(s.Id);
                if (s.Range > 0) extra = L("canrpgmobs:editor-ref-range", "range {0}", s.Range.ToString("0.#", CultureInfo.InvariantCulture)) + "  ·  " + extra;
                // strip tooltip markup
                return new RefItem(s.Id, s.DisplayName, PickLists.SpellTag(mod, s.Id), Regex.Replace(s.Description ?? "", "<[^>]+>", ""), extra.Trim());
            }).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        // races, roles and mobs whose skills use this spell or action
        private List<(string section, string id, string label)> RefUsers(string refId)
        {
            var data = mod.Catalog.Data;
            var found = new List<(string, string, string)>();
            // a bare spell id gets the canrpgclasses domain
            static string Norm(string spell) => spell.Contains(':') ? spell : "canrpgclasses:" + spell;
            string wanted = Norm(refId);

            void Scan(string section, string kind, IEnumerable<(string id, List<SkillRuleModel>? skills)> entries)
            {
                foreach (var (id, skills) in entries.OrderBy(e => e.id, StringComparer.Ordinal))
                {
                    var rule = skills?.FirstOrDefault(r => r != null && (refSpells ? r.action == null && !string.IsNullOrEmpty(r.spell) && Norm(r.spell) == wanted : r.action?.type == refId));
                    if (rule != null) found.Add((section, id, $"{kind}: {ShortName(section, id)}  ({rule.id})"));
                }
            }

            Scan("races", L("canrpgmobs:editor-ref-kind-race", "Race"), data.Races.Select(p => (p.Key, p.Value.skills)));
            Scan("roles", L("canrpgmobs:editor-ref-kind-role", "Role"), data.Roles.Select(p => (p.Key, p.Value.skills)));
            Scan("mobs", L("canrpgmobs:editor-ref-kind-mob", "Mob"), data.Templates.Select(p => (p.Key, p.Value.skills)));
            return found;
        }
    }
}
