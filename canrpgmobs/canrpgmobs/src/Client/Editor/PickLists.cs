using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using canrpgclasses;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json.Linq;
using PlayerModelLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using EffectsHud = effectshud.src.effectshud;

namespace canrpgmobs.Client.Editor
{
    public sealed record PickOption(string Value, string Label);

    // Suggestions for pick fields. Fields still accept any typed value.
    public static class PickLists
    {
        private static List<PickOption>? sounds;
        private static List<PickOption>? shapes;
        private static List<PickOption>? textures;
        // cached until the catalog reloads / the model changes; callers must not modify these lists
        private static MobCatalog? mobKeysOf;
        private static List<PickOption>? mobKeys;
        private static readonly ConditionalWeakTable<CustomModelData, List<PickOption>> plmParts = new();

        private static readonly string[] Activities =
            { "idle", "move", "sprintmode", "sneakmode", "fly", "swim", "jump", "fall", "climb", "floorsitting", "dead", "glide", "mounted" };

        public static List<PickOption> Get(string list, ICoreClientAPI capi, canrpgmobsModSystem mod, EditorSession session, string? contextKey)
        {
            var data = mod.Catalog.Data;
            // cached lists, returned without copying
            switch (list)
            {
                case "sounds": return sounds ??= SoundAssets(capi);
                case "shapes": return shapes ??= Assets(capi, "shapes/entity/", ".json", "shapes/");
                case "textures": return textures ??= Assets(capi, "textures/entity/", ".png", "textures/");
                case "mobKeys": return MobKeys(mod);
                case "plmParts": return PlmPartOptions(capi, mod, session);
            }

            IEnumerable<PickOption> options = list switch
            {
                "races" => Ids(data.Races.Keys),
                "roles" => Ids(data.Roles.Keys),
                "racesNamed" => Named(data.Races.Select(p => (p.Key, p.Value.name))),
                "rolesNamed" => Named(data.Roles.Select(p => (p.Key, p.Value.name))),
                "mobsNamed" => Named(data.Templates.Select(p => (p.Key, p.Value.name))),
                "aiProfilesNamed" => data.AiProfiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PickOption(p.Key, $"{p.Key}  -  {ArchetypeHelp(p.Value.archetype)}")),
                "aiProfiles" => Ids(data.AiProfiles.Keys),
                "tiers" => Ids(data.Tiers.Keys.Append(StatMath.DefaultTier).Distinct()),
                "lootTables" => Ids(data.LootTables.Keys),
                "animSets" => Ids(data.AnimSets.Keys),
                "soundSets" => Ids(data.SoundSets.Keys),
                "factions" => Ids(Factions(data)),
                "factionsAny" => Ids(Factions(data).Prepend(FactionMatrix.Everyone)),
                "archetypes" => Vocabulary(MobsApi.Vocabulary.Archetypes),
                "selectors" => Vocabulary(MobsApi.Vocabulary.Selectors),
                "conditions" => Vocabulary(MobsApi.Vocabulary.Conditions),
                "enemySelectors" => Vocabulary(MobsApi.Vocabulary.EnemySelectors),
                "conditionsNamed" => VocabularyNamed(MobsApi.Vocabulary.Conditions, "condition"),
                "actionsNamed" => VocabularyNamed(MobsApi.Vocabulary.Actions, "action"),
                "spells" => Spells(mod),
                "effects" => Ids(capi.ModLoader.GetModSystem<EffectsHud>()?.effects?.Keys ?? Enumerable.Empty<string>()),
                "damageTypes" => Ids(Enum.GetNames(typeof(EnumDamageType))),
                "activities" => Ids(Activities),
                "animKeys" => Ids(AnimKeys.All),
                "resistKeys" => Ids(StatMath.ResistKeys),
                "shapeAnims" => Ids(ShapeAnimations(capi, mod, session)),
                "plmModels" => Ids(capi.ModLoader.GetModSystem<CustomModelsSystem>()?.CustomModels.Keys ?? Enumerable.Empty<string>()),
                "plmVariants" => Ids(PlmVariants(capi, mod, session, contextKey)),
                "lootCodes" => LootCodes(capi, data, session),
                "actions" => Vocabulary(MobsApi.Vocabulary.Actions),
                "groups" => Ids(data.Groups.Keys),
                "controls" => Ids(ControlNames.All),
                _ => Enumerable.Empty<PickOption>()
            };
            return options.ToList();
        }

        // templates + all race|role pairs, can be thousands
        private static List<PickOption> MobKeys(canrpgmobsModSystem mod)
        {
            if (ReferenceEquals(mod.Catalog, mobKeysOf) && mobKeys != null) return mobKeys;
            var data = mod.Catalog.Data;
            mobKeysOf = mod.Catalog;
            return mobKeys = Ids(data.Templates.Keys
                .Concat(data.Races.Keys.SelectMany(race => data.Roles.Keys.Select(role => race + IdRules.KeySeparator + role)))).ToList();
        }

        private static List<PickOption> PlmPartOptions(ICoreClientAPI capi, canrpgmobsModSystem mod, EditorSession session)
        {
            var model = PlmModel(capi, mod, session);
            if (model == null) return new List<PickOption>();
            if (plmParts.TryGetValue(model, out var known)) return known;
            var options = Ids(model.SkinParts.Keys).ToList();
            plmParts.AddOrUpdate(model, options);
            return options;
        }

        private static IEnumerable<PickOption> Ids(IEnumerable<string> ids)
            => ids.Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).Select(id => new PickOption(id, id));

        // simple mode: "Name  (id)"
        private static IEnumerable<PickOption> Named(IEnumerable<(string id, LocText? name)> entries)
            => entries.Select(e => (e.id, label: e.name?.Resolve(Lang.CurrentLocale, "") ?? ""))
                .OrderBy(e => e.label.Length > 0 ? e.label : e.id, StringComparer.OrdinalIgnoreCase)
                .Select(e => new PickOption(e.id, e.label.Length > 0 ? $"{e.label}  ({e.id})" : e.id));

        // name from lang (vocab-<kind>-<id>), falls back to the id for add-on entries
        private static IEnumerable<PickOption> VocabularyNamed(IReadOnlyDictionary<string, VocabularyEntry> entries, string kind)
            => entries.Values.Select(e =>
            {
                string key = $"canrpgmobs:vocab-{kind}-{e.Id}";
                string name = Lang.HasTranslation(key, false, false) ? Lang.Get(key) : e.Id;
                return new PickOption(e.Id, string.IsNullOrEmpty(e.Help) ? name : $"{name}  -  {e.Help}");
            }).OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase);

        private static string ArchetypeHelp(string archetype)
            => MobsApi.Vocabulary.Archetypes.TryGetValue(archetype, out var entry) && !string.IsNullOrEmpty(entry.Help) ? $"{archetype}: {entry.Help}" : archetype;

        private static IEnumerable<string> Factions(CatalogData data) => FactionMatrix.WithBuiltIns(data.Factions.Keys);

        private static IEnumerable<PickOption> Vocabulary(IReadOnlyDictionary<string, VocabularyEntry> entries)
            => entries.Values.OrderBy(e => e.Id).Select(e => new PickOption(e.Id, string.IsNullOrEmpty(e.Help) ? e.Id : $"{e.Id}  -  {e.Help}"));

        // tagged with how well the spell works on an NPC
        private static IEnumerable<PickOption> Spells(canrpgmobsModSystem mod)
        {
            var core = canrpgclassesModSystem.ClientInstance;
            if (core == null) yield break;
            foreach (var spell in core.Spells.All.Values.OrderBy(s => s.Id))
                yield return new PickOption(spell.Id, $"{SpellTag(mod, spell.Id)} {spell.Id}  {spell.DisplayName}");
        }

        public static string SpellTag(canrpgmobsModSystem mod, string? spellId)
        {
            if (string.IsNullOrWhiteSpace(spellId)) return "";
            return mod.Catalog.RateSpell(spellId!, out _) switch
            {
                SpellSupport.Supported => "[OK]",
                SpellSupport.Degraded => "[~]",
                _ => "[X]"
            };
        }

        private static List<PickOption> SoundAssets(ICoreClientAPI capi)
        {
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in capi.Assets.GetMany("sounds/", null, false))
            {
                var location = asset.Location;
                if (!location.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) continue;
                string path = location.Path.Substring("sounds/".Length, location.Path.Length - "sounds/".Length - ".ogg".Length);
                string full = location.Domain == "game" ? path : $"{location.Domain}:{path}";
                paths.Add(full);
                // growl1, growl2 -> also offer growl*
                string stem = full.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (stem.Length < full.Length) paths.Add(stem + "*");
            }
            return paths.Select(p => new PickOption(p, p)).ToList();
        }

        private static List<PickOption> Assets(ICoreClientAPI capi, string prefix, string extension, string strip)
        {
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in capi.Assets.GetMany(prefix, null, false))
            {
                var location = asset.Location;
                if (!location.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                paths.Add($"{location.Domain}:{location.Path.Substring(strip.Length, location.Path.Length - strip.Length - extension.Length)}");
            }
            return paths.Select(p => new PickOption(p, p)).ToList();
        }

        private static IEnumerable<PickOption> LootCodes(ICoreClientAPI capi, CatalogData data, EditorSession session)
        {
            string type = session.Current?.Token["type"] is JValue value ? value.ToString() : LootEntryModel.Item;
            if (type == LootEntryModel.Table) return Ids(data.LootTables.Keys);
            IEnumerable<AssetLocation?> codes = type == LootEntryModel.Block
                ? capi.World.Blocks.Select(b => b?.Code)
                : capi.World.Items.Select(i => i?.Code);
            return Ids(codes.Where(c => c != null).Select(c => c!.Domain == "game" ? c.Path : c.ToString()).Distinct());
        }

        // own appearance, then the race's, then a race using this anim set
        private static IEnumerable<string> ShapeAnimations(ICoreClientAPI capi, canrpgmobsModSystem mod, EditorSession session)
        {
            string? shapePath = FindLook(mod, session, "shape");
            Shape? shape = null;
            if (shapePath != null)
                shape = Shape.TryGet(capi, AssetLocation.Create(shapePath, "game").WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
            shape ??= capi.World.GetEntityType(new AssetLocation(MobBodies.EntityCode(MobBodies.Humanoid)!))?.Client?.LoadedShape;
            return shape?.Animations?.Select(a => a.Code).Where(c => !string.IsNullOrEmpty(c)).Distinct() ?? Enumerable.Empty<string>();
        }

        private static IEnumerable<string> PlmVariants(ICoreClientAPI capi, canrpgmobsModSystem mod, EditorSession session, string? part)
        {
            if (part == null || PlmModel(capi, mod, session)?.SkinParts.TryGetValue(part, out var skinPart) != true) return Enumerable.Empty<string>();
            return skinPart!.Variants?.Select(v => v.Code) ?? Enumerable.Empty<string>();
        }

        private static CustomModelData? PlmModel(ICoreClientAPI capi, canrpgmobsModSystem mod, EditorSession session)
        {
            var models = capi.ModLoader.GetModSystem<CustomModelsSystem>();
            if (models == null) return null;
            string model = FindLook(mod, session, "model") ?? AppearanceModel.Seraph;
            if (models.CustomModels.TryGetValue(model, out var data)) return data;
            return models.CustomModels.TryGetValue(CustomModelsSystem.SeraphModelCode, out data) ? data : null;
        }

        // nav path first, then the draft's race
        private static string? FindLook(canrpgmobsModSystem mod, EditorSession session, string key)
        {
            for (int i = session.Stack.Count - 1; i >= 0; i--)
            {
                var token = session.Stack[i].Token;
                if (token[key] is JValue { Type: JTokenType.String } own && ((string?)own)?.Length > 0) return (string?)own;
                if (token["appearance"] is JObject appearance && appearance[key] is JValue { Type: JTokenType.String } nested) return (string?)nested;
            }

            var data = mod.Catalog.Data;
            string draftId = JsonDraft.Q(session.DraftId);
            RaceModel? race = null;
            if (session.Section == "mobs" && session.Draft?["race"] is JValue raceId)
                data.Races.TryGetValue(JsonDraft.Q(raceId.ToString()), out race);
            else if (session.Section == "animSets")
                race = data.Races.Values.FirstOrDefault(r => r.animSet == draftId && r.appearance?.kind == AppearanceKinds.Shape);
            if (race?.appearance == null) return null;
            return key == "shape" ? race.appearance.shape : race.appearance.model;
        }
    }
}
