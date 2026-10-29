using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Appearance;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canrpgmobs.Catalog
{
    // checks against loaded game assets, warnings only
    public static partial class CatalogLoader
    {
        // server only, the client doesn't have items/blocks yet at this point
        private static void CheckLootCodes(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            foreach (var table in data.LootTables.Values)
            {
                foreach (var entry in (table.entries ?? new List<LootEntryModel>()).Concat(table.guaranteed ?? new List<LootEntryModel>()))
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.code) || entry.type == LootEntryModel.Table) continue;
                    var code = new AssetLocation(entry.code);
                    bool exists = entry.type == LootEntryModel.Block ? api.World.GetBlock(code) != null : api.World.GetItem(code) != null;
                    if (!exists) report.Warning($"{table.source} > lootTable {table.id}", $"{entry.type} '{entry.code}' does not exist, it never drops");
                }
            }
        }

        // server only, like loot codes
        private static void CheckGearCodes(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            CollectibleObject? Find(string code) => MobGear.Find(api.World, IdRules.Qualify(code, "game"));

            void Check(string path, string kind, List<string>? gear, string? right, string? left)
            {
                if (gear is not { Count: > 0 } && string.IsNullOrWhiteSpace(right) && string.IsNullOrWhiteSpace(left)) return;
                if (kind != AppearanceKinds.Plm) report.Warning(path, "gear, rightHand and leftHand only show on humanoid (plm) bodies");
                foreach (var code in gear ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(code) || MobGear.IsNone(code)) continue;
                    var collectible = Find(code);
                    if (collectible == null) report.Warning(path, $"gear '{code}' does not exist, it is not shown");
                    else if (Vintagestory.GameContent.IAttachableToEntity.FromCollectible(collectible) == null) report.Warning(path, $"gear '{code}' is not wearable, it is not shown");
                }
                foreach (var code in new[] { right, left })
                    if (!string.IsNullOrWhiteSpace(code) && !MobGear.IsNone(code) && Find(code!) == null)
                        report.Warning(path, $"held item '{code}' does not exist, it is not shown");
            }

            void CheckLook(string path, string kind, List<string>? gear, string? right, string? left, List<AppearancePresetModel>? presets)
            {
                Check(path, kind, gear, right, left);
                foreach (var preset in presets ?? new List<AppearancePresetModel>())
                    if (preset != null) Check($"{path} > preset {preset.id}", kind, preset.gear, preset.rightHand, preset.leftHand);
            }

            foreach (var race in data.Races.Values)
                if (race.appearance is { } look)
                    CheckLook($"{race.source} > race {race.id}", look.kind, look.gear, look.rightHand, look.leftHand, look.presets);

            // body kind unknown for roles, races and templates get the shape body warning
            foreach (var role in data.Roles.Values)
                Check($"{role.source} > role {role.id}", AppearanceKinds.Plm, role.gear, role.rightHand, role.leftHand);

            foreach (var ai in data.AiProfiles.Values)
            {
                if (ai.ranged is not { } ranged) continue;
                if (Find(ranged.arrow) is not Item arrow)
                    report.Warning($"{ai.source} > aiProfile {ai.id}", $"ranged.arrow '{ranged.arrow}' is not an item, the mob never shoots");
                // entity types may not be registered yet
                else if (api.World.EntityTypes is { Count: > 0 } && api.World.GetEntityType(Runtime.Ai.AiTaskRpgShoot.ArrowEntityCode(arrow)) == null)
                    report.Warning($"{ai.source} > aiProfile {ai.id}", $"ranged.arrow '{ranged.arrow}' has no flying entity, the mob never shoots");
            }

            foreach (var template in data.Templates.Values)
            {
                if (template.appearance is not { } look) continue;
                var race = data.RaceOf(template);
                CheckLook($"{template.source} > mob {template.id}", race?.appearance?.kind ?? AppearanceKinds.Plm, look.gear, look.rightHand, look.leftHand, look.presets);
            }
        }

        // PlayerModelLib builds models in AssetsFinalize, only the server catalog is built late enough
        // per race or mob, all presets together
        private const long MaxLooks = 64;

        private static void CheckPlayerModels(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            var models = api.ModLoader.GetModSystem<PlayerModelLib.CustomModelsSystem>();
            if (models == null || !models.ModelsLoaded) return;

            void Check(string path, string? model, List<AppearancePresetModel>? presets)
            {
                if (string.IsNullOrEmpty(model)) return;
                string code = models.CustomModels.ContainsKey(model!) || model != AppearanceModel.Seraph ? model! : PlayerModelLib.CustomModelsSystem.SeraphModelCode;
                if (!models.CustomModels.TryGetValue(code, out var modelData))
                {
                    int colon = code.IndexOf(':');
                    string domain = colon > 0 ? code.Substring(0, colon) : "";
                    string hint = domain.Length > 0 && !api.ModLoader.IsModEnabled(domain) ? $" (mod '{domain}' is not installed)" : "";
                    report.Warning(path, $"PlayerModelLib model '{code}' does not exist{hint}, these mobs render as seraphs");
                    return;
                }
                if (presets == null) return;

                IReadOnlyList<string>? VariantsOf(string part)
                    => modelData.SkinParts.TryGetValue(part, out var p) ? p.Variants?.Select(v => v.Code).ToList() : null;

                long looks = 0;
                foreach (var preset in presets)
                {
                    if (preset?.parts == null) continue;
                    looks += AppearanceParts.Combinations(preset.parts, VariantsOf);
                    foreach (var (part, value) in preset.parts)
                    {
                        if (!modelData.SkinParts.TryGetValue(part, out var skinPart))
                        {
                            report.Warning(path, $"preset '{preset.id}': model '{code}' has no skin part '{part}' (it has: {string.Join(", ", modelData.SkinParts.Keys)})");
                            continue;
                        }
                        // "*" = any variant, lists are checked one by one
                        if (value.Trim() == AppearanceParts.Any || skinPart.Variants == null) continue;
                        foreach (var variant in AppearanceParts.Options(value))
                            if (!skinPart.Variants.Any(v => v.Code == variant))
                                report.Warning(path, $"preset '{preset.id}': skin part '{part}' of '{code}' has no variant '{variant}' (it has: {string.Join(", ", skinPart.Variants.Select(v => v.Code))})");
                    }
                }
                if (looks > MaxLooks)
                    report.Warning(path, $"random parts give about {looks} different looks; above {MaxLooks} the texture atlas fills up, narrow some lists or drop a '*'");
            }

            foreach (var race in data.Races.Values)
                if (race.appearance?.kind == AppearanceKinds.Plm) Check($"{race.source} > race {race.id}", race.appearance.model, race.appearance.presets);

            foreach (var template in data.Templates.Values)
            {
                var look = template.appearance;
                if (look == null || (look.model == null && look.presets == null)) continue;
                var race = data.RaceOf(template);
                if (race?.appearance != null && race.appearance.kind != AppearanceKinds.Plm) continue;
                Check($"{template.source} > mob {template.id}", look.model ?? race?.appearance?.model ?? AppearanceModel.Seraph, look.presets ?? race?.appearance?.presets);
            }
        }

        // both sides, unqualified paths default to game
        private static void CheckAssets(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            // shape parsing is slow, animation codes are cached per asset
            HashSet<string>? AnimationCodes(string path, string where, bool codesWanted)
            {
                var location = AssetLocation.Create(path, "game").WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                var asset = api.Assets.TryGet(location, true);
                if (asset == null)
                {
                    report.Warning(where, $"shape '{path}' does not exist, these mobs are invisible");
                    return null;
                }
                if (!codesWanted) return null;
                if (shapeAnimations.TryGetValue(asset, out var known)) return ReferenceEquals(known, NoAnimations) ? null : known;

                var animations = Shape.TryGet(api, location)?.Animations;
                var codes = animations == null ? NoAnimations : new HashSet<string>(animations.Select(a => a.Code), StringComparer.OrdinalIgnoreCase);
                shapeAnimations.AddOrUpdate(asset, codes);
                return ReferenceEquals(codes, NoAnimations) ? null : codes;
            }

            void CheckTexture(string? path, string where)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var location = AssetLocation.Create(path!, "game").WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
                if (!api.Assets.Exists(location)) report.Warning(where, $"texture '{path}' does not exist");
            }

            void CheckSound(string? path, string where)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var location = AssetLocation.Create(path!, "game").WithPathPrefixOnce("sounds/");
                bool exists = location.Path.EndsWith("*")
                    ? api.Assets.GetMany(location.Path.TrimEnd('*'), location.Domain, false).Count > 0
                    : api.Assets.Exists(location.WithPathAppendixOnce(".ogg"));
                if (!exists) report.Warning(where, $"sound '{path}' does not exist");
            }

            void CheckTextures(Dictionary<string, string>? textures, List<AppearancePresetModel>? presets, string where)
            {
                if (textures != null) foreach (var texture in textures.Values) CheckTexture(texture, where);
                if (presets == null) return;
                foreach (var preset in presets)
                    if (preset?.textures != null) foreach (var texture in preset.textures.Values) CheckTexture(texture, $"{where} > preset {preset.id}");
            }

            foreach (var race in data.Races.Values)
            {
                string where = $"{race.source} > race {race.id}";
                var look = race.appearance;
                if (look?.kind != AppearanceKinds.Shape || string.IsNullOrWhiteSpace(look.shape)) continue;
                CheckTextures(look.textures, look.presets, where);

                // missing codes make the game log every play attempt
                var codes = AnimationCodes(look.shape!, where, codesWanted: true);
                if (codes == null) continue;
                var anims = MobResolver.FlattenSet<AnimSetModel, AnimEntryModel>(race.animSet, data.AnimSets, data.Aliases, s => s.extends, s => s.anims);
                foreach (var (key, entry) in anims)
                    if (!string.IsNullOrWhiteSpace(entry.animation) && !codes.Contains(entry.animation))
                        report.Warning(where, $"animation '{entry.animation}' (key {key}) is not in shape '{look.shape}'");
            }

            foreach (var template in data.Templates.Values)
            {
                var look = template.appearance;
                if (look == null) continue;
                string where = $"{template.source} > mob {template.id}";
                if (!string.IsNullOrWhiteSpace(look.shape)) AnimationCodes(look.shape!, where, codesWanted: false);
                CheckTextures(look.textures, look.presets, where);
            }

            foreach (var set in data.SoundSets.Values)
            {
                if (set.sounds == null) continue;
                foreach (var (key, entry) in set.sounds) CheckSound(entry?.path, $"{set.source} > soundSet {set.id} > sound {key}");
            }
            foreach (var role in data.Roles.Values)
            {
                if (role.soundOverrides == null) continue;
                foreach (var (key, entry) in role.soundOverrides) CheckSound(entry?.path, $"{role.source} > role {role.id} > sound {key}");
            }
        }

        private static void CheckArchers(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            bool Shoots(string? code)
            {
                if (string.IsNullOrWhiteSpace(code) || MobGear.IsNone(code)) return false;
                var location = new AssetLocation(IdRules.Qualify(code!, "game"));
                return Runtime.Ai.AiTaskRpgShoot.IsRangedWeapon(api.World.GetItem(location));
            }

            bool IsArcher(string? profileId)
                => profileId != null && data.AiProfiles.TryGetValue(MobResolver.ResolveAlias(profileId, data.Aliases), out var ai)
                   && ai.archetype == MobValidator.ArcherArchetype;

            foreach (var template in data.Templates.Values)
            {
                var race = data.RaceOf(template);
                data.Roles.TryGetValue(MobResolver.ResolveAlias(template.role, data.Aliases), out var role);
                if (!IsArcher(template.aiProfile ?? role?.aiProfile)) continue;
                if (!Shoots(template.appearance?.rightHand ?? role?.rightHand ?? race?.appearance?.rightHand))
                    report.Warning($"{template.source} > mob {template.id}",
                        "archetype rangedArcher, but the right hand holds no bow, crossbow, sling, javelin or spear: this mob never shoots");
            }

            // race|role pairs have no entry, so report on the role once
            foreach (var role in data.Roles.Values)
            {
                if (!IsArcher(role.aiProfile)) continue;
                string where = $"{role.source} > role {role.id}";
                if (role.rightHand != null)
                {
                    if (!Shoots(role.rightHand))
                        report.Warning(where, $"archetype rangedArcher with rightHand '{role.rightHand}': that is nothing to shoot with, mobs of this role never shoot");
                    continue;
                }
                var bare = data.Races.Values.Where(r => !Shoots(r.appearance?.rightHand)).Select(r => r.id).Take(4).ToList();
                if (bare.Count > 0)
                    report.Info(where, $"archetype rangedArcher without a rightHand of its own: paired with {string.Join(", ", bare)} the mob has nothing to shoot with");
            }
        }

        private static void CheckExtensions(ICoreAPI api, CatalogData data, ValidationReport report)
        {
            IEnumerable<CatalogEntryModel> entries = data.Races.Values;
            entries = entries.Concat(data.Roles.Values).Concat(data.Templates.Values).Concat(data.AiProfiles.Values)
                .Concat(data.Factions.Values).Concat(data.Tiers.Values).Concat(data.LootTables.Values)
                .Concat(data.AnimSets.Values).Concat(data.SoundSets.Values).Concat(data.Groups.Values).Concat(data.Zones.Values);

            foreach (var entry in entries)
            {
                if (entry.extensions == null) continue;
                foreach (var domain in entry.extensions.Keys)
                    if (!api.ModLoader.IsModEnabled(domain))
                        report.Info(MobValidator.Path(entry.source, "entry", entry.id), $"extensions.{domain}: mod '{domain}' is not installed, the data is kept but nothing reads it");
            }
        }

        // lang files are loaded on both sides by now
        private static void CheckLangKeys(CatalogData data, ValidationReport report)
        {
            void Check(LocText? text, string path)
            {
                if (text?.Key is not { } key || Lang.HasTranslation(key, false, false)) return;
                report.Warning(path, $"lang key '{key}' is missing from the lang files, " + (text.Texts.Any() ? "the inline text is shown" : "the id is shown"));
            }

            foreach (var race in data.Races.Values)
            {
                Check(race.name, $"{race.source} > race {race.id} > name");
                Check(race.nameFormat, $"{race.source} > race {race.id} > nameFormat");
            }
            foreach (var role in data.Roles.Values) Check(role.name, $"{role.source} > role {role.id} > name");
            foreach (var template in data.Templates.Values) Check(template.name, $"{template.source} > mob {template.id} > name");
            foreach (var faction in data.Factions.Values) Check(faction.name, $"{faction.source} > faction {faction.id} > name");
            foreach (var tier in data.Tiers.Values)
            {
                Check(tier.name, $"{tier.source} > tier {tier.id} > name");
                Check(tier.nameFormat, $"{tier.source} > tier {tier.id} > nameFormat");
            }
            foreach (var template in data.Templates.Values)
                foreach (var phase in template.phases ?? new List<BossPhaseModel>())
                    if (phase != null) Check(phase.announce, $"{template.source} > mob {template.id} > phase {phase.id} > announce");
            foreach (var group in data.Groups.Values) Check(group.name, $"{group.source} > group {group.id} > name");
            foreach (var zone in data.Zones.Values) Check(zone.name, $"{zone.source} > zone {zone.id} > name");
        }
    }
}
