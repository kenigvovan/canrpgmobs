using System;
using Vintagestory.API.Datastructures;

namespace canrpgmobs.Runtime.Spawning
{
    // GroupId wins over MobKey if both are set
    public sealed class SpawnerConfig
    {
        // saved in the block entity tree, don't rename
        private const string MobAttr = "sp_mob";
        private const string GroupAttr = "sp_group";
        private const string LevelAttr = "sp_level";
        private const string TierAttr = "sp_tier";
        private const string FactionAttr = "sp_faction";
        private const string MaxAttr = "sp_max";
        private const string RespawnAttr = "sp_respawn";
        private const string RadiusAttr = "sp_radius";
        private const string ActivationAttr = "sp_activation";
        private const string UseZoneAttr = "sp_usezone";

        // template id or race|role
        public string? MobKey { get; set; }
        public string? GroupId { get; set; }
        // 0 = template's level range
        public int Level { get; set; }
        // MobKey spawns only, group members use their own
        public string? Tier { get; set; }
        public string? Faction { get; set; }
        public int MaxAlive { get; set; } = 3;
        // first fill is immediate
        public float RespawnSeconds { get; set; } = 60f;
        public float Radius { get; set; } = 4f;
        public float ActivationRange { get; set; } = 32f;
        // no mob or group: pick from the zone the block stands in; set fields still win
        public bool UseZone { get; set; }

        public bool IsSet => !string.IsNullOrWhiteSpace(MobKey) || !string.IsNullOrWhiteSpace(GroupId) || UseZone;
        public bool FromZone => UseZone && string.IsNullOrWhiteSpace(MobKey) && string.IsNullOrWhiteSpace(GroupId);

        public SpawnerConfig Clamped() => new()
        {
            MobKey = Blank(MobKey), GroupId = Blank(GroupId), Level = Math.Clamp(Level, 0, 1000), Tier = Blank(Tier), Faction = Blank(Faction),
            MaxAlive = Math.Clamp(MaxAlive, 1, 50), RespawnSeconds = Finite(RespawnSeconds, 0f, 86400f, 60f),
            Radius = Finite(Radius, 0f, 32f, 4f), ActivationRange = Finite(ActivationRange, 4f, 128f, 32f), UseZone = UseZone
        };

        internal void ToTree(ITreeAttribute tree)
        {
            tree.SetString(MobAttr, MobKey ?? "");
            tree.SetString(GroupAttr, GroupId ?? "");
            tree.SetInt(LevelAttr, Level);
            tree.SetString(TierAttr, Tier ?? "");
            tree.SetString(FactionAttr, Faction ?? "");
            tree.SetInt(MaxAttr, MaxAlive);
            tree.SetFloat(RespawnAttr, RespawnSeconds);
            tree.SetFloat(RadiusAttr, Radius);
            tree.SetFloat(ActivationAttr, ActivationRange);
            tree.SetBool(UseZoneAttr, UseZone);
        }

        internal static SpawnerConfig FromTree(ITreeAttribute tree) => new SpawnerConfig
        {
            MobKey = tree.GetString(MobAttr), GroupId = tree.GetString(GroupAttr), Level = tree.GetInt(LevelAttr),
            Tier = tree.GetString(TierAttr), Faction = tree.GetString(FactionAttr), MaxAlive = tree.GetInt(MaxAttr, 3),
            RespawnSeconds = tree.GetFloat(RespawnAttr, 60f), Radius = tree.GetFloat(RadiusAttr, 4f), ActivationRange = tree.GetFloat(ActivationAttr, 32f),
            UseZone = tree.GetBool(UseZoneAttr)
        }.Clamped();

        private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        private static float Finite(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
