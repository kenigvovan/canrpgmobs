using System;
using System.Globalization;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;

namespace canrpgmobs.Runtime.Zones
{
    public static class ZoneText
    {
        public static string Name(ZoneModel zone, string? locale) => zone.name?.Resolve(locale, ShortId(zone.id)) ?? ShortId(zone.id);

        // "10-15", "7", or "" when each mob keeps its own
        public static string Levels(ZoneModel zone)
            => zone.level is not { Length: 2 } l ? "" : l[0] == l[1] ? l[0].ToString() : $"{l[0]}-{l[1]}";

        public static string ShortId(string id)
            => id.StartsWith(canrpgmobsModSystem.ModId + ":") ? id.Substring(canrpgmobsModSystem.ModId.Length + 1) : id;

        // as the coordinates HUD shows them
        public static (int X, int Z) ToLocal(ICoreAPI api, int x, int z)
            => (x - (int)api.World.DefaultSpawnPosition.X, z - (int)api.World.DefaultSpawnPosition.Z);

        // from a zone: its color; own mob: white; a problem: red; off: grey
        public static int SpawnerRgb(ICoreAPI api, BEMobSpawner spawner, int maxLevel)
        {
            if (!spawner.Config.IsSet) return 0x808080;
            if (spawner.LastError != null) return 0xF04030;
            if (!spawner.Config.FromZone) return 0xFFFFFF;
            return spawner.ZoneHere(api)?.Model is { } zone ? Rgb(zone, maxLevel) : 0xF04030;
        }

        public static (int R, int G, int B) Channels(int rgb) => ((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

        // the zone's own color, else green at level 1 to red at the cap
        public static int Rgb(ZoneModel zone, int maxLevel)
        {
            if (zone.color is { Length: 7 } hex && hex[0] == '#' && int.TryParse(hex.Substring(1), NumberStyles.HexNumber, null, out int rgb))
                return rgb;
            if (zone.level is not { Length: 2 } level) return 0x6E9BD1;
            float t = maxLevel <= 1 ? 0f : Math.Clamp((level[0] + level[1]) / 2f / maxLevel, 0f, 1f);
            int r = (int)(80 + 175 * Math.Min(1f, t * 2f));
            int g = (int)(200 - 160 * Math.Max(0f, t * 2f - 1f));
            return (r << 16) | (g << 8) | 70;
        }
    }
}
