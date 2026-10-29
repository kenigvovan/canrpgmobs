using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using canrpgmobs.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canrpgmobs.Catalog
{
    // In-game edits, layered over all mod files. Kept as raw JSON so unknown fields survive a save.
    public sealed class CatalogStore
    {
        public const string FileName = "canrpgmobs-content.json";
        public const string SourceName = "ModConfig/" + FileName;
        public static readonly string[] Sections = { "races", "roles", "mobs", "aiProfiles", "factions", "tiers", "lootTables", "animSets", "soundSets", "groups", "zones" };

        private CatalogStore(JObject root, bool readOnly)
        {
            Root = root;
            ReadOnly = readOnly;
        }

        public JObject Root { get; }
        // server: file unreadable, don't overwrite it. client: always true, it's just a mirror
        public bool ReadOnly { get; }
        public int EntryCount => Sections.Sum(s => (Root[s] as JArray)?.Count ?? 0);
        public bool IsEmpty => EntryCount == 0 && (Root["aliases"] as JObject)?.Count is null or 0 && Root["scaling"] == null && (Root["lang"] as JObject)?.Count is null or 0;

        // empty text removes the key
        public void SetTranslation(string locale, string key, string text)
        {
            if (Root["lang"] is not JObject lang) Root["lang"] = lang = new JObject();
            if (lang[locale] is not JObject table) lang[locale] = table = new JObject();
            if (string.IsNullOrEmpty(text)) table.Remove(key);
            else table[key] = text;
            if (table.Count == 0) lang.Remove(locale);
            if (lang.Count == 0) Root.Remove("lang");
        }

        public static CatalogStore Empty() => new(NewRoot(), false);

        public static CatalogStore LoadServer(ILogger logger)
        {
            if (!File.Exists(FilePath)) return Empty();
            try { return new CatalogStore(JObject.Parse(File.ReadAllText(FilePath)), false); }
            catch (Exception e)
            {
                logger.Error("[canrpgmobs] {0} could not be read, in-game edits are off until it is fixed (the file is left untouched): {1}", SourceName, e.Message);
                return new CatalogStore(NewRoot(), true);
            }
        }

        // client side, from the sync packet
        public static CatalogStore Adopt(string? json, ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(json)) return new CatalogStore(NewRoot(), true);
            try { return new CatalogStore(JObject.Parse(json!), true); }
            catch (Exception e)
            {
                logger?.Warning("[canrpgmobs] the server sent content that isn't valid JSON, showing mod files only: {0}", e.Message);
                return new CatalogStore(NewRoot(), true);
            }
        }

        public bool Save(ILogger logger)
        {
            if (ReadOnly) return false;
            try
            {
                Directory.CreateDirectory(GamePaths.ModConfig);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, Root.ToString(Formatting.Indented));
                // write to temp and move, no half-written file on crash
                File.Move(temp, FilePath, true);
                return true;
            }
            catch (Exception e)
            {
                logger.Error("[canrpgmobs] could not save {0}: {1}", SourceName, e.Message);
                return false;
            }
        }

        public string Serialize() => Root.ToString(Formatting.None);

        public CatalogStore Clone() => new((JObject)Root.DeepClone(), ReadOnly);

        public bool Contains(string section, string id) => Find(section, id, out _) >= 0;

        public JObject? Get(string section, string id)
        {
            int at = Find(section, id, out var array);
            return at < 0 ? null : array![at] as JObject;
        }

        public void Replace(string section, JObject entry)
        {
            var array = Section(section, true)!;
            int at = IndexOf(array, (string?)entry["id"] ?? "");
            if (at >= 0) array[at] = entry;
            else array.Add(entry);
        }

        public bool Remove(string section, string id)
        {
            int at = Find(section, id, out var array);
            if (at < 0) return false;
            array!.RemoveAt(at);
            return true;
        }

        public void SetAlias(string from, string to)
        {
            if (Root["aliases"] is not JObject aliases)
            {
                aliases = new JObject();
                Root["aliases"] = aliases;
            }
            aliases[from] = to;
        }

        // otherwise later loads warn about a missing alias target
        public void RemoveAliasesTo(string id)
        {
            if (Root["aliases"] is not JObject aliases) return;
            foreach (var property in aliases.Properties().ToList())
                if (property.Value.Type == JTokenType.String && IdRules.QualifyKey((string)property.Value!, canrpgmobsModSystem.ModId) == id)
                    property.Remove();
        }

        // same id replaces, entries without a string id are skipped
        public List<(string section, string id)> Merge(JObject file)
        {
            var taken = new List<(string section, string id)>();
            foreach (var section in Sections)
            {
                if (file[section] is not JArray entries) continue;
                foreach (var token in entries)
                {
                    if (token is not JObject entry || entry["id"]?.Type != JTokenType.String) continue;
                    Replace(section, (JObject)entry.DeepClone());
                    taken.Add((section, (string)entry["id"]!));
                }
            }
            if (file["aliases"] is JObject aliases)
            {
                foreach (var property in aliases.Properties())
                    if (property.Value.Type == JTokenType.String) SetAlias(property.Name, (string)property.Value!);
            }
            if (file["scaling"] is JObject scaling) Root["scaling"] = scaling.DeepClone();
            if (file["lang"] is JObject lang)
                foreach (var locale in lang.Properties())
                    if (LangTables.IsLocale(locale.Name) && locale.Value is JObject table)
                        foreach (var entry in table.Properties())
                            if (entry.Value.Type == JTokenType.String) SetTranslation(locale.Name, entry.Name, (string)entry.Value!);
            return taken;
        }

        // i.e. passed validation
        public static bool IsInCatalog(CatalogData data, string section, string id) => section switch
        {
            "races" => data.Races.ContainsKey(id),
            "roles" => data.Roles.ContainsKey(id),
            "mobs" => data.Templates.ContainsKey(id),
            "aiProfiles" => data.AiProfiles.ContainsKey(id),
            "factions" => data.Factions.ContainsKey(id),
            "tiers" => data.Tiers.ContainsKey(id),
            "lootTables" => data.LootTables.ContainsKey(id),
            "animSets" => data.AnimSets.ContainsKey(id),
            "soundSets" => data.SoundSets.ContainsKey(id),
            "groups" => data.Groups.ContainsKey(id),
            "zones" => data.Zones.ContainsKey(id),
            _ => false
        };

        private static string FilePath => Path.Combine(GamePaths.ModConfig, FileName);

        private static JObject NewRoot() => new() { ["formatVersion"] = FormatMigrator.CurrentVersion };

        private JArray? Section(string section, bool create)
        {
            if (Root[section] is JArray array) return array;
            if (!create) return null;
            array = new JArray();
            Root[section] = array;
            return array;
        }

        private int Find(string section, string id, out JArray? array)
        {
            array = Section(section, false);
            return array == null ? -1 : IndexOf(array, id);
        }

        // "goblin" and "canrpgmobs:goblin" are the same entry
        private static int IndexOf(JArray array, string id)
        {
            string wanted = IdRules.Qualify(id, canrpgmobsModSystem.ModId);
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JObject entry && entry["id"]?.Type == JTokenType.String
                    && IdRules.Qualify((string)entry["id"]!, canrpgmobsModSystem.ModId) == wanted)
                    return i;
            }
            return -1;
        }
    }
}
