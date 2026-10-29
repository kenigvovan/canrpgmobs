using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Data
{
    // config/mobs/*.json of any mod, all sections optional, entries merge by id
    public class MobsFileModel
    {
        public int formatVersion { get; set; } = FormatMigrator.CurrentVersion;
        public List<RaceModel>? races { get; set; }
        public List<RoleModel>? roles { get; set; }
        public List<MobTemplateModel>? mobs { get; set; }
        public List<AiProfileModel>? aiProfiles { get; set; }
        public List<FactionModel>? factions { get; set; }
        public List<TierModel>? tiers { get; set; }
        public List<LootTableModel>? lootTables { get; set; }
        // a later file replaces an earlier one
        public ScalingModel? scaling { get; set; }
        public List<AnimSetModel>? animSets { get; set; }
        public List<SoundSetModel>? soundSets { get; set; }
        public List<SpawnGroupModel>? groups { get; set; }
        public List<ZoneModel>? zones { get; set; }
        // old id to new id, for mobs saved in worlds
        public Dictionary<string, string>? aliases { get; set; }
        // language -> lang key -> text, a later file wins per key
        public Dictionary<string, Dictionary<string, string>>? lang { get; set; }
    }

    public class SpawnGroupModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        public float radius { get; set; } = 4f;
        // overrides every member's faction
        public string? faction { get; set; }
        public List<SpawnGroupMemberModel>? members { get; set; }
    }

    public class SpawnGroupMemberModel
    {
        // template id or race|role
        public string mob { get; set; } = "";
        public int[]? count { get; set; }
        public string? tier { get; set; }
        // 0 = spawner's level, else the template's range
        public int level { get; set; }
    }

    public abstract class CatalogEntryModel
    {
        public string id { get; set; } = "";
        // asset path, for problem reports
        [JsonIgnore] public string source { get; set; } = "";
        // other mods' data by domain, read with ResolvedMob.Extension
        public Dictionary<string, JObject>? extensions { get; set; }
    }

    public class RaceModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        // humanoid, creature or a body registered by another mod
        public string body { get; set; } = "humanoid";
        public string? faction { get; set; }
        public AppearanceModel? appearance { get; set; }
        public string? animSet { get; set; }
        public string? soundSet { get; set; }
        public StatBlockModel stats { get; set; } = new();
        public List<SkillRuleModel>? skills { get; set; }
        public List<string>? loot { get; set; }
        // {race} and {role}
        public LocText? nameFormat { get; set; }
    }

    public class RoleModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        public string? aiProfile { get; set; }
        public StatMulModel? statMul { get; set; }
        // added after the multipliers
        public StatAddModel? statAdd { get; set; }
        public Dictionary<string, AnimEntryModel>? animOverrides { get; set; }
        public Dictionary<string, SoundEntryModel>? soundOverrides { get; set; }
        public List<SkillRuleModel>? skills { get; set; }
        public List<string>? loot { get; set; }
        // added to the race's, "none" removes what the race wore
        public List<string>? gear { get; set; }
        // "none" empties the hand, a weapon sets melee damage
        public string? rightHand { get; set; }
        public string? leftHand { get; set; }
    }

    public class MobTemplateModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        public string race { get; set; } = "";
        public string role { get; set; } = "";
        public int[]? levelRange { get; set; }
        public string? aiProfile { get; set; }
        public string? faction { get; set; }
        public StatOverrideModel? stats { get; set; }
        public AppearanceOverrideModel? appearance { get; set; }
        public string? animSet { get; set; }
        public string? soundSet { get; set; }
        public string skillsMode { get; set; } = MergeModes.Append;
        public List<SkillRuleModel>? skills { get; set; }
        public string? defaultTier { get; set; }
        public string lootMode { get; set; } = MergeModes.Append;
        public List<string>? loot { get; set; }
        public List<BossPhaseModel>? phases { get; set; }
    }

    // template skillsMode and lootMode
    public static class MergeModes
    {
        public const string Append = "append";
        public const string Replace = "replace";
        public static readonly string[] All = { Append, Replace };
    }

    public class StatBlockModel
    {
        public float maxHealth { get; set; } = 20f;
        public float meleeDamage { get; set; } = 3f;
        public int meleeTier { get; set; } = 1;
        public float moveSpeed { get; set; } = 0.03f;
        public float spellPower { get; set; } = 1f;
        public float healingPower { get; set; } = 1f;
        // 0 to 0.9
        public float damageReduction { get; set; }
        // school to fraction
        public Dictionary<string, float>? resist { get; set; }
    }

    public class StatMulModel
    {
        public float maxHealth { get; set; } = 1f;
        public float meleeDamage { get; set; } = 1f;
        public float moveSpeed { get; set; } = 1f;
        public float spellPower { get; set; } = 1f;
        public float healingPower { get; set; } = 1f;
    }

    public class StatAddModel
    {
        public float maxHealth { get; set; }
        public float meleeDamage { get; set; }
        public float moveSpeed { get; set; }
        public float spellPower { get; set; }
        public float healingPower { get; set; }
        public float damageReduction { get; set; }
        public Dictionary<string, float>? resist { get; set; }
    }

    // set values replace the race and role result
    public class StatOverrideModel
    {
        public float? maxHealth { get; set; }
        public float? meleeDamage { get; set; }
        public int? meleeTier { get; set; }
        public float? moveSpeed { get; set; }
        public float? spellPower { get; set; }
        public float? healingPower { get; set; }
        public float? damageReduction { get; set; }
        public Dictionary<string, float>? resist { get; set; }
    }

    public class TierModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        // {name} and {level}
        public LocText? nameFormat { get; set; }
        public float hp { get; set; } = 1f;
        public float damage { get; set; } = 1f;
        // also healing
        public float spellPower { get; set; } = 1f;
        public float damageTaken { get; set; } = 1f;
        // extra rolls on tables with tierRollBonus
        public int lootRolls { get; set; }
        public float size { get; set; } = 1f;
        // 0 = immune
        public float ccDurationMul { get; set; } = 1f;

        public string FormatName(string mobName, int level, string? locale)
        {
            if (nameFormat == null) return mobName;
            return nameFormat.Resolve(locale, "{name}").Replace("{name}", mobName).Replace("{level}", level.ToString(CultureInfo.InvariantCulture));
        }
    }

    public class ScalingModel
    {
        // fraction of the level 1 value per level
        public float hpPerLevel { get; set; } = 0.1f;
        public float damagePerLevel { get; set; } = 0.05f;
        public float spellPowerPerLevel { get; set; } = 0.05f;
        // magic resist per level
        public float resistPerLevel { get; set; }
        public int maxLevel { get; set; } = 60;
    }

    public class LootTableModel : CatalogEntryModel
    {
        public int[]? rolls { get; set; }
        // weight of picking nothing
        public float emptyWeight { get; set; }
        // 0.1 = +10% count per level
        public float levelCountScale { get; set; }
        public bool tierRollBonus { get; set; } = true;
        public List<LootEntryModel>? entries { get; set; }
        // still checks chance, minLevel and tiers
        public List<LootEntryModel>? guaranteed { get; set; }
    }

    public class LootEntryModel
    {
        public const string Item = "item";
        public const string Block = "block";
        public const string Table = "table";
        public static readonly string[] Types = { Item, Block, Table };

        public string type { get; set; } = Item;
        // item/block code or loot table id
        public string code { get; set; } = "";
        public float weight { get; set; } = 1f;
        public int[]? count { get; set; }
        public float chance { get; set; } = 1f;
        public int minLevel { get; set; }
        // null = every tier
        public List<string>? tiers { get; set; }
    }

    public static class AppearanceKinds
    {
        // PlayerModelLib, humanoid body
        public const string Plm = "plm";
        // entity shape, creature body
        public const string Shape = "shape";
    }

    public class AppearanceModel
    {
        public const string Seraph = "seraph";

        public string kind { get; set; } = AppearanceKinds.Plm;
        // only the default seraph may omit the domain
        public string model { get; set; } = Seraph;
        public string? shape { get; set; }
        public Dictionary<string, string>? textures { get; set; }
        // shapes scale hitbox and eye height too
        public float size { get; set; } = 1f;
        // [width, height] at size 1
        public float[]? hitbox { get; set; }
        public float? eyeHeight { get; set; }
        // plm: skin parts, shape: texture variants
        public List<AppearancePresetModel>? presets { get; set; }
        // visual only
        public List<string>? gear { get; set; }
        public string? rightHand { get; set; }
        public string? leftHand { get; set; }
    }

    public class AppearanceOverrideModel
    {
        public string? model { get; set; }
        public string? shape { get; set; }
        // merged over the race's
        public Dictionary<string, string>? textures { get; set; }
        public float? size { get; set; }
        public float[]? hitbox { get; set; }
        public float? eyeHeight { get; set; }
        public List<AppearancePresetModel>? presets { get; set; }
        // replaces the race's, "none" = nothing
        public List<string>? gear { get; set; }
        public string? rightHand { get; set; }
        public string? leftHand { get; set; }
    }

    public class AppearancePresetModel
    {
        public string id { get; set; } = "";
        public float weight { get; set; } = 1f;
        // part -> variant, "a|b" or "*" for random
        public Dictionary<string, string>? parts { get; set; }
        public Dictionary<string, string>? textures { get; set; }
        public List<string>? gear { get; set; }
        public string gearMode { get; set; } = GearModes.Replace;
        public string? rightHand { get; set; }
        public string? leftHand { get; set; }
    }

    public static class GearModes
    {
        public const string Replace = "replace";
        public const string Add = "add";
        public static readonly string[] All = { Replace, Add };
    }

    public static class AnimKeys
    {
        public const string Idle = "idle";
        public const string Walk = "walk";
        public const string Run = "run";
        public const string Attack = "attack";
        public const string Cast = "cast";
        public const string Release = "release";
        public const string Hurt = "hurt";
        public const string Die = "die";
        public const string Flee = "flee";
        public const string Taunt = "taunt";
        public const string Shoot = "shoot";
        public static readonly string[] All = { Idle, Walk, Run, Attack, Cast, Release, Hurt, Die, Flee, Taunt, Shoot };
    }

    public class AnimSetModel : CatalogEntryModel
    {
        public string? extends { get; set; }
        public Dictionary<string, AnimEntryModel>? anims { get; set; }
    }

    public class AnimEntryModel
    {
        public string animation { get; set; } = "";
        public float speed { get; set; } = 1f;
        public string? blendMode { get; set; }
        public float? weight { get; set; }
        public float? easeIn { get; set; }
        public float? easeOut { get; set; }
        public string[]? onControls { get; set; }
        public bool defaultAnim { get; set; }
    }

    public static class SoundKeys
    {
        public const string Idle = "idle";
        public const string Hurt = "hurt";
        public const string Death = "death";
        public const string Aggro = "aggro";
        public const string Attack = "attack";
        public const string CastStart = "castStart";
        public static readonly string[] All = { Idle, Hurt, Death, Aggro, Attack, CastStart };
        // played by the game itself from the entity's sound list
        public static readonly string[] EntityKeys = { Idle, Hurt, Death };
    }

    public class SoundSetModel : CatalogEntryModel
    {
        public string? extends { get; set; }
        public Dictionary<string, SoundEntryModel>? sounds { get; set; }
        // percent per tick, like vanilla idleSoundChance
        public float idleChance { get; set; }
    }

    public class SoundEntryModel
    {
        // under sounds/, trailing * = random variant
        public string path { get; set; } = "";
        public float chance { get; set; } = 1f;
        public float range { get; set; } = 24f;
        public float volume { get; set; } = 1f;
        public float pitchVariance { get; set; } = 0.1f;
    }

    public class AiProfileModel : CatalogEntryModel
    {
        public string archetype { get; set; } = "meleeBrute";
        public float aggroRange { get; set; } = 20f;
        // 0 = no leash
        public float leashRange { get; set; } = 40f;
        // 0 = never calls
        public float callForHelpRange { get; set; } = 12f;
        public float[]? preferredRange { get; set; }
        // 0 = only when feared
        public float fleeBelowHp { get; set; }
        public float followRange { get; set; } = 6f;
        public TargetingModel? targeting { get; set; }
        public MeleeModel? melee { get; set; }
        public RangedModel? ranged { get; set; }
        // custom archetype, "@walk" names an animation key
        public List<JObject>? tasks { get; set; }
    }

    public class TargetingModel
    {
        public const string Threat = "threat";
        public const string Nearest = "nearest";
        public const string LowestHp = "lowestHp";
        public static readonly string[] All = { Threat, Nearest, LowestHp };

        public string selector { get; set; } = Threat;
        // seconds
        public float switchCooldown { get; set; } = 1f;
    }

    public class MeleeModel
    {
        public float damageMul { get; set; } = 1f;
        public string damageType { get; set; } = "BluntAttack";
        public int attackDurationMs { get; set; } = 900;
        public int hitAtMs { get; set; } = 450;
        public int[]? cooldownMs { get; set; }
        public string animation { get; set; } = AnimKeys.Attack;
        // 0 = from the weapon, 1.5 bare hand
        public float reach { get; set; }
    }

    // bow + arrow damage like a player's shot, no bow = no shooting
    public class RangedModel
    {
        public string arrow { get; set; } = "game:arrow-flint";
        public float damageMul { get; set; } = 1f;
        public string damageType { get; set; } = "PiercingAttack";
        public int aimMs { get; set; } = 1000;
        public int[]? cooldownMs { get; set; }
        // blocks per second
        public float speed { get; set; } = 30f;
        // degrees, doubled for first shots
        public float dispersion { get; set; } = 2f;
        public float minRange { get; set; } = 3f;
        public string animation { get; set; } = AnimKeys.Shoot;
        // "none" = silent
        public string sound { get; set; } = "game:bow-release";
        // 0 = straight
        public float gravityFactor { get; set; } = 1f;
        public bool useMeleeAdd { get; set; }
    }

    // canrpgmobs:players and canrpgmobs:wildlife always exist
    public class FactionModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        public FactionMembersModel? members { get; set; }
        // faction or "*" -> ally/neutral/hostile, counts both ways
        public Dictionary<string, string>? relations { get; set; }
        // null = every hostile faction
        public List<string>? attackOnSight { get; set; }
        public bool helpAllies { get; set; } = true;
    }

    public class FactionMembersModel
    {
        // non-mob codes with * wildcards
        public List<string>? entityCodes { get; set; }
    }

    public class SkillRuleModel
    {
        public string id { get; set; } = "";
        // bare id = canrpgclasses domain
        public string spell { get; set; } = "";
        // replaces the spell
        public SkillActionModel? action { get; set; }
        public string target { get; set; } = SkillTargets.CurrentEnemy;
        public float allyHpBelow { get; set; } = 0.7f;
        public string? targetEffect { get; set; }
        public float clusterRadius { get; set; } = 4f;
        public float priority { get; set; } = 50f;
        // among ready rules of the top priority
        public float weight { get; set; } = 1f;
        public float minRange { get; set; }
        public float maxRange { get; set; } = 16f;
        public float cooldown { get; set; } = 5f;
        public float cooldownJitter { get; set; } = 0.2f;
        public string cooldownMode { get; set; } = CooldownModes.Max;
        public float gcd { get; set; } = 1.5f;
        public float[]? initialCooldown { get; set; }
        public List<SkillConditionModel>? conditions { get; set; }
        // null = only for enemy targets
        public bool? requireLineOfSight { get; set; }
        public bool stopToCast { get; set; } = true;
        public bool faceTarget { get; set; } = true;
        public SkillAnimModel? animation { get; set; }
        public SkillSoundModel? sound { get; set; }
        public SkillInterruptModel? interrupt { get; set; }
        public int recoverMs { get; set; } = 400;

        // for the generic selector param checks
        public float? Number(string name) => name switch
        {
            "allyHpBelow" => allyHpBelow,
            "clusterRadius" => clusterRadius,
            "minRange" => minRange,
            "maxRange" => maxRange,
            _ => null
        };
    }

    public static class SkillTargets
    {
        public const string CurrentEnemy = "currentEnemy";
        public const string Self = "self";
        public const string LowestHpAlly = "lowestHpAlly";
        public const string RandomEnemyInRange = "randomEnemyInRange";
        public const string AllyWithoutEffect = "allyWithoutEffect";
        public const string EnemyCluster = "enemyCluster";
    }

    public static class CooldownModes
    {
        public const string Max = "max";
        public const string Rule = "rule";
        public const string Spell = "spell";
        public static readonly string[] All = { Max, Rule, Spell };
    }

    public class SkillConditionModel
    {
        public static readonly string[] NumberFields = { "value", "min", "max", "range" };

        public string type { get; set; } = "";
        public float? value { get; set; }
        public float? min { get; set; }
        public float? max { get; set; }
        public float? range { get; set; }
        public string? effect { get; set; }
        public bool not { get; set; }

        public float? Number(string name) => name switch
        {
            "value" => value,
            "min" => min,
            "max" => max,
            "range" => range,
            _ => null
        };
    }

    public class SkillActionModel
    {
        public static readonly string[] NumberFields = { "count", "maxAlive", "radius", "level", "seconds", "value", "spellPower" };

        public string type { get; set; } = "";
        public string? mob { get; set; }
        public float? count { get; set; }
        public float? maxAlive { get; set; }
        public float? radius { get; set; }
        public float? level { get; set; }
        public float? seconds { get; set; }
        public float? value { get; set; }
        public float? spellPower { get; set; }
        public bool despawnWithOwner { get; set; } = true;

        public float? Number(string name) => name switch
        {
            "count" => count,
            "maxAlive" => maxAlive,
            "radius" => radius,
            "level" => level,
            "seconds" => seconds,
            "value" => value,
            "spellPower" => spellPower,
            _ => null
        };
    }

    // matches canrpgclasses ControlType
    public static class ControlNames
    {
        public const string Stun = "stun";
        public const string Fear = "fear";
        public const string Silence = "silence";
        public const string Root = "root";
        public const string Transmute = "transmute";
        public static readonly string[] All = { Stun, Fear, Silence, Root, Transmute };
    }

    // entered once per life, the latest one is active
    public class BossPhaseModel
    {
        public string id { get; set; } = "";
        public float hpBelow { get; set; } = 0.5f;
        public LocText? announce { get; set; }
        public string? sound { get; set; }
        public string? animation { get; set; }
        public List<string>? ccImmune { get; set; }
        // stacks with the tier's
        public float ccDurationMul { get; set; } = 1f;
        public float damageMul { get; set; } = 1f;
        public float spellPowerMul { get; set; } = 1f;
        public float damageTakenMul { get; set; } = 1f;
        public List<SkillActionModel>? actions { get; set; }
    }

    public class SkillAnimModel
    {
        public string? cast { get; set; }
        public string? release { get; set; }
    }

    public class SkillSoundModel
    {
        public string? start { get; set; }
        public string? release { get; set; }
    }

    public class SkillInterruptModel
    {
        public bool onDamage { get; set; } = true;
        // fraction of max health, 0 = any hit
        public float damageFraction { get; set; }
        public bool onMove { get; set; }
        // cancel or finish
        public string onTargetLost { get; set; } = "cancel";
    }

    // canrpgmobs-settings.json
    public class MobsSettings
    {
        public const string KeepSnapshot = "keepSnapshot";
        public const string Despawn = "despawn";
        public const string FallbackPrefix = "fallback:";

        public int formatVersion { get; set; } = FormatMigrator.CurrentVersion;
        public string missingArchetypePolicy { get; set; } = KeepSnapshot;
        public int brainEvalMs { get; set; } = 300;
        public bool hotApplyAppearance { get; set; } = true;
        // live mobs per 50 ms tick after a catalog change
        public int reapplyPerTick { get; set; } = 10;
        public int maxPreviewSeconds { get; set; } = 60;
        // 0 = game default 128
        public int mobSimulationRange { get; set; } = 80;
        public bool zonesOnMapForPlayers { get; set; }

        public static bool IsValidPolicy(string? policy)
            => policy == KeepSnapshot || policy == Despawn
               || (policy != null && policy.StartsWith(FallbackPrefix) && policy.Length > FallbackPrefix.Length);
    }
}
