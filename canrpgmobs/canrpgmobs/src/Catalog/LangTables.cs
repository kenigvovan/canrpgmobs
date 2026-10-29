using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canrpgmobs.Catalog
{
    // Our translations written straight into the game's language tables: GetAllEntries() hands out the live dictionary.
    // A reload of a language (rare) drops them; the next catalog load puts them back.
    public static class LangTables
    {
        // what the lang files had before we wrote over a key, null = nothing
        private static readonly Dictionary<(string locale, string key), string?> originals = new();
        private static readonly object sync = new();

        public static bool IsLocale(string? code)
            => !string.IsNullOrEmpty(code) && code!.Length <= 12 && code.All(c => char.IsLetterOrDigit(c) || c == '-');

        public static void Apply(Dictionary<string, Dictionary<string, string>> lang, ILogger? logger)
        {
            lock (sync)
            {
                try
                {
                    // put back what we wrote last time and is gone now
                    foreach (var ((locale, key), original) in originals.ToList())
                    {
                        if (lang.TryGetValue(locale, out var table) && table.ContainsKey(key)) continue;
                        if (Entries(locale) is { } entries)
                        {
                            if (original == null) entries.Remove(key);
                            else entries[key] = original;
                        }
                        originals.Remove((locale, key));
                    }
                    foreach (var (locale, table) in lang)
                    {
                        if (Entries(locale) is not { } entries) continue;
                        foreach (var (key, text) in table)
                        {
                            if (!originals.ContainsKey((locale, key))) originals[(locale, key)] = entries.TryGetValue(key, out var had) ? had : null;
                            entries[key] = text;
                        }
                    }
                }
                catch (Exception e)
                {
                    logger?.Warning("[canrpgmobs] could not add translations to the game's language tables: {0}", e.Message);
                }
            }
        }

        // what the lang files say, ignoring ours; for the editor
        public static string? Original(string locale, string key)
        {
            lock (sync)
            {
                if (originals.TryGetValue((locale, key), out var original)) return original;
            }
            return Entries(locale) is { } entries && entries.TryGetValue(key, out var text) ? text : null;
        }

        // every name key with its English text, the way the editor and the export list them
        public static List<(string key, string english)> NameKeys(CatalogData data)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (kind, id, text) in data.NamedEntries())
            {
                string key = text?.Key ?? text?.AutoKey ?? CatalogLoader.AutoKeyFor(kind, id);
                if (found.ContainsKey(key)) continue;
                found[key] = text != null && text.TryGetValue("en", out var en) && en.Length > 0 ? en : Original("en", key) ?? id;
            }
            return found.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        }

        public static IDictionary<string, string>? Entries(string locale)
            => Lang.AvailableLanguages.TryGetValue(locale, out var language) ? language.GetAllEntries() : null;
    }
}
