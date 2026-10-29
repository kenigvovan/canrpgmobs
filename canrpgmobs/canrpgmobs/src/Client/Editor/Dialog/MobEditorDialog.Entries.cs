using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // left column: section dropdown and entry list
    public partial class MobEditorDialog
    {
        private const double ListTop = RowStep * 3;
        private const double ListBottom = RowStep * 2 + 4 + 13 * (RowH + 2) + 4;
        private static readonly string[] SortModes = { "name", "level", "race", "edited" };

        // mobs only: "" = any
        private string raceFilter = "";
        private string roleFilter = "";
        private string sortMode = "name";

        private bool MobFilters => session.Section == "mobs";
        private double EntriesTop => MobFilters ? ListTop + 2 * RowStep : ListTop;
        // second line with details where the section has them
        private bool TwoLineRows => session.Section is "mobs" or "zones" or "groups" or "races" or "roles";
        private double EntryH => TwoLineRows ? 36 : RowH;
        private int ListRows => (int)((ListBottom - 4 - EntriesTop) / (EntryH + 2));

        private void AddEntryList(GuiComposer compo, double x, double y)
        {
            // icon tabs like the claims window, art from canrpgclasses, the name on hover
            const double tabGap = 4;
            double tabW = (ListW - tabGap * (MainSections.Length - 1)) / MainSections.Length;
            var tabBounds = MainSections.Select((_, i) => B(x + i * (tabW + tabGap), y, tabW, 24)).ToArray();
            compo.AddIconToggleButtons(MainSections.Select(SectionIcon).ToArray(), CairoFont.ButtonText(), i =>
            {
                if (MainSections[i] == session.Section && page is Page.Reference or Page.Translations) OpenPage(Page.Editor);
                else OnSectionPicked(MainSections[i]);
            }, tabBounds, "sectiontabs");
            for (int i = 0; i < MainSections.Length; i++) compo.AddTip(L("canrpgmobs:editor-tab-" + MainSections[i], MainSections[i]), tabBounds[i]);
            int activeTab = Array.IndexOf(MainSections, session.Section);
            afterCompose.Add(() => SingleComposer.GetToggleButton("sectiontabs-" + activeTab)?.SetValue(true));

            // first item stands for "not in the library", the last one is the spell/action reference page
            const string referenceCode = "@reference", translationsCode = "@translations";
            var libraryCodes = LibrarySections.Prepend("").Append(referenceCode).Append(translationsCode).ToArray();
            var libraryNames = LibrarySections.Select(s => $"{L("canrpgmobs:editor-section-" + s, s)} ({EntryIds(s).Count})")
                .Prepend(L("canrpgmobs:editor-library-pick", "Library: races, roles, AI..."))
                .Append(L("canrpgmobs:editor-ref-title", "Spells and actions (reference)"))
                .Append(L("canrpgmobs:editor-tr-title", "Translations")).ToArray();
            int libraryIndex = page == Page.Reference ? Array.IndexOf(libraryCodes, referenceCode)
                : page == Page.Translations ? Array.IndexOf(libraryCodes, translationsCode)
                : Math.Max(0, Array.IndexOf(libraryCodes, session.Section));
            compo.AddDropDown(libraryCodes, libraryNames, libraryIndex, (code, _) =>
            {
                if (code == referenceCode) OpenReference();
                else if (code == translationsCode) OpenPage(Page.Translations);
                // same section skips OnSectionPicked, still leave the page
                else if (code == session.Section && page is Page.Reference or Page.Translations) OpenPage(Page.Editor);
                else if (code.Length > 0) OnSectionPicked(code);
                else Compose();
            }, B(x, y + RowStep, ListW), "section");

            SearchRow(compo, x, y + RowStep * 2, ListW - 40, filterDraft, v => filterDraft = v,
                "x", x + ListW - 36, 36, () => { filter = filterDraft = ""; listPage = 0; });
            if (MobFilters) AddMobFilters(compo, x, y + ListTop);

            var entries = FilteredEntries();
            int rows = ListRows;
            int pages = Math.Max(1, (entries.Count + rows - 1) / rows);
            listPage = Math.Clamp(listPage, 0, pages - 1);

            double row = y + EntriesTop + 4;
            foreach (var id in entries.Skip(listPage * rows).Take(rows))
            {
                string entryId = id;
                bool selected = session.Draft != null && Same(session.OpenedId ?? session.DraftId, id);
                string label = (selected ? "> " : "") + EntryLabel(id);
                if (!TwoLineRows)
                    compo.AddFitButton(label, () => OnEntryClicked(entryId), B(x, row, ListW), EnumButtonStyle.Small);
                else
                {
                    // blank button, the two lines are dynamic text on top (static text would end up under it)
                    var rowBounds = B(x, row, ListW, EntryH);
                    compo.AddSmallButton("", () => OnEntryClicked(entryId), rowBounds, EnumButtonStyle.Small);
                    // the open one in the accent colour
                    var nameFont = selected ? UiKit.ValueFont() : CairoFont.WhiteSmallText();
                    string shown = UiKit.Fit(label, nameFont, ListW - 16);
                    compo.AddDynamicText(shown, nameFont, B(x + 8, row + 3, ListW - 16, 18), NextKey());
                    // the id goes here now that the first line shows the name alone
                    string details = EntryDetails(id), shortId = Short(id);
                    string extra = EntryName(id) is { Length: > 0 } entryName && entryName != shortId
                        ? (details.Length > 0 ? details + "  ·  " + shortId : shortId) : details;
                    if (extra.Length > 0) compo.AddDynamicText(UiKit.Fit(extra, HelpFont(), ListW - 16), HelpFont(), B(x + 8, row + 20, ListW - 16, 14), NextKey());
                    if (shown != label) compo.AddTip(label, rowBounds);
                }
                row += EntryH + 2;
            }
            if (entries.Count == 0)
                compo.AddStaticText(L("canrpgmobs:editor-list-empty", "Nothing matches."), HelpFont(), B(x + 4, row + 6, ListW - 8));

            double bottom = y + ListBottom;
            bool mobs = session.Section == "mobs";
            Pager(compo, x, bottom, listPage, pages, p => listPage = p, entries.Count, 132)
                .AddSmallButton(mobs ? (page == Page.Wizard ? "> " : "") + L("canrpgmobs:editor-new-mob", "+ New mob") : L("canrpgmobs:editor-new", "+ New"),
                    mobs ? () => OpenPage(Page.Wizard) : (ActionConsumable)OnNew, B(x + 140, bottom, ListW - 140), EnumButtonStyle.Small);

            double tools = bottom + RowStep;
            double half = (ListW - 6) / 2;
            bool copyable = session.Section is "mobs" or "races" or "roles" or "lootTables";
            if (copyable) compo.AddSmallButton(L("canrpgmobs:editor-copy-from", "Create based on..."), OnCreateFrom, B(x, tools, half), EnumButtonStyle.Small);
            compo.AddSmallButton((page == Page.Test ? "> " : "") + L("canrpgmobs:editor-test", "Test panel"), () => OpenPage(Page.Test),
                copyable ? B(x + half + 6, tools, half) : B(x, tools, ListW), EnumButtonStyle.Small);
            if (mobs) compo.AddSmallButton(L("canrpgmobs:editor-new-empty", "Empty entry"), OnNew, B(x, tools + RowStep, ListW), EnumButtonStyle.Small);
        }

        private static string SectionIcon(string section) => UiKit.ClassesIcon(section switch
        {
            "mobs" => "skeleton", "zones" => "position-marker", "groups" => "dark-squad", _ => "pouch-with-beads"
        });

        private void AddMobFilters(GuiComposer compo, double x, double y)
        {
            var data = mod.Catalog.Data;
            const double gap = 6;
            double half = (ListW - gap) / 2;

            void Filter(string[] codes, string[] names, string current, Action<string> set, ElementBounds bounds)
            {
                compo.AddDropDown(codes, names, Math.Max(0, Array.IndexOf(codes, current)), (code, _) => { set(code); listPage = 0; Compose(); }, bounds, NextKey());
            }

            // race and role side by side, sort below: three in a row don't fit the names
            var races = data.Races.Keys.OrderBy(k => k).ToArray();
            var roles = data.Roles.Keys.OrderBy(k => k).ToArray();
            Filter(races.Prepend("").ToArray(), races.Select(r => ShortName("races", r)).Prepend(L("canrpgmobs:editor-filter-race", "Any race")).ToArray(),
                raceFilter, v => raceFilter = v, B(x, y, half));
            Filter(roles.Prepend("").ToArray(), roles.Select(r => ShortName("roles", r)).Prepend(L("canrpgmobs:editor-filter-role", "Any role")).ToArray(),
                roleFilter, v => roleFilter = v, B(x + half + gap, y, half));
            Filter(SortModes, SortModes.Select(s => L("canrpgmobs:editor-sort-" + s, s)).ToArray(), sortMode, v => sortMode = v, B(x, y + RowStep, ListW));
        }

        private List<string> FilteredEntries()
        {
            var ids = EntryIds(session.Section)
                .Where(id => filter.Length == 0 || id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                             || EntryName(id).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            // by the name the list shows, not by id
            if (!MobFilters) return ids.OrderBy(id => EntryName(id) is { Length: > 0 } n ? n : Short(id), StringComparer.CurrentCultureIgnoreCase).ToList();

            var templates = mod.Catalog.Data.Templates;
            MobTemplateModel? T(string id) => templates.GetValueOrDefault(id);
            if (raceFilter.Length > 0) ids = ids.Where(id => T(id) is { } t && Same(t.race, raceFilter));
            if (roleFilter.Length > 0) ids = ids.Where(id => T(id) is { } t && Same(t.role, roleFilter));
            // ids come sorted, OrderBy is stable
            return (sortMode switch
            {
                "name" => ids.OrderBy(id => EntryName(id) is { Length: > 0 } n ? n : Short(id), StringComparer.CurrentCultureIgnoreCase),
                "level" => ids.OrderBy(id => T(id)?.levelRange is { Length: 2 } r ? r[0] : int.MaxValue),
                "race" => ids.OrderBy(id => T(id)?.race ?? "", StringComparer.Ordinal),
                "edited" => ids.OrderBy(id => mod.Store.Contains("mobs", id) ? 0 : 1),
                _ => ids
            }).ToList();
        }

        // second column: what tells entries apart at a glance
        private string EntryDetails(string id)
        {
            var data = mod.Catalog.Data;
            switch (session.Section)
            {
                case "mobs" when data.Templates.TryGetValue(id, out var t):
                    string levels = t.levelRange is { Length: 2 } r ? (r[0] == r[1] ? $"{r[0]}" : $"{r[0]}-{r[1]}") : "";
                    string kind = $"{Clip(ShortName("races", t.race), 14)} / {Clip(ShortName("roles", t.role), 14)}";
                    return levels.Length > 0 ? L("canrpgmobs:editor-entry-levels", "lvl {0}", levels) + "  ·  " + kind : kind;
                case "zones" when data.Zones.TryGetValue(id, out var z):
                    string level = z.level is { Length: 2 } zl ? $"{zl[0]}-{zl[1]}" : "";
                    return z.enabled ? level : $"{level} {L("canrpgmobs:editor-zone-off", "off")}".Trim();
                case "groups" when data.Groups.TryGetValue(id, out var g):
                    return L("canrpgmobs:editor-group-members", "{0} kinds", g.members?.Count ?? 0);
                case "races" or "roles":
                    bool race = session.Section == "races";
                    int users = data.Templates.Values.Count(m => Same(race ? m.race : m.role, id));
                    string usedBy = users == 0 ? "" : L("canrpgmobs:editor-used-count", "{0} mob(s)", users);
                    // the model is what tells look-alike races apart
                    string look = race && data.Races.TryGetValue(id, out var raceModel) && raceModel.appearance is { } a
                        ? (a.kind == AppearanceKinds.Shape ? Short(a.shape ?? "") : Short(a.model)) : "";
                    return string.Join("  ·  ", new[] { look, usedBy }.Where(s => s.Length > 0));
                default:
                    return "";
            }
        }

        // name alone, for tight columns
        private string ShortName(string section, string id)
        {
            string qualified = Q(id);
            var info = Section(section);
            var model = info?.Find(mod.Catalog.Data, qualified);
            string? name = model == null ? null : info!.Name?.Invoke(model);
            return string.IsNullOrEmpty(name) ? Short(qualified) : name!;
        }

        // so jumping to a loot table and back keeps the list page
        private readonly Dictionary<string, int> listPageBySection = new(StringComparer.Ordinal);

        // also closes the draft
        private void SwitchSection(string section)
        {
            listPageBySection[session.Section] = listPage;
            session.SelectSection(section);
            listPage = listPageBySection.GetValueOrDefault(section);
        }

        private void ShowInList(string id)
        {
            int at = FilteredEntries().FindIndex(entry => Same(entry, id));
            if (at >= 0) listPage = at / ListRows;
        }

        private readonly Dictionary<string, List<string>> entryIdCache = new();
        private MobCatalog? entryIdCatalog;
        private CatalogStore? entryIdStore;

        // cached per catalog/store instance (a sync replaces both); don't modify the returned list
        private List<string> EntryIds(string section)
        {
            if (!ReferenceEquals(entryIdCatalog, mod.Catalog) || !ReferenceEquals(entryIdStore, mod.Store))
            {
                entryIdCache.Clear();
                entryIdCatalog = mod.Catalog;
                entryIdStore = mod.Store;
            }
            if (!entryIdCache.TryGetValue(section, out var ids))
                entryIdCache[section] = ids = BuildEntryIds(section);
            return ids;
        }

        private List<string> BuildEntryIds(string section)
        {
            var data = mod.Catalog.Data;
            var ids = Section(section)?.Ids(data) ?? Enumerable.Empty<string>();
            var all = new SortedSet<string>(ids, StringComparer.Ordinal);
            // stored entries that failed validation aren't in the catalog but must stay editable
            if (mod.Store.Root[section] is JArray stored)
            {
                foreach (var token in stored)
                    if (token is JObject entry && entry["id"] is JValue { Type: JTokenType.String } id)
                        all.Add(Q((string)id!));
            }
            return all.ToList();
        }

        private string EntryLabel(string id)
        {
            string shortId = Short(id);
            string name = EntryName(id);
            // two-line rows carry the id on the second line
            string label = name.Length == 0 || name == shortId ? shortId : TwoLineRows ? name : $"{name} · {shortId}";
            if (mod.Store.Contains(session.Section, id)) label += " *";
            if (!CatalogStore.IsInCatalog(mod.Catalog.Data, session.Section, id)) label += " (!)";
            return label;
        }

        private string EntryName(string id)
        {
            var info = Section(session.Section);
            if (info?.Name is not { } name) return "";
            var model = info.Find(mod.Catalog.Data, id);
            return (model == null ? null : name(model)) ?? "";
        }

        private bool OnEntryClicked(string id)
        {
            confirmDelete = false;
            pendingSection = null;
            // already open (back from wizard/test page), nothing to discard
            if (session.Draft != null && session.OpenedId != null && Same(id, session.OpenedId))
            {
                pendingOpen = null;
                page = Page.Editor;
                pick = null;
                itemPick = null;
                Compose();
                return true;
            }
            if (ConfirmDiscard(ref pendingOpen, id)) return true;
            pendingOpen = null;
            OpenEntry(session.Section, id);
            Compose();
            return true;
        }

        // from the zone panel; never drops unsaved work of another entry
        public void ShowEntry(string section, string id)
        {
            if (session.IsDirty && !(session.Section == section && Same(session.DraftId, id)))
                SetStatus(L("canrpgmobs:editor-unsaved-elsewhere", "Save or revert the open entry first, then open {0}.", Short(id)), true);
            else
            {
                if (session.Section != section) SwitchSection(section);
                OpenEntry(section, id);
            }
            if (IsOpened()) Compose();
            else TryOpen();
        }

        private void OpenEntry(string section, string id)
        {
            var json = mod.Store.Get(section, id) ?? FromCatalog(section, id);
            if (json == null)
            {
                SetStatus(L("canrpgmobs:editor-not-found", "That entry no longer exists."), true);
                return;
            }
            session.Open(section, json, id);
            page = Page.Editor;
            ShowInList(id);
            ResetPages();
            // OpenLootTable sets it again after this
            lootReturn = null;
            confirmDelete = false;
            SetStatus("", false);
            previewKey = SuggestPreviewKey();
        }

        private JObject? FromCatalog(string section, string id)
        {
            var model = Section(section)?.Find(mod.Catalog.Data, id);
            return model == null ? null : JObject.FromObject(model, Serializer);
        }

        private void OnSectionPicked(string section)
        {
            if (section == session.Section) return;
            confirmDelete = false;
            pendingOpen = null;
            if (ConfirmDiscard(ref pendingSection, section)) return;
            pendingSection = null;
            bool enteringLibrary = InLibrary(section) && !InLibrary(session.Section);
            SwitchSection(section);
            page = Page.Editor;
            filter = filterDraft = "";
            ResetPages();
            lootReturn = null;
            SetStatus(enteringLibrary ? L("canrpgmobs:editor-library-note", "Building blocks mobs are made of. A change here affects every mob that uses it.") : "", false);
            Compose();
        }

        private bool OnNew()
        {
            confirmDelete = false;
            pendingSection = null;
            if (ConfirmDiscard(ref pendingOpen, NewEntryMark)) return true;
            pendingOpen = null;
            string id = UniqueId($"{canrpgmobsModSystem.ModId}:new_{EditorSchema.SectionNodes[session.Section].ToLowerInvariant()}");
            session.OpenNew(session.Section, EditorSchema.NewEntry(session.Section, id, mod.Catalog.Data));
            page = Page.Editor;
            ResetPages();
            lootReturn = null;
            SetStatus(L("canrpgmobs:editor-created", "New entry {0}. Fill it in, then save.", id), false);
            previewKey = SuggestPreviewKey();
            Compose();
            return true;
        }
    }
}
