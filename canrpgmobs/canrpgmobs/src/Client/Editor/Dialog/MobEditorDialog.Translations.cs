using System;
using System.Collections.Generic;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // One language at a time: every name key with its English text and a field for the translation.
    // Saved into the world's content ("lang"), the server and every client put it into the game's language tables.
    public partial class MobEditorDialog
    {
        private const string TranslationsTag = "lang";
        private const int TranslationRows = 12;

        // the game's own languages; a client may have only some of them loaded
        private static readonly string[] GameLocales =
            { "cs", "da", "de", "es-419", "es-es", "fi", "fr", "hu", "id", "it", "ja", "ko", "nl", "no", "pl", "pt-br", "pt-pt", "ro", "ru", "sv-se", "th", "tr", "uk", "vi", "zh-cn", "zh-tw" };

        private string? trLocale;
        private string trFilter = "";
        private string trFilterDraft = "";
        private int trPage;
        private bool trMissingOnly;
        // key -> typed text, for trLocale only
        private readonly Dictionary<string, string> trEdits = new(StringComparer.Ordinal);

        private sealed record TranslationItem(string Key, string Kind, string English, string Section, string Id);

        private string TrLocale => trLocale ??= Lang.CurrentLocale is { Length: > 0 } current && current != "en"
            ? current
            : mod.Catalog.Data.Lang.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? GameLocales[0];

        private void AddTranslationsPage(GuiComposer compo, double x, double y)
        {
            var data = mod.Catalog.Data;
            var locales = GameLocales.Concat(Lang.AvailableLanguages.Keys).Concat(data.Lang.Keys)
                .Where(code => code != "en" && LangTables.IsLocale(code)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.Ordinal).ToArray();
            string locale = TrLocale;
            // languages that already have some of ours get a mark
            var localeNames = locales.Select(code => data.Lang.ContainsKey(code) ? code + " *" : code).ToArray();
            compo.AddDropDown(locales, localeNames, Math.Max(0, Array.IndexOf(locales, locale)), (code, _) =>
            {
                if (trEdits.Count > 0)
                {
                    SetStatus(L("canrpgmobs:editor-tr-unsaved", "Save or drop the {0} changes first.", locale), true);
                    Compose();
                    return;
                }
                trLocale = code;
                trPage = 0;
                Compose();
            }, B(x, y, 100), NextKey());
            SearchRow(compo, x + 106, y, 180, trFilterDraft, v => trFilterDraft = v,
                L("canrpgmobs:editor-itempick-search", "Search"), x + 290, 80, () => { trFilter = trFilterDraft.Trim(); trPage = 0; });
            compo.AddSmallButton((trMissingOnly ? "> " : "") + L("canrpgmobs:editor-tr-missing", "Untranslated"), () => { trMissingOnly = !trMissingOnly; trPage = 0; Compose(); return true; },
                B(x + 374, y, 112), EnumButtonStyle.Small);
            // typing doesn't recompose, so no count here
            compo.AddSmallButton(L("canrpgmobs:editor-tr-save", "Save"), SaveTranslations,
                B(x + 490, y, PanelW - 490), EnumButtonStyle.Small);
            PanelInset(compo, x, y, 30);

            var items = TranslationItems()
                .Where(i => trFilter.Length == 0 || i.English.IndexOf(trFilter, StringComparison.OrdinalIgnoreCase) >= 0 || i.Key.IndexOf(trFilter, StringComparison.OrdinalIgnoreCase) >= 0
                            || (CurrentTranslation(locale, i.Key).text?.IndexOf(trFilter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .Where(i => !trMissingOnly || CurrentTranslation(locale, i.Key).text == null)
                // untranslated first, then by name
                .OrderBy(i => CurrentTranslation(locale, i.Key).text == null ? 0 : 1)
                .ThenBy(i => i.English, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            int pages = Math.Max(1, (items.Count + TranslationRows - 1) / TranslationRows);
            trPage = Math.Clamp(trPage, 0, pages - 1);

            double row = y + 36;
            foreach (var item in items.Skip(trPage * TranslationRows).Take(TranslationRows))
            {
                string key = item.Key;
                var (text, fromFile) = CurrentTranslation(locale, key);
                compo.AddFitText($"{item.Kind}: {item.English}", UiKit.LabelFont(), B(x + 4, row + 5, 236, 18));
                TextInput(compo, B(x + 244, row, 300), text ?? "", v =>
                {
                    string typed = v.Trim();
                    string saved = mod.Catalog.Data.Lang.TryGetValue(locale, out var table) && table.TryGetValue(key, out var own) ? own : "";
                    if (typed == saved) trEdits.Remove(key);
                    else trEdits[key] = typed;
                });
                // * = typed, not saved; "mod" = from a lang file, typing overrides it for this server
                string mark = trEdits.ContainsKey(key) ? "*" : fromFile ? L("canrpgmobs:editor-tr-from-mod", "mod") : "";
                if (mark.Length > 0) compo.AddStaticText(mark, HelpFont(), B(x + 550, row + 6, 50, 16));
                row += RowStep;
            }
            if (items.Count == 0)
                compo.AddStaticText(L("canrpgmobs:editor-list-empty", "Nothing matches."), HelpFont(), B(x + 4, row + 6, 300));
            double bottom = y + 36 + TranslationRows * RowStep + 2;
            Pager(compo, x, bottom, trPage, pages, p => trPage = p, items.Count);
            compo.AddStaticText(L("canrpgmobs:editor-tr-note", "Empty = the English name shows. Saved into this world, players get it on join. Lang files: /canrpgmobs lang export {0}", locale),
                HelpFont(), B(x + 210, bottom + 2, PanelW - 210, 30));
        }

        // typed, else ours, else a lang file's
        private (string? text, bool fromFile) CurrentTranslation(string locale, string key)
        {
            if (trEdits.TryGetValue(key, out var typed)) return (typed.Length > 0 ? typed : null, false);
            if (mod.Catalog.Data.Lang.TryGetValue(locale, out var table) && table.TryGetValue(key, out var own)) return (own, false);
            return LangTables.Original(locale, key) is { } file ? (file, true) : (null, false);
        }

        private List<TranslationItem> TranslationItems()
        {
            var data = mod.Catalog.Data;
            var items = new List<TranslationItem>();
            void Add<T>(Dictionary<string, T> entries, string section, string kind, Func<T, LocText?> name)
            {
                string label = L("canrpgmobs:editor-section-" + section, section);
                foreach (var (id, entry) in entries)
                {
                    var text = name(entry);
                    string key = text?.Key ?? text?.AutoKey ?? CatalogLoader.AutoKeyFor(kind, id);
                    string english = text != null && text.TryGetValue("en", out var en) && en.Length > 0 ? en
                        : LangTables.Original("en", key) ?? Short(id);
                    items.Add(new TranslationItem(key, label, english, section, id));
                }
            }
            Add(data.Templates, "mobs", "mob", m => m.name);
            Add(data.Races, "races", "race", r => r.name);
            Add(data.Roles, "roles", "role", r => r.name);
            Add(data.Zones, "zones", "zone", z => z.name);
            Add(data.Groups, "groups", "group", g => g.name);
            Add(data.Tiers, "tiers", "tier", t => t.name);
            return items.GroupBy(i => i.Key).Select(g => g.First()).ToList();
        }

        private bool SaveTranslations()
        {
            if (trEdits.Count == 0) return true;
            var json = new JObject();
            foreach (var (key, text) in trEdits) json[key] = text;
            mod.SendEdit(canrpgmobsModSystem.LangSection, TrLocale, null, json.ToString(Newtonsoft.Json.Formatting.None), TranslationsTag);
            SetStatus(L("canrpgmobs:editor-saving", "Saving..."), false);
            return true;
        }

        private void OnTranslationsSaved(canrpgmobs.Net.CatalogEditResultPacket packet)
        {
            if (packet.Ok) trEdits.Clear();
            SetStatus(packet.Ok ? packet.Message : L("canrpgmobs:editor-not-saved", "Not saved: {0}", packet.Message), !packet.Ok);
            if (page == Page.Translations) Compose();
        }
    }
}
