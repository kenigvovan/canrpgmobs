using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Data
{
    // immutable, shared by every mob with this key
    public sealed class ResolvedMob
    {
        public string Key { get; }
        public RaceModel Race { get; }
        public RoleModel Role { get; }
        public MobTemplateModel? Template { get; }
        public AiProfileModel Ai { get; }
        public AppearanceModel Appearance { get; }
        public string Faction { get; }
        public float MaxHealth { get; }
        public float MeleeDamage { get; }
        public int MeleeTier { get; }
        public float MoveSpeed { get; }
        public float SpellPower { get; }
        public float HealingPower { get; }
        public float DamageReduction { get; }
        // level 1, before scaling
        public IReadOnlyDictionary<string, float> Resists { get; }
        // a table listed twice rolls twice
        public IReadOnlyList<string> LootTables { get; }
        public string? DefaultTier { get; }
        public IReadOnlyDictionary<string, AnimEntryModel> Anims { get; }
        public IReadOnlyDictionary<string, SoundEntryModel> Sounds { get; }
        public float IdleSoundChance { get; }
        public int MinLevel { get; }
        public int MaxLevel { get; }
        // highest priority first
        public IReadOnlyList<SkillRuleModel> Skills { get; }
        // spell unknown or not usable by NPCs
        public int SkippedSkills { get; }
        // highest hpBelow first
        public IReadOnlyList<BossPhaseModel> Phases { get; }
        // role melee mul/add, also applied to held weapon damage
        public float MeleeMul { get; }
        public float MeleeAdd { get; }
        // template meleeDamage wins over a held weapon
        public bool MeleeOverridden { get; }

        // template wins over role, role over race
        public JObject? Extension(string modDomain)
        {
            foreach (var source in new[] { Template?.extensions, Role.extensions, Race.extensions })
                if (source != null && source.TryGetValue(modDomain, out var data)) return data;
            return null;
        }

        internal ResolvedMob(string key, RaceModel race, RoleModel role, MobTemplateModel? template, AiProfileModel ai, Func<string, bool>? spellUsable, CatalogData data)
        {
            Key = key;
            Race = race;
            Role = role;
            Template = template;
            Ai = ai;
            Faction = !string.IsNullOrEmpty(template?.faction) ? template!.faction!
                : !string.IsNullOrEmpty(race.faction) ? race.faction! : FactionMatrix.Monsters;

            // race, then role mul/add, then template overrides
            var mul = role.statMul ?? new StatMulModel();
            var over = template?.stats;
            var add = role.statAdd;
            var stats = race.stats;
            MaxHealth = over?.maxHealth ?? stats.maxHealth * mul.maxHealth + (add?.maxHealth ?? 0f);
            MeleeDamage = over?.meleeDamage ?? stats.meleeDamage * mul.meleeDamage + (add?.meleeDamage ?? 0f);
            MeleeMul = mul.meleeDamage;
            MeleeAdd = add?.meleeDamage ?? 0f;
            MeleeOverridden = over?.meleeDamage != null;
            MeleeTier = over?.meleeTier ?? stats.meleeTier;
            MoveSpeed = over?.moveSpeed ?? stats.moveSpeed * mul.moveSpeed + (add?.moveSpeed ?? 0f);
            SpellPower = over?.spellPower ?? stats.spellPower * mul.spellPower + (add?.spellPower ?? 0f);
            HealingPower = over?.healingPower ?? stats.healingPower * mul.healingPower + (add?.healingPower ?? 0f);
            DamageReduction = over?.damageReduction ?? stats.damageReduction + (add?.damageReduction ?? 0f);
            Resists = MergeResists(stats.resist, add?.resist, over?.resist);

            LootTables = MergeLoot(race, role, template);
            DefaultTier = string.IsNullOrWhiteSpace(template?.defaultTier) ? null : template!.defaultTier;

            string? animSetId = template?.animSet ?? race.animSet;
            var anims = MobResolver.FlattenSet<AnimSetModel, AnimEntryModel>(animSetId, data.AnimSets, data.Aliases, s => s.extends, s => s.anims);
            Overlay(anims, role.animOverrides);
            Anims = anims;

            string? soundSetId = template?.soundSet ?? race.soundSet;
            var sounds = MobResolver.FlattenSet<SoundSetModel, SoundEntryModel>(soundSetId, data.SoundSets, data.Aliases, s => s.extends, s => s.sounds);
            Overlay(sounds, role.soundOverrides);
            Sounds = sounds;
            IdleSoundChance = soundSetId != null && data.SoundSets.TryGetValue(MobResolver.ResolveAlias(soundSetId, data.Aliases), out var soundSet)
                ? soundSet.idleChance : 0f;

            Appearance = MergeAppearance(race, role, template);

            if (template?.levelRange is { Length: >= 2 } range)
            {
                MinLevel = Math.Max(1, range[0]);
                MaxLevel = Math.Max(MinLevel, range[1]);
            }
            else MinLevel = MaxLevel = 1;

            Skills = MergeSkills(race, role, template, spellUsable, out int skipped);
            SkippedSkills = skipped;
            Phases = (template?.phases ?? new List<BossPhaseModel>()).Where(p => p != null).OrderByDescending(p => p.hpBelow).ToArray();
        }

        public string DisplayName(string? locale)
        {
            if (Template?.name != null) return Template.name.Resolve(locale, Template.id);
            string race = Race.name?.Resolve(locale, Race.id) ?? Race.id;
            string role = Role.name?.Resolve(locale, Role.id) ?? Role.id;
            string format = Race.nameFormat?.Resolve(locale, "{race} {role}") ?? "{race} {role}";
            return format.Replace("{race}", race).Replace("{role}", role);
        }

        private static Dictionary<string, float> MergeResists(Dictionary<string, float>? race, Dictionary<string, float>? added, Dictionary<string, float>? replaced)
        {
            var resists = new Dictionary<string, float>(StringComparer.Ordinal);
            if (race != null) foreach (var pair in race) resists[pair.Key] = pair.Value;
            if (added != null) foreach (var pair in added) resists[pair.Key] = (resists.TryGetValue(pair.Key, out var before) ? before : 0f) + pair.Value;
            if (replaced != null) foreach (var pair in replaced) resists[pair.Key] = pair.Value;
            return resists;
        }

        private static string[] MergeLoot(RaceModel race, RoleModel role, MobTemplateModel? template)
        {
            IEnumerable<string> loot = template?.lootMode == MergeModes.Replace
                ? template.loot ?? new List<string>()
                : (race.loot ?? new List<string>()).Concat(role.loot ?? new List<string>()).Concat(template?.loot ?? new List<string>());
            return loot.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        }

        private static void Overlay<T>(Dictionary<string, T> target, Dictionary<string, T>? overrides) where T : class
        {
            if (overrides == null) return;
            foreach (var pair in overrides) if (pair.Value != null) target[pair.Key] = pair.Value;
        }

        private static AppearanceModel MergeAppearance(RaceModel race, RoleModel role, MobTemplateModel? template)
        {
            var baseLook = race.appearance ?? new AppearanceModel();
            var lookOver = template?.appearance;
            Dictionary<string, string>? textures = null;
            if (baseLook.textures != null || lookOver?.textures != null)
            {
                textures = new Dictionary<string, string>(StringComparer.Ordinal);
                if (baseLook.textures != null) foreach (var pair in baseLook.textures) textures[pair.Key] = pair.Value;
                if (lookOver?.textures != null) foreach (var pair in lookOver.textures) textures[pair.Key] = pair.Value;
            }
            return new AppearanceModel
            {
                kind = baseLook.kind,
                model = lookOver?.model ?? baseLook.model,
                shape = lookOver?.shape ?? baseLook.shape,
                textures = textures,
                size = lookOver?.size ?? baseLook.size,
                hitbox = lookOver?.hitbox ?? baseLook.hitbox,
                eyeHeight = lookOver?.eyeHeight ?? baseLook.eyeHeight,
                presets = lookOver?.presets ?? baseLook.presets,
                // race gear + role gear, template list replaces both
                gear = lookOver?.gear ?? (baseLook.gear == null && role.gear == null
                    ? null
                    : (baseLook.gear ?? new List<string>()).Concat(role.gear ?? new List<string>()).ToList()),
                rightHand = lookOver?.rightHand ?? role.rightHand ?? baseLook.rightHand,
                leftHand = lookOver?.leftHand ?? role.leftHand ?? baseLook.leftHand
            };
        }

        private static SkillRuleModel[] MergeSkills(RaceModel race, RoleModel role, MobTemplateModel? template, Func<string, bool>? spellUsable, out int skipped)
        {
            IEnumerable<SkillRuleModel> all = template?.skillsMode == MergeModes.Replace
                ? template.skills ?? new List<SkillRuleModel>()
                : (race.skills ?? new List<SkillRuleModel>())
                    .Concat(role.skills ?? new List<SkillRuleModel>())
                    .Concat(template?.skills ?? new List<SkillRuleModel>());
            var list = all.Where(s => s != null).ToList();
            // action rules need no spell, bad action types are caught by the validator
            var usable = spellUsable == null ? list : list.Where(s => s.action != null || spellUsable(s.spell)).ToList();
            skipped = list.Count - usable.Count;
            return usable.OrderByDescending(s => s.priority).ToArray(); // stable sort, keeps file order
        }
    }
}
