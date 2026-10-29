using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Data
{
    // errors reject the entry, warnings keep it
    public static partial class MobValidator
    {
        public const int MaxPresets = 8;
        public const string DefaultArchetype = "meleeBrute";
        public const string CustomArchetype = "custom";
        public const string ArcherArchetype = "rangedArcher";
        private static readonly string[] BlendModes = { "Average", "Add", "AddAverage" };
        private static readonly string[] EntityActivities =
            { "idle", "move", "sprintmode", "sneakmode", "fly", "swim", "jump", "fall", "climb", "floorsitting", "dead", "glide", "mounted" };

        public sealed class Rejected
        {
            public HashSet<string> Races { get; } = new();
            public HashSet<string> Roles { get; } = new();
            public HashSet<string> Templates { get; } = new();
            public HashSet<string> AiProfiles { get; } = new();
            public HashSet<string> Tiers { get; } = new();
            public HashSet<string> LootTables { get; } = new();
            public HashSet<string> Zones { get; } = new();
        }

        private sealed class Scope
        {
            public Scope(CatalogData data, ISpellInfoSource spells, RuleVocabulary vocabulary, ValidationReport report)
            {
                Data = data;
                Spells = spells;
                Vocabulary = vocabulary;
                Report = report;
            }

            public CatalogData Data { get; }
            public ISpellInfoSource Spells { get; }
            public RuleVocabulary Vocabulary { get; }
            public ValidationReport Report { get; }
        }

        public static Rejected Validate(CatalogData data, ISpellInfoSource spells, ICollection<string> damageTypes, RuleVocabulary vocabulary, ValidationReport report)
        {
            var rejected = new Rejected();
            var scope = new Scope(data, spells, vocabulary, report);
            var factions = new FactionMatrix(data.Factions);

            foreach (var faction in data.Factions.Values) CheckFaction(faction, factions, report);
            foreach (var tier in data.Tiers.Values) CheckTier(tier, report, rejected);
            if (data.Scaling != null) CheckScaling(data.Scaling, report);
            foreach (var table in data.LootTables.Values) CheckLootTable(table, data, report, rejected);
            CheckLootCycles(data, report);
            foreach (var set in data.AnimSets.Values) CheckAnimSet(set, data, report);
            foreach (var set in data.SoundSets.Values) CheckSoundSet(set, data, report);
            foreach (var ai in data.AiProfiles.Values) CheckAi(ai, damageTypes, scope, rejected);
            foreach (var race in data.Races.Values)
            {
                CheckRace(race, scope, rejected);
                CheckFactionRef(race.faction, Path(race.source, "race", race.id), factions, report);
            }
            foreach (var role in data.Roles.Values) CheckRole(role, scope, rejected);
            foreach (var template in data.Templates.Values)
            {
                CheckTemplate(template, scope, rejected);
                CheckFactionRef(template.faction, Path(template.source, "mob", template.id), factions, report);
            }
            foreach (var group in data.Groups.Values) CheckGroup(group, scope, factions);
            foreach (var zone in data.Zones.Values) CheckZone(zone, scope, factions, rejected);
            CheckZoneOverlaps(data, rejected, report);
            CheckAliases(data, report);
            return rejected;
        }

        private static void CheckRace(RaceModel race, Scope scope, Rejected rejected)
        {
            var report = scope.Report;
            string path = Path(race.source, "race", race.id);
            bool bad = false;

            if (MobBodies.Get(race.body) == null) { report.Error(path, $"unknown body '{race.body}', expected one of: {string.Join(", ", MobBodies.All.Keys)}"); bad = true; }

            var look = race.appearance;
            if (look == null)
            {
                if (MobBodies.AppearanceKind(race.body) == AppearanceKinds.Shape)
                {
                    report.Error(path, $"body {race.body} needs an appearance with kind {AppearanceKinds.Shape}");
                    bad = true;
                }
                else report.Info(path, "no appearance, the default seraph model is used");
            }
            else bad |= CheckAppearance(look, race.body, path, report);

            var s = race.stats;
            if (s == null) { report.Error(path, "stats is missing"); bad = true; }
            else
            {
                if (!Positive(s.maxHealth)) { report.Error(path, "stats.maxHealth must be a number above 0"); bad = true; }
                if (!NonNegative(s.meleeDamage)) { report.Error(path, "stats.meleeDamage must be 0 or more"); bad = true; }
                if (!InRange(s.moveSpeed, 0f, 0.2f)) { report.Error(path, "stats.moveSpeed must be between 0 and 0.2"); bad = true; }
                if (s.meleeTier < 0 || s.meleeTier > 10) report.Warning(path, "stats.meleeTier is usually between 0 and 10");
                bad |= CheckStatExtras(s.spellPower, s.healingPower, s.damageReduction, s.resist, path, report);
            }

            CheckText(race.name, path + " > name", report);
            CheckText(race.nameFormat, path + " > nameFormat", report);
            CheckSkills(race.skills, path, scope);
            CheckLootRefs(race.loot, path, scope);
            CheckSetRefs(race.animSet, race.soundSet, path, scope);
            if (bad) rejected.Races.Add(race.id);
        }

        private static void CheckRole(RoleModel role, Scope scope, Rejected rejected)
        {
            var report = scope.Report;
            string path = Path(role.source, "role", role.id);
            bool bad = false;

            var mul = role.statMul;
            if (mul != null && (!Positive(mul.maxHealth) || !NonNegative(mul.meleeDamage) || !NonNegative(mul.moveSpeed)
                                || !Positive(mul.spellPower) || !NonNegative(mul.healingPower)))
            {
                report.Error(path, "statMul values must be numbers, maxHealth and spellPower above 0, the rest 0 or more");
                bad = true;
            }
            var add = role.statAdd;
            if (add != null && (!float.IsFinite(add.maxHealth) || !float.IsFinite(add.meleeDamage) || !float.IsFinite(add.moveSpeed)
                                || !float.IsFinite(add.spellPower) || !float.IsFinite(add.healingPower) || !float.IsFinite(add.damageReduction)
                                || (add.resist != null && add.resist.Values.Any(v => !float.IsFinite(v)))))
            {
                report.Error(path, "statAdd values must be numbers");
                bad = true;
            }
            if (!string.IsNullOrEmpty(role.aiProfile) && !scope.Data.AiProfiles.ContainsKey(MobResolver.ResolveAlias(role.aiProfile!, scope.Data.Aliases)))
                report.Warning(path, $"unknown aiProfile '{role.aiProfile}', the default melee profile is used");

            CheckText(role.name, path + " > name", report);
            CheckSkills(role.skills, path, scope);
            CheckLootRefs(role.loot, path, scope);
            // same "anim key" path as sets, the editor's jump-to-problem relies on it
            CheckAnimEntries(role.animOverrides, path, report);
            CheckSoundEntries(role.soundOverrides, path, report);
            if (bad) rejected.Roles.Add(role.id);
        }

        private static void CheckTemplate(MobTemplateModel t, Scope scope, Rejected rejected)
        {
            var report = scope.Report;
            var data = scope.Data;
            string path = Path(t.source, "mob", t.id);
            bool bad = false;

            if (!data.Races.ContainsKey(MobResolver.ResolveAlias(t.race, data.Aliases))) { report.Error(path, $"unknown race '{t.race}'"); bad = true; }
            if (!data.Roles.ContainsKey(MobResolver.ResolveAlias(t.role, data.Aliases))) { report.Error(path, $"unknown role '{t.role}'"); bad = true; }
            if (!string.IsNullOrEmpty(t.aiProfile) && !data.AiProfiles.ContainsKey(MobResolver.ResolveAlias(t.aiProfile!, data.Aliases)))
                report.Warning(path, $"unknown aiProfile '{t.aiProfile}', the role's profile is used");

            if (t.levelRange != null && !IntRange(t.levelRange, 1, 1000))
            {
                report.Error(path, "levelRange must be [min, max] between 1 and 1000, with min not above max");
                bad = true;
            }

            var s = t.stats;
            if (s != null && ((s.maxHealth is { } hp && !Positive(hp)) || (s.meleeDamage is { } dmg && !NonNegative(dmg))
                              || (s.moveSpeed is { } speed && !InRange(speed, 0f, 0.2f))))
            {
                report.Error(path, "stats overrides must be valid numbers (maxHealth above 0, moveSpeed 0 to 0.2)");
                bad = true;
            }

            if (s != null) bad |= CheckStatExtras(s.spellPower, s.healingPower, s.damageReduction, s.resist, path, report);
            if (UnknownTier(data, t.defaultTier))
                report.Warning(path, $"unknown defaultTier '{t.defaultTier}', normal is used");
            if (Array.IndexOf(MergeModes.All, t.lootMode) < 0)
                report.Warning(path, $"lootMode '{t.lootMode}' is not append or replace, append is used");

            var look = t.appearance;
            if (look != null)
            {
                string raceKind = data.RaceOf(t)?.appearance?.kind ?? AppearanceKinds.Plm;
                bad |= CheckAppearanceOverride(look, raceKind, path, report);
            }

            if (Array.IndexOf(MergeModes.All, t.skillsMode) < 0)
                report.Warning(path, $"skillsMode '{t.skillsMode}' is not append or replace, append is used");

            CheckText(t.name, path + " > name", report);
            CheckSkills(t.skills, path, scope);
            CheckPhases(t.phases, path, scope);
            CheckLootRefs(t.loot, path, scope);
            CheckSetRefs(t.animSet, t.soundSet, path, scope);
            if (bad) rejected.Templates.Add(t.id);
        }

        // only warnings, bad members are skipped
        private static void CheckGroup(SpawnGroupModel group, Scope scope, FactionMatrix factions)
        {
            var report = scope.Report;
            string path = Path(group.source, "group", group.id);
            CheckText(group.name, path + " > name", report);
            if (!InRange(group.radius, 0f, 32f)) report.Warning(path, "radius should be between 0 and 32 blocks");
            CheckFactionRef(group.faction, path, factions, report);
            if (group.members is not { Count: > 0 })
            {
                report.Warning(path, "no members, the group spawns nothing");
                return;
            }

            for (int i = 0; i < group.members.Count; i++)
            {
                var member = group.members[i];
                if (member == null) continue;
                string at = $"{path} > members[{i}] {member.mob}";
                if (string.IsNullOrWhiteSpace(member.mob))
                {
                    report.Warning(at, "mob is empty, the member is skipped");
                    continue;
                }
                if (MobResolver.Resolve(member.mob, scope.Data, null, out var error) == null) report.Warning(at, $"mob '{member.mob}' does not resolve: {error}");
                if (member.count != null && !IntRange(member.count, 0, 50))
                    report.Warning(at, "count must be [min, max] between 0 and 50, with min not above max");
                if (UnknownTier(scope.Data, member.tier))
                    report.Warning(at, $"unknown tier '{member.tier}', the member is not spawned");
                if (member.level < 0 || member.level > 1000) report.Warning(at, "level must be between 0 (the spawner's) and 1000");
            }
        }

        private static void CheckAi(AiProfileModel ai, ICollection<string> damageTypes, Scope scope, Rejected rejected)
        {
            var report = scope.Report;
            string path = Path(ai.source, "aiProfile", ai.id);
            bool bad = false;

            var archetypes = scope.Vocabulary.Archetypes;
            if (!archetypes.ContainsKey(ai.archetype ?? ""))
                report.Warning(path, $"unknown archetype '{ai.archetype}', behaves as {DefaultArchetype} (known: {Known(archetypes)})");
            if (!InRange(ai.aggroRange, 1f, 64f)) { report.Error(path, "aggroRange must be between 1 and 64"); bad = true; }
            if (!InRange(ai.leashRange, 0f, 256f)) { report.Error(path, "leashRange must be between 0 (off) and 256"); bad = true; }
            else if (ai.leashRange > 0f && ai.leashRange < ai.aggroRange)
                report.Warning(path, "leashRange is shorter than aggroRange, the mob gives up chases it starts");
            if (!InRange(ai.callForHelpRange, 0f, 64f)) { report.Error(path, "callForHelpRange must be between 0 (off) and 64"); bad = true; }

            var band = ai.preferredRange;
            if (band != null && (band.Length != 2 || !NonNegative(band[0]) || !float.IsFinite(band[1]) || band[1] <= band[0] || band[1] > 48f))
            {
                report.Error(path, "preferredRange must be [min, max] between 0 and 48, with min below max");
                bad = true;
            }
            // 1 itself is excluded
            if (!float.IsFinite(ai.fleeBelowHp) || ai.fleeBelowHp < 0f || ai.fleeBelowHp >= 1f) { report.Error(path, "fleeBelowHp must be 0 (flee only when feared) or a fraction below 1"); bad = true; }
            if (!InRange(ai.followRange, 1f, 32f)) { report.Error(path, "followRange must be between 1 and 32"); bad = true; }

            if (ai.targeting != null) CheckTargeting(ai.targeting, scope.Vocabulary, path, report);
            if (ai.archetype == CustomArchetype)
            {
                if (ai.tasks is not { Count: > 0 }) { report.Error(path, "archetype custom needs a tasks list"); bad = true; }
                else
                {
                    for (int i = 0; i < ai.tasks.Count; i++)
                        if (ai.tasks[i]?["code"]?.Type != JTokenType.String) report.Warning(path, $"tasks[{i}] has no code and is skipped");
                }
            }
            if (archetypes.TryGetValue(ai.archetype ?? "", out var entry)) CheckUnreadBlocks(ai, entry, path, report);
            if (ai.melee != null) bad |= CheckMelee(ai.melee, damageTypes, path, report);
            if (ai.ranged != null) bad |= CheckRanged(ai.ranged, damageTypes, path, report);
            if (bad) rejected.AiProfiles.Add(ai.id);
        }

        private static void CheckTargeting(TargetingModel targeting, RuleVocabulary vocabulary, string path, ValidationReport report)
        {
            if (!vocabulary.EnemySelectors.ContainsKey(targeting.selector ?? ""))
                report.Warning(path, $"targeting.selector '{targeting.selector}' is not one of: {Known(vocabulary.EnemySelectors)}, threat is used");
            if (!InRange(targeting.switchCooldown, 0f, 30f))
                report.Warning(path, "targeting.switchCooldown should be between 0 and 30 seconds");
        }

        // blocks the archetype doesn't read
        private static void CheckUnreadBlocks(AiProfileModel ai, VocabularyEntry archetype, string path, ValidationReport report)
        {
            (string name, bool written)[] blocks =
            {
                ("tasks", ai.tasks is { Count: > 0 }), ("melee", ai.melee != null), ("ranged", ai.ranged != null),
                ("targeting", ai.targeting != null), ("preferredRange", ai.preferredRange != null)
            };
            foreach (var (name, written) in blocks)
                if (written && !archetype.Params.Any(p => p.Name == name))
                    report.Info(path, $"{name} is not read by archetype {ai.archetype}, it is ignored");
        }

        // true = unusable
        private static bool CheckMelee(MeleeModel m, ICollection<string> damageTypes, string path, ValidationReport report)
        {
            bool bad = false;
            if (!NonNegative(m.damageMul)) { report.Error(path, "melee.damageMul must be 0 or more"); bad = true; }
            if (!damageTypes.Contains(m.damageType)) { report.Error(path, $"melee.damageType '{m.damageType}' is not a game damage type"); bad = true; }
            if (m.attackDurationMs <= 0) { report.Error(path, "melee.attackDurationMs must be above 0"); bad = true; }
            if (m.hitAtMs < 0 || m.hitAtMs > m.attackDurationMs) report.Warning(path, "melee.hitAtMs should be between 0 and attackDurationMs");
            if (m.cooldownMs != null && !IntRange(m.cooldownMs, 0, int.MaxValue))
                report.Warning(path, "melee.cooldownMs must be [min, max], the default is used");
            if (!InRange(m.reach, 0f, 8f)) report.Warning(path, "melee.reach should be between 0 (take it from the weapon) and 8 blocks");
            return bad;
        }

        private static bool CheckRanged(RangedModel r, ICollection<string> damageTypes, string path, ValidationReport report)
        {
            bool bad = false;
            if (!NonNegative(r.damageMul)) { report.Error(path, "ranged.damageMul must be 0 or more"); bad = true; }
            if (!damageTypes.Contains(r.damageType)) { report.Error(path, $"ranged.damageType '{r.damageType}' is not a game damage type"); bad = true; }
            if (string.IsNullOrWhiteSpace(r.arrow)) { report.Error(path, "ranged.arrow is empty"); bad = true; }
            if (r.aimMs <= 0) { report.Error(path, "ranged.aimMs must be above 0"); bad = true; }
            if (!InRange(r.speed, 1f, 100f)) report.Warning(path, "ranged.speed should be between 1 and 100 blocks per second");
            if (!InRange(r.dispersion, 0f, 45f)) report.Warning(path, "ranged.dispersion should be between 0 and 45 degrees");
            if (!InRange(r.minRange, 0f, 32f)) report.Warning(path, "ranged.minRange should be between 0 and 32 blocks");
            if (r.cooldownMs != null && !IntRange(r.cooldownMs, 0, int.MaxValue))
                report.Warning(path, "ranged.cooldownMs must be [min, max], the default is used");
            if (!InRange(r.gravityFactor, 0f, 5f)) report.Warning(path, "ranged.gravityFactor should be between 0 (flies straight) and 5");
            return bad;
        }

        // true = unusable
        private static bool CheckAppearance(AppearanceModel look, string body, string path, ValidationReport report)
        {
            bool bad = false;
            string expected = MobBodies.AppearanceKind(body);
            if (look.kind != expected) { report.Error(path, $"body {body} needs appearance.kind '{expected}', not '{look.kind}'"); bad = true; }
            if (look.kind == AppearanceKinds.Shape && string.IsNullOrWhiteSpace(look.shape)) { report.Error(path, "appearance.shape is missing"); bad = true; }
            bad |= CheckLookFields(look.kind, look.model, look.textures, look.size, look.hitbox, look.eyeHeight, look.presets, path, report);
            return bad;
        }

        private static bool CheckAppearanceOverride(AppearanceOverrideModel look, string raceKind, string path, ValidationReport report)
        {
            if (raceKind == AppearanceKinds.Plm && (look.shape != null || look.textures != null || look.hitbox != null || look.eyeHeight != null))
                report.Warning(path, "appearance shape, textures, hitbox and eyeHeight only apply to shape bodies; the race uses a PlayerModelLib model");
            if (raceKind == AppearanceKinds.Shape && look.model != null)
                report.Warning(path, "appearance.model only applies to PlayerModelLib bodies; the race uses a shape");
            return CheckLookFields(raceKind, look.model, look.textures, look.size ?? 1f, look.hitbox, look.eyeHeight, look.presets, path, report);
        }

        private static bool CheckLookFields(string kind, string? model, Dictionary<string, string>? textures, float size, float[]? hitbox, float? eyeHeight,
            List<AppearancePresetModel>? presets, string path, ValidationReport report)
        {
            bool bad = false;
            if (kind == AppearanceKinds.Plm && model != null)
            {
                if (model.Trim().Length == 0) { report.Error(path, "appearance.model is empty"); bad = true; }
                else if (model != AppearanceModel.Seraph && !model.Contains(':'))
                    report.Warning(path, $"appearance.model '{model}' has no domain; PlayerModelLib registers models as domain:code, so this will fall back to seraph");
            }
            if (!InRange(size, 0.1f, 5f)) { report.Error(path, "appearance.size must be between 0.1 and 5"); bad = true; }
            if (hitbox != null && (hitbox.Length != 2 || !InRange(hitbox[0], 0.1f, 8f) || !InRange(hitbox[1], 0.1f, 8f)))
            {
                report.Error(path, "appearance.hitbox must be [width, height], each between 0.1 and 8");
                bad = true;
            }
            if (eyeHeight is { } eye && !InRange(eye, 0f, 8f)) { report.Error(path, "appearance.eyeHeight must be between 0 and 8"); bad = true; }
            if (textures != null)
            {
                foreach (var (code, texture) in textures)
                    if (string.IsNullOrWhiteSpace(texture)) report.Warning(path, $"appearance texture '{code}' has no path");
            }

            if (presets == null) return bad;
            if (presets.Count > MaxPresets) report.Warning(path, $"{presets.Count} presets; keep it to {MaxPresets} or fewer, every preset costs texture atlas space");
            var seen = new HashSet<string>();
            foreach (var p in presets)
            {
                if (p == null) continue;
                if (string.IsNullOrEmpty(p.id)) report.Warning(path, "a preset has no id, it can't be picked by name");
                else if (!seen.Add(p.id)) report.Warning(path, $"preset id '{p.id}' is used twice");
                if (!NonNegative(p.weight)) { report.Error(path, $"preset '{p.id}' weight must be 0 or more"); bad = true; }
                if (kind == AppearanceKinds.Plm && p.textures != null) report.Info(path, $"preset '{p.id}': textures only apply to shape bodies");
                if (kind == AppearanceKinds.Shape && p.parts != null) report.Info(path, $"preset '{p.id}': parts only apply to PlayerModelLib bodies");
                if (!GearModes.All.Contains(p.gearMode))
                    report.Warning(path, $"preset '{p.id}': gearMode '{p.gearMode}' is not replace or add, replace is used");
                else if (p.gearMode == GearModes.Add && p.gear == null)
                    report.Info(path, $"preset '{p.id}': gearMode add without gear of its own changes nothing");
            }
            return bad;
        }

        private static void CheckTier(TierModel t, ValidationReport report, Rejected rejected)
        {
            string path = Path(t.source, "tier", t.id);
            bool bad = false;
            if (!Positive(t.hp) || !NonNegative(t.damage) || !Positive(t.spellPower) || !NonNegative(t.damageTaken))
            {
                report.Error(path, "hp and spellPower must be above 0, damage and damageTaken 0 or more");
                bad = true;
            }
            if (!InRange(t.size, 0.2f, 5f)) { report.Error(path, "size must be between 0.2 and 5"); bad = true; }
            if (t.lootRolls < 0 || t.lootRolls > 20) report.Warning(path, "lootRolls should be between 0 and 20");
            if (!NonNegative(t.ccDurationMul)) report.Warning(path, "ccDurationMul must be 0 or more");
            if (t.nameFormat != null)
            {
                foreach (var (locale, value) in t.nameFormat.Texts)
                    if (value != null && !value.Contains("{name}"))
                        report.Warning(path, $"nameFormat '{locale}' has no {{name}}, every mob of this tier gets the same name");
            }
            CheckText(t.name, path + " > name", report);
            CheckText(t.nameFormat, path + " > nameFormat", report);
            if (bad) rejected.Tiers.Add(t.id);
        }

        private static void CheckScaling(ScalingModel s, ValidationReport report)
        {
            const string path = "scaling";
            if (!InRange(s.hpPerLevel, 0f, 10f) || !InRange(s.damagePerLevel, 0f, 10f) || !InRange(s.spellPowerPerLevel, 0f, 10f))
                report.Warning(path, "hpPerLevel, damagePerLevel and spellPowerPerLevel should be between 0 and 10");
            if (!InRange(s.resistPerLevel, 0f, 0.05f)) report.Warning(path, "resistPerLevel should be between 0 and 0.05, resists cap at 0.9");
            if (s.maxLevel < 1 || s.maxLevel > 1000) report.Warning(path, "maxLevel should be between 1 and 1000");
        }

        private static void CheckAliases(CatalogData data, ValidationReport report)
        {
            foreach (var (from, to) in data.Aliases)
            {
                string path = $"alias {from}";
                if (from == to) { report.Warning(path, "points at itself"); continue; }

                var visited = new HashSet<string> { from };
                string current = to;
                bool looped = false;
                for (int i = 0; i < MobResolver.MaxAliasHops && data.Aliases.TryGetValue(current, out var next); i++)
                {
                    if (!visited.Add(current)) { looped = true; break; }
                    current = next;
                }
                if (looped || data.Aliases.ContainsKey(current)) { report.Warning(path, "alias chain loops or is too long"); continue; }

                if (!Exists(current, data)) report.Warning(path, $"target '{current}' does not exist");
            }
        }

        private static bool Exists(string id, CatalogData data)
        {
            int sep = id.IndexOf(IdRules.KeySeparator);
            if (sep > 0)
                return data.Races.ContainsKey(MobResolver.ResolveAlias(id.Substring(0, sep), data.Aliases))
                       && data.Roles.ContainsKey(MobResolver.ResolveAlias(id.Substring(sep + 1), data.Aliases));
            return data.Races.ContainsKey(id) || data.Roles.ContainsKey(id) || data.Templates.ContainsKey(id) || data.AiProfiles.ContainsKey(id)
                   || data.Factions.ContainsKey(id) || data.Tiers.ContainsKey(id) || data.LootTables.ContainsKey(id)
                   || data.AnimSets.ContainsKey(id) || data.SoundSets.ContainsKey(id) || data.Groups.ContainsKey(id) || data.Zones.ContainsKey(id);
        }

        // true = unusable
        private static bool CheckStatExtras(float? spellPower, float? healingPower, float? damageReduction, Dictionary<string, float>? resist, string path, ValidationReport report)
        {
            bool bad = false;
            if (spellPower is { } sp && !Positive(sp)) { report.Error(path, "spellPower must be a number above 0"); bad = true; }
            if (healingPower is { } heal && !NonNegative(heal)) { report.Error(path, "healingPower must be 0 or more"); bad = true; }
            if (damageReduction is { } dr && !InRange(dr, 0f, StatMath.MaxDamageReduction))
            {
                report.Error(path, $"damageReduction must be between 0 and {Num(StatMath.MaxDamageReduction)}");
                bad = true;
            }
            if (resist == null) return bad;
            foreach (var (key, value) in resist)
            {
                if (!StatMath.ResistKeys.Contains(key)) report.Warning(path, $"resist '{key}' is not one of: {string.Join(", ", StatMath.ResistKeys)} - ignored");
                else if (!InRange(value, 0f, 1f)) { report.Error(path, $"resist.{key} must be between 0 and 1"); bad = true; }
            }
            return bad;
        }

        // never rejects, bad relations are just ignored
        private static void CheckFaction(FactionModel f, FactionMatrix factions, ValidationReport report)
        {
            string path = Path(f.source, "faction", f.id);
            CheckText(f.name, path + " > name", report);

            if (f.relations != null)
            {
                foreach (var (other, value) in f.relations)
                {
                    if (!FactionMatrix.TryParse(value, out _))
                        report.Warning(path, $"relation to '{other}' is '{value}', expected ally, neutral or hostile - ignored");
                    else if (other != FactionMatrix.Everyone && !factions.Exists(other))
                        report.Warning(path, $"relation to unknown faction '{other}'");
                }
            }

            if (f.attackOnSight != null)
            {
                foreach (var target in f.attackOnSight)
                {
                    if (target == FactionMatrix.Everyone) continue;
                    if (!factions.Exists(target)) report.Warning(path, $"attackOnSight names unknown faction '{target}'");
                    else if (factions.IsAlly(f.id, target)) report.Warning(path, $"attackOnSight names '{target}', an ally, which is never attacked");
                }
            }

            var codes = f.members?.entityCodes;
            if (codes is { Count: > 0 })
            {
                if (f.id == FactionMatrix.Players) report.Info(path, "players membership is fixed (players and their pets), members.entityCodes is ignored");
                else if (codes.Any(string.IsNullOrWhiteSpace)) report.Warning(path, "members.entityCodes has an empty entry");
            }
        }

        private static void CheckFactionRef(string? faction, string path, FactionMatrix factions, ValidationReport report)
        {
            if (!string.IsNullOrEmpty(faction) && !factions.Exists(faction!))
                report.Warning(path, $"unknown faction '{faction}': these mobs are neutral to everyone and only fight back");
        }

        // game font has no em/en dashes
        private static void CheckText(LocText? text, string path, ValidationReport report)
        {
            if (text == null) return;
            foreach (var (locale, value) in text.Texts)
                if (value != null && (value.Contains('—') || value.Contains('–')))
                    report.Warning(path, $"'{locale}' text contains a long dash the game font can't draw, use a spaced hyphen");
        }

        private static string Known(IReadOnlyDictionary<string, VocabularyEntry> entries)
            => entries.Count == 0 ? "none registered" : string.Join(", ", entries.Keys.OrderBy(k => k, StringComparer.Ordinal));

        private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static bool InRange(float v, float min, float max) => float.IsFinite(v) && v >= min && v <= max;
        // [min, max] inside min..max
        private static bool IntRange(int[] pair, int min, int max) => pair.Length == 2 && pair[0] >= min && pair[0] <= pair[1] && pair[1] <= max;
        // "normal" always exists
        private static bool UnknownTier(CatalogData data, string? tier) => !string.IsNullOrEmpty(tier) && tier != StatMath.DefaultTier && !data.Tiers.ContainsKey(tier!);
        internal static string Path(string source, string kind, string id) => string.IsNullOrEmpty(source) ? $"{kind} {id}" : $"{source} > {kind} {id}";
        private static bool Positive(float v) => float.IsFinite(v) && v > 0f;
        private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
    }
}
