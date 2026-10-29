using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Client.Editor.Controllers
{
    public enum FieldKind { Id, Text, Number, Int, Toggle, TriState, Choice, Pick, LocText, RangePair, IntRangePair, StringList, KeyValue }

    // no GUI types here, the dialog decides how each kind is drawn
    public sealed class FieldSpec
    {
        public FieldSpec(string key, FieldKind kind)
        {
            Key = key;
            Kind = kind;
        }

        public string Key { get; }
        public FieldKind Kind { get; }
        public float Min { get; init; } = float.MinValue;
        public float Max { get; init; } = float.MaxValue;
        public string[]? Choices { get; init; }
        // KeyValue: suggestions for keys
        public string? PickList { get; init; }
        // KeyValue only
        public string[]? ValueChoices { get; init; }
        public string? ValuePickList { get; init; }
        public bool NumberValues { get; init; }
        public bool IsSound { get; init; }
        // Toggle: value the game uses when the field is missing
        public bool Default { get; init; }
        // Choice without a default, can't be cleared
        public bool Required { get; init; }
    }

    public enum ChildMode { Object, Array, Map }

    public sealed class ChildSpec
    {
        public ChildSpec(string key, ChildMode mode, string nodeId, Func<JObject> newItem)
        {
            Key = key;
            Mode = mode;
            NodeId = nodeId;
            NewItem = newItem;
        }

        public string Key { get; }
        public ChildMode Mode { get; }
        public string NodeId { get; }
        public Func<JObject> NewItem { get; }
    }

    public sealed class NodeSpec
    {
        private readonly Func<JObject, IEnumerable<FieldSpec>>? dynamicFields;

        public NodeSpec(string id, FieldSpec[] fields, ChildSpec[]? children = null, Func<JObject, IEnumerable<FieldSpec>>? dynamicFields = null)
        {
            Id = id;
            Fields = fields;
            Children = children ?? Array.Empty<ChildSpec>();
            this.dynamicFields = dynamicFields;
        }

        public string Id { get; }
        public IReadOnlyList<FieldSpec> Fields { get; }
        public IReadOnlyList<ChildSpec> Children { get; }

        // static fields + ones that depend on current values (condition params)
        public IEnumerable<FieldSpec> FieldsFor(JObject token)
            => dynamicFields == null ? Fields : Fields.Concat(dynamicFields(token).Where(d => Fields.All(f => f.Key != d.Key)));
    }

    // Mirrors the data models. The validator still decides what's valid.
    public sealed class EditorSchema
    {
        public static readonly IReadOnlyDictionary<string, string> SectionNodes = new Dictionary<string, string>
        {
            ["races"] = "race", ["roles"] = "role", ["mobs"] = "mob", ["aiProfiles"] = "aiProfile", ["factions"] = "faction",
            ["tiers"] = "tier", ["lootTables"] = "lootTable", ["animSets"] = "animSet", ["soundSets"] = "soundSet", ["groups"] = "group",
            ["zones"] = "zone"
        };

        private static readonly string[] Relations = { "ally", "neutral", "hostile" };

        private readonly Dictionary<string, NodeSpec> nodes = new(StringComparer.Ordinal);
        private readonly RuleVocabulary vocabulary;

        public EditorSchema(RuleVocabulary vocabulary)
        {
            this.vocabulary = vocabulary;
            Build();
        }

        public NodeSpec Node(string id) => nodes[id];
        public NodeSpec SectionNode(string section) => nodes[SectionNodes[section]];

        public static JObject NewEntry(string section, string id, CatalogData data)
        {
            JObject Name() => new() { ["en"] = "New" };
            var entry = section switch
            {
                "races" => new JObject
                {
                    ["name"] = Name(), ["body"] = MobBodies.Humanoid,
                    ["appearance"] = new JObject { ["kind"] = AppearanceKinds.Plm, ["model"] = AppearanceModel.Seraph },
                    ["stats"] = new JObject { ["maxHealth"] = 20, ["meleeDamage"] = 3, ["moveSpeed"] = 0.03 }
                },
                "roles" => new JObject { ["name"] = Name() },
                "mobs" => new JObject
                {
                    ["name"] = Name(), ["race"] = data.Races.Keys.OrderBy(k => k).FirstOrDefault() ?? "",
                    ["role"] = data.Roles.Keys.OrderBy(k => k).FirstOrDefault() ?? ""
                },
                "aiProfiles" => new JObject { ["archetype"] = MobValidator.DefaultArchetype, ["aggroRange"] = 20 },
                "factions" => new JObject { ["name"] = Name(), ["relations"] = new JObject { ["players"] = "hostile" } },
                "tiers" => new JObject { ["name"] = Name(), ["hp"] = 1 },
                "lootTables" => new JObject { ["rolls"] = new JArray(1, 1), ["entries"] = new JArray(new JObject { ["code"] = "flint" }) },
                "animSets" => new JObject { ["anims"] = new JObject() },
                "soundSets" => new JObject { ["sounds"] = new JObject() },
                "groups" => new JObject
                {
                    ["name"] = Name(), ["radius"] = 4,
                    ["members"] = new JArray(new JObject { ["mob"] = data.Templates.Keys.OrderBy(k => k).FirstOrDefault() ?? "", ["count"] = new JArray(1, 2) })
                },
                // the outline is drawn on the map or with the wand
                "zones" => new JObject { ["name"] = Name(), ["level"] = new JArray(1, 5), ["polygon"] = new JArray() },
                _ => new JObject()
            };
            entry.AddFirst(new JProperty("id", id));
            return entry;
        }

        public static string Describe(string nodeId, JObject item, string? key)
        {
            string S(string k) => item[k] is JValue value && value.Value != null
                ? value.Type == JTokenType.Float ? JsonDraft.Display(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture)) : value.ToString()
                : "";
            string text = nodeId switch
            {
                "skill" => $"{S("id")}  {S("spell")}",
                "condition" => $"{(item["not"] is JValue { Type: JTokenType.Boolean } not && (bool)not ? "not " : "")}{S("type")} {S("value")}",
                "lootEntry" => $"{(S("type").Length == 0 ? LootEntryModel.Item : S("type"))} {S("code")}  w{(S("weight").Length == 0 ? "1" : S("weight"))}",
                "preset" => $"{S("id")}  w{(S("weight").Length == 0 ? "1" : S("weight"))}",
                "phase" => $"{S("id")}  hp <= {S("hpBelow")}",
                "action" => $"{S("type")} {S("mob")}",
                "groupMember" => $"{S("mob")}  x{(item["count"] is JArray { Count: 2 } count ? $"{count[0]}-{count[1]}" : "1")}",
                "zoneSpawn" => $"{(S("mob").Length > 0 ? S("mob") : "group " + S("group"))}  w{(S("weight").Length == 0 ? "1" : S("weight"))}",
                "animEntry" => $"{key} -> {S("animation")}",
                "soundEntry" => $"{key} -> {S("path")}",
                _ => key ?? S("id")
            };
            return string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();
        }

        private void Build()
        {
            FieldSpec Id() => new("id", FieldKind.Id);
            FieldSpec Text(string key) => new(key, FieldKind.Text);
            FieldSpec Num(string key, float min, float max) => new(key, FieldKind.Number) { Min = min, Max = max };
            FieldSpec Int(string key, float min, float max) => new(key, FieldKind.Int) { Min = min, Max = max };
            FieldSpec Toggle(string key, bool byDefault = false) => new(key, FieldKind.Toggle) { Default = byDefault };
            FieldSpec Choice(string key, params string[] choices) => new(key, FieldKind.Choice) { Choices = choices };
            FieldSpec Pick(string key, string list) => new(key, FieldKind.Pick) { PickList = list };
            FieldSpec Sound(string key) => new(key, FieldKind.Pick) { PickList = "sounds", IsSound = true };
            FieldSpec Loc(string key) => new(key, FieldKind.LocText);
            FieldSpec Range(string key, float min, float max) => new(key, FieldKind.RangePair) { Min = min, Max = max };
            FieldSpec IntRange(string key, float min, float max) => new(key, FieldKind.IntRangePair) { Min = min, Max = max };
            FieldSpec List(string key, string? pick) => new(key, FieldKind.StringList) { PickList = pick };

            ChildSpec Obj(string key, string node) => new(key, ChildMode.Object, node, () => new JObject());
            ChildSpec Arr(string key, string node, Func<JObject> make) => new(key, ChildMode.Array, node, make);
            ChildSpec Map(string key, string node, Func<JObject> make) => new(key, ChildMode.Map, node, make);

            int counter = 0;
            ChildSpec Skills() => Arr("skills", "skill", () => new JObject { ["id"] = "skill" + ++counter, ["spell"] = "", ["priority"] = 50, ["maxRange"] = 16, ["cooldown"] = 5 });
            ChildSpec AnimEntries(string key) => Map(key, "animEntry", () => new JObject { ["animation"] = "" });
            ChildSpec SoundEntries(string key) => Map(key, "soundEntry", () => new JObject { ["path"] = "" });

            FieldSpec[] StatFields(bool withAdd) => new[]
            {
                Num("maxHealth", 0, 100000), Num("meleeDamage", 0, 10000), Int("meleeTier", 0, 10), Num("moveSpeed", 0, 0.2f),
                Num("spellPower", 0, 100), Num("healingPower", 0, 100), Num("damageReduction", withAdd ? -0.9f : 0, 0.9f),
                new FieldSpec("resist", FieldKind.KeyValue) { PickList = "resistKeys", NumberValues = true }
            };

            Add(new NodeSpec("race",
                new[]
                {
                    Id(), Loc("name"), new FieldSpec("body", FieldKind.Choice) { Choices = MobBodies.All.Keys.ToArray(), Required = true }, Pick("faction", "factions"), Pick("animSet", "animSets"),
                    Pick("soundSet", "soundSets"), List("loot", "lootTables"), Loc("nameFormat")
                },
                new[] { Obj("appearance", "appearance"), Obj("stats", "stats"), Skills() }));

            Add(new NodeSpec("role",
                new[] { Id(), Loc("name"), Pick("aiProfile", "aiProfiles"), List("loot", "lootTables"), List("gear", "items:wearable"), Pick("rightHand", "items:hand"), Pick("leftHand", "items:hand") },
                new[] { Obj("statMul", "statMul"), Obj("statAdd", "statAdd"), Skills(), AnimEntries("animOverrides"), SoundEntries("soundOverrides") }));

            Add(new NodeSpec("mob",
                new[]
                {
                    Id(), Loc("name"), Pick("race", "races"), Pick("role", "roles"), IntRange("levelRange", 1, 1000), Pick("aiProfile", "aiProfiles"),
                    Pick("faction", "factions"), Pick("defaultTier", "tiers"), Pick("animSet", "animSets"), Pick("soundSet", "soundSets"),
                    Choice("skillsMode", MergeModes.All), Choice("lootMode", MergeModes.All), List("loot", "lootTables")
                },
                new[]
                {
                    Obj("stats", "stats"), Obj("appearance", "appearanceOverride"), Skills(),
                    Arr("phases", "phase", () => new JObject { ["id"] = "phase" + ++counter, ["hpBelow"] = 0.5 })
                }));

            Add(new NodeSpec("aiProfile",
                new[]
                {
                    Id(), Pick("archetype", "archetypes"), Num("aggroRange", 1, 64), Num("leashRange", 0, 256), Num("callForHelpRange", 0, 64),
                    Range("preferredRange", 0, 48), Num("fleeBelowHp", 0, 0.99f), Num("followRange", 1, 32)
                },
                new[] { Obj("targeting", "targeting"), Obj("melee", "melee"), Obj("ranged", "ranged") }));
            // from the registry, includes add-on selectors
            Add(new NodeSpec("targeting", new[] { Pick("selector", "enemySelectors"), Num("switchCooldown", 0, 30) }));
            Add(new NodeSpec("melee", new[]
            {
                Num("damageMul", 0, 100), Pick("damageType", "damageTypes"), Int("attackDurationMs", 1, 10000), Int("hitAtMs", 0, 10000),
                IntRange("cooldownMs", 0, 60000), Pick("animation", "animKeys"), Num("reach", 0, 8)
            }));
            Add(new NodeSpec("ranged", new[]
            {
                Pick("arrow", "items:arrow"), Num("damageMul", 0, 100), Pick("damageType", "damageTypes"), Int("aimMs", 100, 10000), IntRange("cooldownMs", 0, 60000),
                Num("speed", 1, 100), Num("dispersion", 0, 45), Num("minRange", 0, 32), Pick("animation", "animKeys")
            }));

            Add(new NodeSpec("faction",
                new[]
                {
                    Id(), Loc("name"),
                    new FieldSpec("relations", FieldKind.KeyValue) { PickList = "factionsAny", ValueChoices = Relations },
                    List("attackOnSight", "factionsAny"), Toggle("helpAllies", true)
                },
                new[] { Obj("members", "members") }));
            Add(new NodeSpec("members", new[] { List("entityCodes", null) }));

            Add(new NodeSpec("tier", new[]
            {
                Id(), Loc("name"), Loc("nameFormat"), Num("hp", 0.01f, 100), Num("damage", 0, 100), Num("spellPower", 0.01f, 100),
                Num("damageTaken", 0, 100), Int("lootRolls", 0, 20), Num("size", 0.2f, 5), Num("ccDurationMul", 0, 10)
            }));

            Add(new NodeSpec("lootTable",
                new[] { Id(), IntRange("rolls", 0, 100), Num("emptyWeight", 0, 100000), Num("levelCountScale", 0, 10), Toggle("tierRollBonus", true) },
                new[]
                {
                    Arr("entries", "lootEntry", () => new JObject { ["code"] = "" }),
                    Arr("guaranteed", "lootEntry", () => new JObject { ["code"] = "" })
                }));
            Add(new NodeSpec("lootEntry", new[]
            {
                Choice("type", LootEntryModel.Types), Pick("code", "lootCodes"), Num("weight", 0, 100000), IntRange("count", 0, 1000),
                Num("chance", 0, 1), Int("minLevel", 0, 1000), List("tiers", "tiers")
            }));

            Add(new NodeSpec("animSet", new[] { Id(), Pick("extends", "animSets") }, new[] { AnimEntries("anims") }));
            Add(new NodeSpec("animEntry", new[]
            {
                Pick("animation", "shapeAnims"), Num("speed", 0.01f, 20), Choice("blendMode", "Average", "Add", "AddAverage"), Num("weight", 0, 1000),
                Num("easeIn", 0, 1000), Num("easeOut", 0, 1000), List("onControls", "activities"), Toggle("defaultAnim")
            }));

            Add(new NodeSpec("soundSet", new[] { Id(), Pick("extends", "soundSets"), Num("idleChance", 0, 100) }, new[] { SoundEntries("sounds") }));
            Add(new NodeSpec("soundEntry", new[] { Sound("path"), Num("chance", 0, 1), Num("range", 1, 128), Num("volume", 0, 4), Num("pitchVariance", 0, 1) }));

            Add(new NodeSpec("group", new[] { Id(), Loc("name"), Num("radius", 0, 32), Pick("faction", "factions") },
                new[] { Arr("members", "groupMember", () => new JObject { ["mob"] = "", ["count"] = new JArray(1, 1) }) }));
            Add(new NodeSpec("groupMember", new[] { Pick("mob", "mobKeys"), IntRange("count", 0, 50), Pick("tier", "tiers"), Int("level", 0, 1000) }));

            // polygon has no form, it's edited on the map
            Add(new NodeSpec("zone",
                new[]
                {
                    Id(), Loc("name"), Toggle("enabled", true), Int("dimension", 0, 1000), IntRange("y", -64, 1024), Int("priority", -1000, 1000), Text("color"),
                    IntRange("level", 1, 1000), new FieldSpec("tiers", FieldKind.KeyValue) { PickList = "tiers", NumberValues = true }, Pick("faction", "factions"),
                    Int("maxAlive", 0, 500), Int("perPlayer", 0, 100), Num("respawnSeconds", 0, 86400), IntRange("spawnDistance", 0, 128),
                    Choice("surface", ZoneRules.Surfaces), IntRange("light", 0, 32), Choice("vanilla", ZoneRules.VanillaModes), List("vanillaDeny", null),
                    Choice("leash", ZoneRules.LeashModes), Num("despawnAfterSeconds", 0, 86400), Toggle("announce", true)
                },
                new[] { Arr("spawns", "zoneSpawn", () => new JObject { ["mob"] = "", ["weight"] = 1 }) }));
            Add(new NodeSpec("zoneSpawn", new[]
            {
                Pick("mob", "mobKeys"), Pick("group", "groups"), Num("weight", 0, 100000), IntRange("count", 1, 50), Choice("time", ZoneRules.Times),
                IntRange("level", 1, 1000), Pick("tier", "tiers")
            }));

            Add(new NodeSpec("stats", StatFields(false)));
            Add(new NodeSpec("statMul", new[]
            {
                Num("maxHealth", 0.01f, 100), Num("meleeDamage", 0, 100), Num("moveSpeed", 0, 10), Num("spellPower", 0.01f, 100), Num("healingPower", 0, 100)
            }));
            Add(new NodeSpec("statAdd", StatFields(true).Where(f => f.Key != "meleeTier").ToArray()));

            var textures = new FieldSpec("textures", FieldKind.KeyValue) { ValuePickList = "textures" };
            Add(new NodeSpec("appearance",
                new[]
                {
                    Choice("kind", AppearanceKinds.Plm, AppearanceKinds.Shape), Pick("model", "plmModels"), Pick("shape", "shapes"), textures,
                    Num("size", 0.1f, 5), Range("hitbox", 0.1f, 8), Num("eyeHeight", 0, 8), List("gear", "items:wearable"), Pick("rightHand", "items:hand"), Pick("leftHand", "items:hand")
                },
                new[] { Arr("presets", "preset", () => new JObject { ["id"] = "preset" + ++counter, ["weight"] = 1 }) }));
            Add(new NodeSpec("appearanceOverride",
                new[]
                {
                    Pick("model", "plmModels"), Pick("shape", "shapes"), textures, Num("size", 0.1f, 5), Range("hitbox", 0.1f, 8), Num("eyeHeight", 0, 8),
                    List("gear", "items:wearable"), Pick("rightHand", "items:hand"), Pick("leftHand", "items:hand")
                },
                new[] { Arr("presets", "preset", () => new JObject { ["id"] = "preset" + ++counter, ["weight"] = 1 }) }));
            Add(new NodeSpec("preset", new[]
            {
                Text("id"), Num("weight", 0, 1000),
                new FieldSpec("parts", FieldKind.KeyValue) { PickList = "plmParts", ValuePickList = "plmVariants" },
                textures, List("gear", "items:wearable"), Pick("rightHand", "items:hand"), Pick("leftHand", "items:hand")
            }));

            Add(new NodeSpec("skill",
                new[]
                {
                    Text("id"), Pick("spell", "spells"), Pick("target", "selectors"), Num("priority", -10000, 10000), Num("weight", 0, 1000),
                    Num("minRange", 0, 128), Num("maxRange", 0, 128), Num("cooldown", 0, 3600), Num("cooldownJitter", 0, 1),
                    Choice("cooldownMode", CooldownModes.All), Num("gcd", 0, 30), Range("initialCooldown", 0, 3600),
                    new FieldSpec("requireLineOfSight", FieldKind.TriState), Toggle("stopToCast", true), Toggle("faceTarget", true), Int("recoverMs", 0, 10000)
                },
                new[]
                {
                    Arr("conditions", "condition", () => new JObject { ["type"] = "chance", ["value"] = 0.5 }),
                    Obj("action", "action"), Obj("animation", "skillAnim"), Obj("sound", "skillSound"), Obj("interrupt", "interrupt")
                },
                token => ParamFields(vocabulary.Selectors, (string?)token["target"] ?? SkillTargets.CurrentEnemy)));
            Add(new NodeSpec("condition", new[] { Pick("type", "conditions"), Toggle("not") },
                dynamicFields: token => ParamFields(vocabulary.Conditions, (string?)token["type"] ?? "")));
            Add(new NodeSpec("skillAnim", new[] { Pick("cast", "animKeys"), Pick("release", "animKeys") }));
            Add(new NodeSpec("skillSound", new[] { Sound("start"), Sound("release") }));
            Add(new NodeSpec("interrupt", new[] { Toggle("onDamage", true), Num("damageFraction", 0, 1), Toggle("onMove"), Choice("onTargetLost", "cancel", "finish") }));
            Add(new NodeSpec("action", new[] { Pick("type", "actions"), Toggle("despawnWithOwner", true) },
                dynamicFields: token => ParamFields(vocabulary.Actions, (string?)token["type"] ?? "")));
            Add(new NodeSpec("phase",
                new[]
                {
                    Text("id"), Num("hpBelow", 0.01f, 0.99f), Loc("announce"), Sound("sound"), Pick("animation", "animKeys"),
                    List("ccImmune", "controls"), Num("ccDurationMul", 0, 10), Num("damageMul", 0, 100), Num("spellPowerMul", 0.01f, 100),
                    Num("damageTakenMul", 0, 100)
                },
                new[] { Arr("actions", "action", () => new JObject { ["type"] = "enrage", ["value"] = 1.5 }) }));
        }

        private void Add(NodeSpec node) => nodes[node.Id] = node;

        // add-on conditions/selectors get a form from their registered params
        private static IEnumerable<FieldSpec> ParamFields(IReadOnlyDictionary<string, VocabularyEntry> entries, string id)
        {
            if (!entries.TryGetValue(id, out var entry)) yield break;
            foreach (var p in entry.Params)
            {
                yield return p.Kind switch
                {
                    ParamKind.Effect => new FieldSpec(p.Name, FieldKind.Pick) { PickList = "effects" },
                    ParamKind.Mob => new FieldSpec(p.Name, FieldKind.Pick) { PickList = "mobKeys" },
                    ParamKind.Count => new FieldSpec(p.Name, FieldKind.Int) { Min = p.Min, Max = p.Max },
                    _ => new FieldSpec(p.Name, FieldKind.Number) { Min = p.Min, Max = p.Max }
                };
            }
        }
    }
}
