using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace canrpgmobs.Data
{
    public static partial class MobValidator
    {
        private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        // a bad outline or level rejects the zone, the rest only warns
        private static void CheckZone(ZoneModel zone, Scope scope, FactionMatrix factions, Rejected rejected)
        {
            var report = scope.Report;
            string path = Path(zone.source, "zone", zone.id);
            CheckText(zone.name, path + " > name", report);

            var corners = ZoneGeometry.Corners(zone.polygon);
            if (corners == null)
            {
                report.Error(path, "polygon needs at least 3 corners, each [x, z]");
                rejected.Zones.Add(zone.id);
            }
            else if (ZoneGeometry.SelfIntersects(corners, out int a, out int b))
            {
                report.Error(path, $"polygon edges {a + 1} and {b + 1} cross or fold back, the outline must not cross itself");
                rejected.Zones.Add(zone.id);
            }
            else if (ZoneGeometry.Area(corners) < 1)
            {
                report.Error(path, "polygon has no area");
                rejected.Zones.Add(zone.id);
            }

            if (zone.y != null && !(zone.y.Length == 2 && zone.y[0] <= zone.y[1]))
            {
                report.Error(path, "y must be [min, max] with min not above max");
                rejected.Zones.Add(zone.id);
            }

            int maxLevel = (scope.Data.Scaling ?? new ScalingModel()).maxLevel;
            if (zone.level != null)
            {
                if (!IntRange(zone.level, 1, 1000))
                {
                    report.Error(path, "level must be [min, max] from 1, with min not above max");
                    rejected.Zones.Add(zone.id);
                }
                else if (zone.level[1] > maxLevel) report.Warning(path, $"level goes above scaling.maxLevel {maxLevel}, mobs are capped there");
            }

            if (zone.color != null && !HexColor.IsMatch(zone.color)) report.Warning(path, $"color '{zone.color}' is not #rrggbb, the level color is used");
            CheckFactionRef(zone.faction, path, factions, report);
            if (zone.tiers != null)
                foreach (var (tier, weight) in zone.tiers)
                {
                    if (tier != StatMath.DefaultTier && !scope.Data.Tiers.ContainsKey(tier)) report.Warning(path, $"tiers: unknown tier '{tier}', never picked");
                    if (!NonNegative(weight)) report.Warning(path, $"tiers: weight of '{tier}' must be 0 or more");
                }

            if (zone.maxAlive < 0 || zone.maxAlive > 500) report.Warning(path, "maxAlive should be between 0 and 500");
            if (zone.perPlayer < 0 || zone.perPlayer > 100) report.Warning(path, "perPlayer should be between 0 and 100");
            if (!InRange(zone.respawnSeconds, 0f, 86400f)) report.Warning(path, "respawnSeconds should be between 0 and 86400");
            if (!InRange(zone.despawnAfterSeconds, 0f, 86400f)) report.Warning(path, "despawnAfterSeconds should be between 0 (never) and 86400");
            if (zone.spawnDistance != null && !IntRange(zone.spawnDistance, 0, 128))
                report.Warning(path, "spawnDistance must be [min, max] between 0 and 128 blocks, with min not above max");
            else if (corners != null)
            {
                // every spot inside would be too close to a player standing in the zone
                int minDistance = (zone.spawnDistance ?? ZoneRules.DefaultSpawnDistance)[0];
                int width = zone.polygon!.Max(p => p[0]) - zone.polygon!.Min(p => p[0]);
                int depth = zone.polygon!.Max(p => p[1]) - zone.polygon!.Min(p => p[1]);
                if (Math.Max(width, depth) < minDistance * 2)
                    report.Warning(path, $"zone is {width}x{depth} blocks, smaller than twice spawnDistance min {minDistance}: most spots are too close to a player inside, lower spawnDistance");
            }
            if (zone.light != null && !IntRange(zone.light, 0, 32)) report.Warning(path, "light must be [min, max] between 0 and 32, with min not above max");
            if (!ZoneRules.Surfaces.Contains(zone.surface)) report.Warning(path, $"surface '{zone.surface}' is not ground, cave or any");
            if (!ZoneRules.LeashModes.Contains(zone.leash)) report.Warning(path, $"leash '{zone.leash}' is not spawn or zone");
            if (!ZoneRules.VanillaModes.Contains(zone.vanilla)) report.Warning(path, $"vanilla '{zone.vanilla}' is not keep, none or deny");
            else if (zone.vanilla != ZoneRules.Deny && zone.vanillaDeny is { Count: > 0 }) report.Info(path, "vanillaDeny is only read with vanilla: deny");
            else if (zone.vanilla == ZoneRules.Deny && zone.vanillaDeny is not { Count: > 0 }) report.Warning(path, "vanilla: deny without vanillaDeny codes blocks nothing");

            if (zone.spawns is not { Count: > 0 })
            {
                report.Info(path, "no spawns, only spawner blocks set to use the zone put mobs here");
                return;
            }
            for (int i = 0; i < zone.spawns.Count; i++)
            {
                var spawn = zone.spawns[i];
                if (spawn == null) continue;
                bool hasMob = !string.IsNullOrWhiteSpace(spawn.mob), hasGroup = !string.IsNullOrWhiteSpace(spawn.group);
                string at = $"{path} > spawns[{i}] {spawn.mob ?? spawn.group}";
                if (hasMob == hasGroup)
                {
                    report.Warning(at, "set either mob or group, the entry is skipped");
                    continue;
                }
                if (hasMob && MobResolver.Resolve(spawn.mob!, scope.Data, null, out var error) == null) report.Warning(at, $"mob '{spawn.mob}' does not resolve: {error}");
                if (hasGroup && !scope.Data.Groups.ContainsKey(spawn.group!)) report.Warning(at, $"unknown group '{spawn.group}'");
                if (!Positive(spawn.weight)) report.Warning(at, "weight must be above 0, the entry is never picked");
                if (spawn.count != null && !IntRange(spawn.count, 1, 50)) report.Warning(at, "count must be [min, max] between 1 and 50, with min not above max");
                if (hasGroup && spawn.count != null) report.Info(at, "count is ignored for groups, they bring their own");
                if (!ZoneRules.Times.Contains(spawn.time)) report.Warning(at, $"time '{spawn.time}' is not any, day or night");
                if (spawn.level != null && !IntRange(spawn.level, 1, 1000)) report.Warning(at, "level must be [min, max] from 1, with min not above max");
                if (UnknownTier(scope.Data, spawn.tier))
                    report.Warning(at, $"unknown tier '{spawn.tier}', the entry is not spawned");
            }
        }

        // same priority and dimension leaves the winner to the id order
        private static void CheckZoneOverlaps(CatalogData data, Rejected rejected, ValidationReport report)
        {
            var zones = data.Zones.Values.Where(z => z.enabled && !rejected.Zones.Contains(z.id))
                .Select(z => (Zone: z, Corners: ZoneGeometry.Corners(z.polygon)!))
                .OrderBy(z => z.Zone.id, StringComparer.Ordinal).ToList();
            for (int i = 0; i < zones.Count; i++)
                for (int j = i + 1; j < zones.Count; j++)
                {
                    var (a, ca) = zones[i];
                    var (b, cb) = zones[j];
                    if (a.dimension != b.dimension || a.priority != b.priority || !HeightsMeet(a, b)) continue;
                    if (ZoneGeometry.Overlaps(ca, cb))
                        report.Warning(Path(a.source, "zone", a.id), $"overlaps zone {b.id} with the same priority {a.priority}: {a.id} wins there by id order, give one a higher priority");
                }
        }

        private static bool HeightsMeet(ZoneModel a, ZoneModel b)
            => a.y is not { Length: 2 } ya || b.y is not { Length: 2 } yb || (ya[0] <= yb[1] && yb[0] <= ya[1]);
    }
}
