using System;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Client.Editor.Controllers
{
    // Ready-made skill rules for simple mode, numbers taken from the content packs.
    public static class SkillPresets
    {
        public static readonly string[] SpellUses = { "attack", "cluster", "heal", "buff" };

        public static JObject Spell(string use, string spellId, string ruleId, float spellRange)
        {
            float maxRange = spellRange > 0 ? Math.Clamp(spellRange, 3f, 32f) : 16f;
            var rule = new JObject { ["id"] = ruleId, ["spell"] = spellId };
            switch (use)
            {
                case "heal":
                    rule["target"] = SkillTargets.LowestHpAlly;
                    rule["allyHpBelow"] = 0.6;
                    rule["priority"] = 70;
                    rule["maxRange"] = maxRange;
                    rule["cooldown"] = 8;
                    rule["interrupt"] = new JObject { ["onDamage"] = true, ["damageFraction"] = 0.25, ["onTargetLost"] = "finish" };
                    break;
                case "buff":
                    rule["target"] = SkillTargets.Self;
                    rule["priority"] = 80;
                    rule["cooldown"] = 30;
                    rule["conditions"] = new JArray(new JObject { ["type"] = "enemiesInRange", ["range"] = 8, ["min"] = 1 });
                    break;
                case "cluster":
                    rule["target"] = SkillTargets.EnemyCluster;
                    rule["clusterRadius"] = 4;
                    rule["priority"] = 60;
                    rule["maxRange"] = maxRange;
                    rule["cooldown"] = 10;
                    break;
                default:
                    rule["priority"] = 50;
                    rule["minRange"] = 0;
                    rule["maxRange"] = maxRange;
                    rule["cooldown"] = 6;
                    break;
            }
            rule["initialCooldown"] = new JArray(1, 3);
            rule["animation"] = new JObject { ["cast"] = AnimKeys.Cast, ["release"] = AnimKeys.Release };
            return rule;
        }

        public static JObject Action(string type, string ruleId) => type switch
        {
            "summon" => new JObject
            {
                ["id"] = ruleId,
                ["action"] = new JObject { ["type"] = "summon", ["mob"] = "", ["count"] = 2, ["maxAlive"] = 2, ["radius"] = 4 },
                ["target"] = SkillTargets.Self, ["priority"] = 85, ["cooldown"] = 40, ["initialCooldown"] = new JArray(6, 10),
                ["conditions"] = new JArray(new JObject { ["type"] = "inCombatFor", ["value"] = 4 }),
                ["animation"] = new JObject { ["cast"] = AnimKeys.Cast }
            },
            "enrage" => new JObject
            {
                ["id"] = ruleId,
                ["action"] = new JObject { ["type"] = "enrage", ["value"] = 1.4, ["seconds"] = 12 },
                ["target"] = SkillTargets.Self, ["priority"] = 90, ["cooldown"] = 45,
                ["conditions"] = new JArray(new JObject { ["type"] = "selfHpBelow", ["value"] = 0.5 }),
                ["animation"] = new JObject { ["cast"] = AnimKeys.Attack }
            },
            "steal" => new JObject
            {
                ["id"] = ruleId,
                ["action"] = new JObject { ["type"] = "steal", ["count"] = 1 },
                ["priority"] = 60, ["minRange"] = 0, ["maxRange"] = 2.5, ["cooldown"] = 30, ["initialCooldown"] = new JArray(4, 8),
                ["animation"] = new JObject { ["cast"] = AnimKeys.Attack }
            },
            _ => new JObject
            {
                ["id"] = ruleId, ["action"] = new JObject { ["type"] = type }, ["target"] = SkillTargets.Self, ["priority"] = 60, ["cooldown"] = 20
            }
        };

        // null = has to be picked by hand (effects, mobs)
        public static float? DefaultParam(ParamInfo param) => param.Kind switch
        {
            ParamKind.Fraction => 0.5f,
            ParamKind.Blocks => 8f,
            ParamKind.Seconds => 5f,
            ParamKind.Count => 1f,
            ParamKind.Multiplier => 1.5f,
            _ => null
        };
    }
}
