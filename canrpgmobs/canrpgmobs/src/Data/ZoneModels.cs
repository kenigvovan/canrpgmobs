using System.Collections.Generic;

namespace canrpgmobs.Data
{
    public class ZoneModel : CatalogEntryModel
    {
        public LocText? name { get; set; }
        public bool enabled { get; set; } = true;
        public int dimension { get; set; }
        // [[x, z], ...], edges must not cross
        public List<int[]>? polygon { get; set; }
        // null = full height
        public int[]? y { get; set; }
        // higher wins on overlap
        public int priority { get; set; }
        // #rrggbb, null = by level
        public string? color { get; set; }

        // null = each mob's own range
        public int[]? level { get; set; }
        // a spawn's own tier wins
        public Dictionary<string, float>? tiers { get; set; }
        public string? faction { get; set; }
        public List<ZoneSpawnModel>? spawns { get; set; }

        public int maxAlive { get; set; } = 20;
        // per player inside, up to maxAlive
        public int perPlayer { get; set; } = 6;
        public float respawnSeconds { get; set; } = 60f;
        public int[]? spawnDistance { get; set; }
        public string surface { get; set; } = ZoneRules.Ground;
        public int[]? light { get; set; }

        public string vanilla { get; set; } = ZoneRules.Keep;
        public List<string>? vanillaDeny { get; set; }
        public string leash { get; set; } = ZoneRules.LeashSpawn;
        // no player near this long = gone, 0 = never
        public float despawnAfterSeconds { get; set; } = 300f;
        public bool announce { get; set; } = true;
    }

    public class ZoneSpawnModel
    {
        // or group
        public string? mob { get; set; }
        public string? group { get; set; }
        public float weight { get; set; } = 1f;
        // ignored for groups
        public int[]? count { get; set; }
        public string time { get; set; } = ZoneRules.AnyTime;
        public int[]? level { get; set; }
        public string? tier { get; set; }
    }

    public static class ZoneRules
    {
        public const string Ground = "ground";
        public const string Cave = "cave";
        public const string AnySurface = "any";
        public static readonly string[] Surfaces = { Ground, Cave, AnySurface };

        public const string Keep = "keep";
        public const string NoVanilla = "none";
        public const string Deny = "deny";
        public static readonly string[] VanillaModes = { Keep, NoVanilla, Deny };

        public const string LeashSpawn = "spawn";
        public const string LeashZone = "zone";
        public static readonly string[] LeashModes = { LeashSpawn, LeashZone };

        public const string AnyTime = "any";
        public static readonly string[] Times = { AnyTime, "day", "night" };

        public static readonly int[] DefaultSpawnDistance = { 24, 64 };
    }
}
