using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Client.Editor.Controllers
{
    // Resolves the draft against the live catalog with the same merge rules as the game. Cheap enough to run on every
    // recompose; real validation happens on Validate/Save. Races are shown with the first role and vice versa.
    public sealed class DraftResolver
    {
        private string cacheKey = "";
        private MobCatalog? cachedCatalog;
        private ResolvedMob? cached;
        private string? cachedError;

        public ResolvedMob? Resolve(MobCatalog catalog, string section, JObject? draft, out string? error)
        {
            error = null;
            if (draft == null || section is not ("races" or "roles" or "mobs")) return null;
            string key = section + "\n" + draft.ToString(Formatting.None);
            // catalog changes (another entry saved) invalidate it too
            if (key != cacheKey || !ReferenceEquals(catalog, cachedCatalog))
            {
                cacheKey = key;
                cachedCatalog = catalog;
                cached = Build(catalog.Data, section, draft, out cachedError);
            }
            error = cachedError;
            return cached;
        }

        private static ResolvedMob? Build(CatalogData source, string section, JObject draft, out string? error)
        {
            string domain = canrpgmobsModSystem.ModId;
            var data = Copy(source);
            string id = IdRules.Qualify(draft["id"]?.ToString() ?? "", domain);
            string mobKey;
            try
            {
                switch (section)
                {
                    case "races":
                    {
                        var race = draft.ToObject<RaceModel>() ?? new RaceModel();
                        race.id = id;
                        CatalogLoader.QualifyRace(race, domain);
                        data.Races[id] = race;
                        mobKey = id + IdRules.KeySeparator + First(data.Roles.Keys);
                        break;
                    }
                    case "roles":
                    {
                        var role = draft.ToObject<RoleModel>() ?? new RoleModel();
                        role.id = id;
                        CatalogLoader.QualifyRole(role, domain);
                        data.Roles[id] = role;
                        mobKey = First(data.Races.Keys) + IdRules.KeySeparator + id;
                        break;
                    }
                    default:
                    {
                        var template = draft.ToObject<MobTemplateModel>() ?? new MobTemplateModel();
                        template.id = id;
                        CatalogLoader.QualifyTemplate(template, domain);
                        data.Templates[id] = template;
                        mobKey = id;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            return MobResolver.Resolve(mobKey, data, null, out error);
        }

        // wizard: entries that don't exist yet, resolved together
        public ResolvedMob? ResolveBundle(MobCatalog catalog, JObject bundle, string mobId, out string? error)
        {
            string key = "bundle\n" + bundle.ToString(Formatting.None);
            if (key != cacheKey || !ReferenceEquals(catalog, cachedCatalog))
            {
                cacheKey = key;
                cachedCatalog = catalog;
                cached = BuildBundle(catalog.Data, bundle, mobId, out cachedError);
            }
            error = cachedError;
            return cached;
        }

        private static ResolvedMob? BuildBundle(CatalogData source, JObject bundle, string mobId, out string? error)
        {
            string domain = canrpgmobsModSystem.ModId;
            var data = Copy(source);
            try
            {
                IEnumerable<T> Entries<T>(string section) where T : CatalogEntryModel
                    => (bundle[section] as JArray)?.OfType<JObject>().Select(e => e.ToObject<T>()!).Where(e => e != null) ?? Enumerable.Empty<T>();

                foreach (var race in Entries<RaceModel>("races")) { race.id = IdRules.Qualify(race.id, domain); CatalogLoader.QualifyRace(race, domain); data.Races[race.id] = race; }
                foreach (var role in Entries<RoleModel>("roles")) { role.id = IdRules.Qualify(role.id, domain); CatalogLoader.QualifyRole(role, domain); data.Roles[role.id] = role; }
                foreach (var ai in Entries<AiProfileModel>("aiProfiles")) { ai.id = IdRules.Qualify(ai.id, domain); data.AiProfiles[ai.id] = ai; }
                foreach (var table in Entries<LootTableModel>("lootTables")) { table.id = IdRules.Qualify(table.id, domain); CatalogLoader.QualifyLootTable(table, domain); data.LootTables[table.id] = table; }
                foreach (var t in Entries<MobTemplateModel>("mobs")) { t.id = IdRules.Qualify(t.id, domain); CatalogLoader.QualifyTemplate(t, domain); data.Templates[t.id] = t; }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            return MobResolver.Resolve(mobId, data, null, out error);
        }

        private static string First(IEnumerable<string> keys) => keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? "";

        // shallow, models are shared with the live catalog (read-only here)
        private static CatalogData Copy(CatalogData source)
        {
            var data = new CatalogData { Scaling = source.Scaling };
            void Into<T>(Dictionary<string, T> from, Dictionary<string, T> to)
            {
                foreach (var pair in from) to[pair.Key] = pair.Value;
            }
            Into(source.Races, data.Races);
            Into(source.Roles, data.Roles);
            Into(source.Templates, data.Templates);
            Into(source.AiProfiles, data.AiProfiles);
            Into(source.Factions, data.Factions);
            Into(source.Tiers, data.Tiers);
            Into(source.LootTables, data.LootTables);
            Into(source.AnimSets, data.AnimSets);
            Into(source.SoundSets, data.SoundSets);
            Into(source.Groups, data.Groups);
            Into(source.Aliases, data.Aliases);
            return data;
        }
    }
}
