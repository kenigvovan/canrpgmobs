using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canrpgmobs.Catalog
{
    // never throws, problems go to the report
    public static partial class CatalogLoader
    {
        public const string AssetPath = "config/mobs/";

        // Parse cache per asset object, every editor save rebuilds the catalog. /reload hands out new asset
        // objects so it invalidates itself. Merging migrates in place, hence the DeepClone on use.
        private static readonly ConditionalWeakTable<IAsset, JObject> parsedFiles = new();
        // same for shape animation codes, NoAnimations = no animations or failed to parse
        private static readonly HashSet<string> NoAnimations = new();
        private static readonly ConditionalWeakTable<IAsset, HashSet<string>> shapeAnimations = new();

        // names translate through "<domain>:<kind>-<id>" in any lang file, no inline text per language needed
        private static void SetAutoKeys(CatalogData data)
        {
            foreach (var (kind, id, name) in data.NamedEntries())
                if (name != null) name.AutoKey = AutoKeyFor(kind, id);
        }

        public static string AutoKeyFor(string kind, string id)
        {
            int colon = id.IndexOf(':');
            return colon > 0 ? $"{id.Substring(0, colon)}:{kind}-{id.Substring(colon + 1)}" : $"{canrpgmobsModSystem.ModId}:{kind}-{id}";
        }

        // store: in-game content, null = mod files only
        // logProblems: false for trial builds of an edit, keeps the log clean
        // assetChecks: false skips lang key and shape checks (shapes are read from disk)
        public static MobCatalog Load(ICoreAPI api, ILogger logger, RuleVocabulary vocabulary, CatalogStore? store, out ValidationReport report, bool logProblems = true,
            bool assetChecks = true)
        {
            report = new ValidationReport();
            var data = new CatalogData();
            int files = 0;

            foreach (var asset in api.Assets.GetMany(AssetPath))
            {
                if (!asset.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string where = asset.Location.ToString();

                if (!parsedFiles.TryGetValue(asset, out var parsed))
                {
                    try { parsed = JObject.Parse(asset.ToText()); }
                    catch (Exception e)
                    {
                        report.Error(where, "not valid JSON - " + e.Message);
                        continue;
                    }
                    parsedFiles.AddOrUpdate(asset, parsed);
                }
                if (MergeFile((JObject)parsed.DeepClone(), asset.Location.Domain, where, data, report)) files++;
            }

            // in-game content last, it wins over mod files
            if (store != null && !store.IsEmpty && MergeFile((JObject)store.Root.DeepClone(), canrpgmobsModSystem.ModId, CatalogStore.SourceName, data, report))
                files++;

            SetAutoKeys(data);

            var spells = new NpcSpellCompatibility(api);
            var damageTypes = new HashSet<string>(Enum.GetNames(typeof(EnumDamageType)), StringComparer.OrdinalIgnoreCase);
            var rejected = MobValidator.Validate(data, spells, damageTypes, vocabulary, report);
            // removing an entry can break others (template of a rejected race), revalidate until stable
            for (int pass = 0; RemoveRejected(data, rejected) > 0 && pass < 5; pass++)
            {
                var again = new ValidationReport();
                rejected = MobValidator.Validate(data, spells, damageTypes, vocabulary, again);
                var known = new HashSet<string>(report.Problems.Select(p => p.ToString()));
                foreach (var problem in again.Problems)
                {
                    if (known.Contains(problem.ToString())) continue;
                    if (problem.Severity == Severity.Error) report.Error(problem.Path, problem.Message);
                    else if (problem.Severity == Severity.Warning) report.Warning(problem.Path, problem.Message);
                }
            }
            if (api.Side == EnumAppSide.Server)
            {
                CheckLootCodes(api, data, report);
                CheckGearCodes(api, data, report);
                CheckPlayerModels(api, data, report);
                CheckArchers(api, data, report);
                CheckExtensions(api, data, report);
            }
            if (assetChecks)
            {
                CheckLangKeys(data, report);
                CheckAssets(api, data, report);
            }

            // server only, otherwise singleplayer logs everything twice
            if (logProblems && api.Side == EnumAppSide.Server) LogProblems(logger, report);

            if (logProblems) logger.Notification("[canrpgmobs] catalog ({0}): {1} race(s), {2} role(s), {3} mob template(s), {4} AI profile(s), {5} faction(s), {6} tier(s), {7} loot table(s), {8} alias(es), {9} zone(s) from {10} file(s); {11}",
                api.Side, data.Races.Count, data.Roles.Count, data.Templates.Count, data.AiProfiles.Count, data.Factions.Count,
                data.Tiers.Count, data.LootTables.Count, data.Aliases.Count, data.Zones.Count, files, report.Summary);
            return new MobCatalog(data, spells);
        }

        // for a targeted asset reload
        public static IEnumerable<string> Domains(ICoreAPI api)
            => api.Assets.GetMany(AssetPath, null, false).Select(a => a.Location.Domain).Append(canrpgmobsModSystem.ModId).Distinct();

        private static void Merge(MobsFileModel file, string domain, string where, CatalogData data, ValidationReport report)
        {
            Add(file.races, data.Races, "race", domain, where, report, race => QualifyRace(race, domain));
            Add(file.roles, data.Roles, "role", domain, where, report, role => QualifyRole(role, domain));
            Add(file.mobs, data.Templates, "mob", domain, where, report, t => QualifyTemplate(t, domain));
            Add(file.tiers, data.Tiers, "tier", domain, where, report, null);
            Add(file.lootTables, data.LootTables, "lootTable", domain, where, report, table => QualifyLootTable(table, domain));
            if (file.scaling != null)
            {
                if (data.Scaling != null) report.Info(where, "scaling replaces the one loaded earlier");
                data.Scaling = file.scaling;
            }
            Add(file.animSets, data.AnimSets, "animSet", domain, where, report, set => set.extends = QualifyOpt(set.extends, domain));
            Add(file.soundSets, data.SoundSets, "soundSet", domain, where, report, set => set.extends = QualifyOpt(set.extends, domain));
            Add(file.aiProfiles, data.AiProfiles, "aiProfile", domain, where, report, null);
            Add(file.groups, data.Groups, "group", domain, where, report, group =>
            {
                if (!string.IsNullOrEmpty(group.faction)) group.faction = QualifyFaction(group.faction!, domain);
                if (group.members == null) return;
                foreach (var member in group.members)
                {
                    if (member == null) continue;
                    if (!string.IsNullOrWhiteSpace(member.mob)) member.mob = IdRules.QualifyKey(member.mob.Trim(), domain);
                    if (!string.IsNullOrEmpty(member.tier)) member.tier = QualifyTier(member.tier!, domain);
                }
            });
            Add(file.zones, data.Zones, "zone", domain, where, report, zone => QualifyZone(zone, domain));
            Add(file.factions, data.Factions, "faction", domain, where, report, f =>
            {
                if (f.relations != null) f.relations = QualifyKeys(f.relations, id => QualifyFaction(id, domain));
                f.attackOnSight = f.attackOnSight?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => QualifyFaction(t, domain)).Distinct().ToList();
            });

            MergeLang(file.lang, domain, where, data, report);

            if (file.aliases == null) return;
            foreach (var (from, to) in file.aliases)
            {
                if (!IdRules.IsClean(from) || string.IsNullOrWhiteSpace(to))
                {
                    report.Error(where, $"alias '{from}' to '{to}' skipped: invalid id or empty target");
                    continue;
                }
                data.Aliases[IdRules.Qualify(from, domain)] = IdRules.QualifyKey(to, domain);
            }
        }

        public static void LogProblems(ILogger logger, ValidationReport report)
        {
            foreach (var problem in report.Problems)
            {
                if (problem.Severity == Severity.Info) logger.VerboseDebug("[canrpgmobs] {0}", problem);
                else logger.Warning("[canrpgmobs] {0}", problem);
            }
        }

        private static bool MergeFile(JObject json, string domain, string where, CatalogData data, ValidationReport report)
        {
            if (!FormatMigrator.Migrate(json, where, report)) return false;
            SchemaCheck.ReportUnknownFields(json, typeof(MobsFileModel), where, report);

            MobsFileModel? file;
            try { file = json.ToObject<MobsFileModel>(); }
            catch (Exception e)
            {
                report.Error(where, "a value has the wrong type - " + e.Message);
                return false;
            }
            if (file == null) return false;

            Merge(file, domain, where, data, report);
            return true;
        }

        // keys without a domain get the file's, like in lang files
        private static void MergeLang(Dictionary<string, Dictionary<string, string>>? lang, string domain, string where, CatalogData data, ValidationReport report)
        {
            if (lang == null) return;
            foreach (var (locale, entries) in lang)
            {
                if (!LangTables.IsLocale(locale))
                {
                    report.Warning(where, $"lang '{locale}' skipped: not a language code");
                    continue;
                }
                if (entries == null) continue;
                if (!data.Lang.TryGetValue(locale, out var table)) data.Lang[locale] = table = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, text) in entries)
                {
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(text)) continue;
                    table[key.Contains(':') ? key.Trim() : $"{domain}:{key.Trim()}"] = text;
                }
            }
        }

        // also used by the editor for drafts
        internal static void QualifyRace(RaceModel race, string domain)
        {
            if (!string.IsNullOrEmpty(race.faction)) race.faction = QualifyFaction(race.faction!, domain);
            race.loot = QualifyAll(race.loot, domain);
            QualifySkills(race.skills, domain);
            race.animSet = QualifyOpt(race.animSet, domain);
            race.soundSet = QualifyOpt(race.soundSet, domain);
        }

        internal static void QualifyRole(RoleModel role, string domain)
        {
            role.aiProfile = QualifyOpt(role.aiProfile, domain);
            role.loot = QualifyAll(role.loot, domain);
            QualifySkills(role.skills, domain);
        }

        internal static void QualifyTemplate(MobTemplateModel t, string domain)
        {
            t.race = IdRules.Qualify(t.race ?? "", domain);
            t.role = IdRules.Qualify(t.role ?? "", domain);
            t.aiProfile = QualifyOpt(t.aiProfile, domain);
            if (!string.IsNullOrEmpty(t.faction)) t.faction = QualifyFaction(t.faction!, domain);
            if (!string.IsNullOrEmpty(t.defaultTier)) t.defaultTier = QualifyTier(t.defaultTier!, domain);
            t.loot = QualifyAll(t.loot, domain);
            QualifySkills(t.skills, domain);
            if (t.phases != null)
                foreach (var phase in t.phases)
                    if (phase?.actions != null) foreach (var action in phase.actions) QualifyAction(action, domain);
            t.animSet = QualifyOpt(t.animSet, domain);
            t.soundSet = QualifyOpt(t.soundSet, domain);
        }

        internal static void QualifyLootTable(LootTableModel table, string domain)
        {
            QualifyEntries(table.entries, domain);
            QualifyEntries(table.guaranteed, domain);
        }

        internal static void QualifyZone(ZoneModel zone, string domain)
        {
            if (!string.IsNullOrEmpty(zone.faction)) zone.faction = QualifyFaction(zone.faction!, domain);
            if (zone.tiers != null) zone.tiers = QualifyKeys(zone.tiers, id => QualifyTier(id, domain));
            if (zone.spawns == null) return;
            foreach (var spawn in zone.spawns)
            {
                if (spawn == null) continue;
                if (!string.IsNullOrWhiteSpace(spawn.mob)) spawn.mob = IdRules.QualifyKey(spawn.mob!, domain);
                if (!string.IsNullOrWhiteSpace(spawn.group)) spawn.group = IdRules.Qualify(spawn.group!, domain);
                if (!string.IsNullOrEmpty(spawn.tier)) spawn.tier = QualifyTier(spawn.tier!, domain);
            }
        }

        private static List<string>? QualifyAll(List<string>? ids, string domain)
            => ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => IdRules.Qualify(id, domain)).ToList();

        // empty stays as it is
        private static string? QualifyOpt(string? id, string domain) => string.IsNullOrEmpty(id) ? id : IdRules.Qualify(id!, domain);

        // keys that qualify to the same id: the last one wins
        private static Dictionary<string, T> QualifyKeys<T>(Dictionary<string, T> map, System.Func<string, string> qualify)
            => map.Where(p => !string.IsNullOrWhiteSpace(p.Key)).GroupBy(p => qualify(p.Key)).ToDictionary(g => g.Key, g => g.Last().Value);

        private static void QualifySkills(List<SkillRuleModel>? skills, string domain)
        {
            if (skills == null) return;
            foreach (var rule in skills) QualifyAction(rule?.action, domain);
        }

        private static void QualifyAction(SkillActionModel? action, string domain)
        {
            if (action != null && !string.IsNullOrWhiteSpace(action.mob)) action.mob = IdRules.QualifyKey(action.mob!.Trim(), domain);
        }

        private static void QualifyEntries(List<LootEntryModel>? entries, string domain)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.code)) continue;
                // tables default to this mod's domain, items and blocks to game
                entry.code = IdRules.Qualify(entry.code, entry.type == LootEntryModel.Table ? domain : "game");
                entry.tiers = entry.tiers?.Where(tier => !string.IsNullOrWhiteSpace(tier)).Select(tier => QualifyTier(tier, domain)).ToList();
            }
        }

        // built-in factions always belong to canrpgmobs
        private static string QualifyFaction(string id, string domain)
        {
            string trimmed = id.Trim();
            if (trimmed == FactionMatrix.Everyone) return FactionMatrix.Everyone;
            if (!trimmed.Contains(':'))
            {
                string builtIn = IdRules.Qualify(trimmed, canrpgmobsModSystem.ModId);
                if (FactionMatrix.BuiltIns.Contains(builtIn)) return builtIn;
            }
            return IdRules.Qualify(trimmed, domain);
        }

        // same for the normal tier
        private static string QualifyTier(string id, string domain)
        {
            string trimmed = id.Trim();
            return !trimmed.Contains(':') && IdRules.Qualify(trimmed, canrpgmobsModSystem.ModId) == StatMath.DefaultTier
                ? StatMath.DefaultTier
                : IdRules.Qualify(trimmed, domain);
        }

        private static int RemoveRejected(CatalogData data, MobValidator.Rejected rejected)
        {
            int removed = 0;
            foreach (var id in rejected.Races) if (data.Races.Remove(id)) removed++;
            foreach (var id in rejected.Roles) if (data.Roles.Remove(id)) removed++;
            foreach (var id in rejected.Templates) if (data.Templates.Remove(id)) removed++;
            foreach (var id in rejected.AiProfiles) if (data.AiProfiles.Remove(id)) removed++;
            foreach (var id in rejected.Tiers) if (data.Tiers.Remove(id)) removed++;
            foreach (var id in rejected.LootTables) if (data.LootTables.Remove(id)) removed++;
            // nothing refers to zones, no need for another pass
            foreach (var id in rejected.Zones) data.Zones.Remove(id);
            return removed;
        }

        private static void Add<T>(List<T>? items, Dictionary<string, T> into, string kind, string domain, string where,
            ValidationReport report, Action<T>? qualifyRefs) where T : CatalogEntryModel
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (!IdRules.IsClean(item.id))
                {
                    report.Error(where, $"skipped a {kind} with an empty or invalid id '{item.id}' (allowed: letters, digits, : _ - .)");
                    continue;
                }

                item.id = IdRules.Qualify(item.id, domain);
                item.source = where;
                qualifyRefs?.Invoke(item);

                if (into.TryGetValue(item.id, out var earlier))
                    report.Info(where, $"{kind} {item.id} replaces the one from {earlier.source}");
                into[item.id] = item;
            }
        }
    }
}
