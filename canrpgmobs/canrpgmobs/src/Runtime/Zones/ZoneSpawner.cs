using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canrpgmobs.Runtime.Zones
{
    // only zones with a player inside spawn; one batch per respawnSeconds, all at once when someone walks in
    public sealed class ZoneSpawner
    {
        public const int TickMs = 2000;
        private const int TriesPerTick = 4;
        // the rest wait for the next tick
        private const int BatchesPerTick = 6;
        private const int KeptAttempts = 40;
        private const long AttemptsKeptMs = 30000;
        private const long WindowMs = 10 * 60 * 1000;
        private const long RetryMs = 5000;
        private const int GroupSpread = 5;
        private const int ZonePointTries = 8;

        public sealed record Attempt(double X, double Y, double Z, bool Ok, string Reason, long AtMs);

        public sealed class State
        {
            public readonly HashSet<long> Alive = new();
            public readonly Dictionary<long, long> LastNearMs = new();
            public readonly Queue<long> SpawnedRecent = new();
            public readonly Queue<long> KilledRecent = new();
            public readonly Dictionary<string, int> Failures = new();
            public readonly List<Attempt> Attempts = new();
            public long NextSpawnMs;
            public bool Filling = true;
            public int PlayersInside;
            public int Cap;
            public long Spawned, Killed, Despawned;
            public string? LastError;
        }

        private readonly Dictionary<string, State> states = new();
        private readonly Dictionary<string, List<(double X, double Y, double Z, int Dimension)>> inside = new();
        private readonly BlockPos probe = new(0);
        private readonly List<long> aliveCopy = new();

        public State? Get(string zoneId) => states.GetValueOrDefault(zoneId);

        private State For(string zoneId)
        {
            if (!states.TryGetValue(zoneId, out var state)) states[zoneId] = state = new State();
            return state;
        }

        public static string? ZoneOf(Entity entity) => MobKeys.Identity(entity)?.GetString(MobKeys.Zone) is { Length: > 0 } id ? id : null;

        // spawned or loaded with its chunk
        public void Track(Entity entity, long now)
        {
            if (ZoneOf(entity) is not { } id) return;
            var state = For(id);
            if (state.Alive.Add(entity.EntityId)) state.LastNearMs[entity.EntityId] = now;
        }

        public void OnDied(Entity entity, long now)
        {
            if (ZoneOf(entity) is not { } id || !states.TryGetValue(id, out var state)) return;
            if (!state.Alive.Contains(entity.EntityId)) return;
            Forget(state, entity.EntityId);
            state.Killed++;
            state.KilledRecent.Enqueue(now);
        }

        public void Tick(ICoreServerAPI sapi, canrpgmobsModSystem mod)
        {
            long now = sapi.World.ElapsedMilliseconds;
            var spots = mod.PlayerSpots(sapi);
            var index = mod.Catalog.ZoneIndex;
            foreach (var list in inside.Values) list.Clear();
            foreach (var spot in spots)
            {
                if (index.At(spot.Dimension, spot.X, spot.Y, spot.Z) is not { } shape) continue;
                if (!inside.TryGetValue(shape.Model.id, out var list)) inside[shape.Model.id] = list = new();
                list.Add(spot);
            }

            foreach (var (id, state) in states)
            {
                var zone = index.Get(id)?.Model;
                Prune(sapi, zone, state, spots, now);
                bool someone = inside.TryGetValue(id, out var here) && here.Count > 0;
                state.PlayersInside = someone ? here!.Count : 0;
                // walking in again fills the zone at once
                if (!someone) state.Filling = true;
            }

            int batches = 0;
            foreach (var (id, players) in inside)
            {
                if (players.Count == 0 || index.Get(id) is not { } shape) continue;
                var zone = shape.Model;
                var state = For(id);
                state.PlayersInside = players.Count;
                state.Cap = Cap(zone, players.Count);
                if (zone.spawns is not { Count: > 0 }) continue;
                if (state.Alive.Count >= state.Cap)
                {
                    state.Filling = false;
                    state.NextSpawnMs = 0;
                    continue;
                }
                if (state.NextSpawnMs == 0) state.NextSpawnMs = now + (state.Filling ? 0 : (long)(zone.respawnSeconds * 1000));
                if (now < state.NextSpawnMs || batches >= BatchesPerTick) continue;
                batches++;

                int made = SpawnBatch(sapi, mod, shape, state, players, now, TriesPerTick);
                state.NextSpawnMs = made == 0 ? now + RetryMs : state.Filling ? now : now + (long)(zone.respawnSeconds * 1000);
            }
        }

        public static int Cap(ZoneModel zone, int players) => Math.Min(zone.maxAlive, zone.perPlayer * Math.Max(0, players));

        // up to the cap around the given player, ignoring the respawn timer
        public int Fill(ICoreServerAPI sapi, canrpgmobsModSystem mod, ZoneShape shape, (double X, double Y, double Z, int Dimension) at, out State state)
        {
            state = For(shape.Model.id);
            long now = sapi.World.ElapsedMilliseconds;
            Prune(sapi, shape.Model, state, mod.PlayerSpots(sapi), now);
            int cap = Cap(shape.Model, Math.Max(1, state.PlayersInside));
            var players = new List<(double, double, double, int)> { at };
            int made = 0;
            for (int round = 0; round < 12 && state.Alive.Count < cap; round++)
                made += SpawnBatch(sapi, mod, shape, state, players, now, TriesPerTick, cap);
            return made;
        }

        // loaded zone mobs, tracked or not
        public int Clear(ICoreServerAPI sapi, string zoneId)
        {
            int removed = 0;
            foreach (var entity in sapi.World.LoadedEntities.Values.ToList())
            {
                if (!entity.Alive || ZoneOf(entity) != zoneId) continue;
                entity.Die(EnumDespawnReason.Removed);
                removed++;
            }
            if (states.TryGetValue(zoneId, out var state))
            {
                state.Alive.Clear();
                state.LastNearMs.Clear();
                state.NextSpawnMs = 0;
                state.Filling = true;
            }
            return removed;
        }

        // vanilla spawns, OnTrySpawnEntity; only the normal world is checked
        public static bool AllowVanilla(MobCatalog catalog, EntityProperties properties, Vec3d position)
        {
            if (properties.Code?.Domain == canrpgmobsModSystem.ModId) return true;
            if (catalog.ZoneIndex.At(0, position.X, position.Y, position.Z) is not { } shape) return true;
            var zone = shape.Model;
            if (zone.vanilla == ZoneRules.NoVanilla) return false;
            if (zone.vanilla != ZoneRules.Deny || zone.vanillaDeny == null) return true;
            foreach (var pattern in zone.vanillaDeny)
                if (!string.IsNullOrWhiteSpace(pattern) && WildcardUtil.Match(new AssetLocation(pattern.Trim()), properties.Code)) return false;
            return true;
        }

        private static int[] SpawnDistance(ZoneModel zone) => zone.spawnDistance is { Length: 2 } d ? d : ZoneRules.DefaultSpawnDistance;

        // a column at spawnDistance from (px, pz)
        private static (int x, int z) RingPoint(double px, double pz, int[] range, Random rand)
        {
            double angle = rand.NextDouble() * GameMath.TWOPI;
            double distance = range[0] + rand.NextDouble() * Math.Max(0, range[1] - range[0]);
            return ((int)Math.Floor(px + Math.Cos(angle) * distance), (int)Math.Floor(pz + Math.Sin(angle) * distance));
        }

        // the entry's own tier wins over the zone's weights
        internal static string? EntryTier(ZoneModel zone, ZoneSpawnModel entry, Random rand)
            => !string.IsNullOrEmpty(entry.tier) ? entry.tier : PickTier(zone, rand);

        private static void Forget(State state, long id)
        {
            state.Alive.Remove(id);
            state.LastNearMs.Remove(id);
        }

        private int SpawnBatch(ICoreServerAPI sapi, canrpgmobsModSystem mod, ZoneShape shape, State state,
            List<(double X, double Y, double Z, int Dimension)> players, long now, int tries, int? capOverride = null)
        {
            var zone = shape.Model;
            var rand = sapi.World.Rand;
            int cap = capOverride ?? state.Cap;
            var range = SpawnDistance(zone);
            for (int t = 0; t < tries; t++)
            {
                // half the tries anywhere in the zone, so a player at its edge doesn't get every mob on one side
                int x, z;
                if (t % 2 == 0 && RandomInZone(shape, players, range, rand) is { } inside) (x, z) = inside;
                else
                {
                    var player = players[rand.Next(players.Count)];
                    (x, z) = RingPoint(player.X, player.Z, range, rand);
                }

                if (FindSpot(sapi, mod, shape, x, z, players, range[0], out var spot, out string reason) is not { } pos)
                {
                    Fail(state, spot.X, spot.Y, spot.Z, reason, now);
                    continue;
                }
                bool day = IsDay(sapi, x, z);
                if (Pick(zone, day, rand) is not { } entry)
                {
                    Fail(state, pos.X, pos.Y, pos.Z, day ? "nothing spawns by day" : "nothing spawns at night", now);
                    continue;
                }

                int? level = Roll(entry.level ?? zone.level, rand);
                string? tier = EntryTier(zone, entry, rand);
                // one spot never takes more than half the cap, the rest go elsewhere
                int room = Math.Max(1, Math.Min(cap - state.Alive.Count, (cap + 1) / 2));
                string? error;
                var made = !string.IsNullOrWhiteSpace(entry.group)
                    ? MobSpawner.TrySpawnGroup(sapi, entry.group!, pos, level, zone.faction, room, out error, GroupSpread, requireFreeSpot: true, zoneId: zone.id)
                    : SpawnSingles(sapi, zone, entry, pos, level, tier, Math.Min(room, Roll(entry.count, rand) ?? 1), out error);

                if (made.Count == 0)
                {
                    Fail(state, pos.X, pos.Y, pos.Z, error ?? "could not spawn", now);
                    state.LastError = error;
                    continue;
                }
                foreach (var agent in made)
                {
                    state.Alive.Add(agent.EntityId);
                    state.LastNearMs[agent.EntityId] = now;
                    state.Spawned++;
                    state.SpawnedRecent.Enqueue(now);
                }
                Remember(state, new Attempt(pos.X + 0.5, pos.Y, pos.Z + 0.5, true, $"{made.Count} x {entry.mob ?? entry.group}", now));
                return made.Count;
            }
            return 0;
        }

        // first one on the spot, the rest nearby; stops at the first failure
        private static List<EntityAgent> SpawnSingles(ICoreServerAPI sapi, ZoneModel zone, ZoneSpawnModel entry, BlockPos pos, int? level, string? tier, int count, out string? error)
        {
            var made = new List<EntityAgent>();
            var rand = sapi.World.Rand;
            error = null;
            for (int i = 0; i < count; i++)
            {
                var at = i == 0 ? SpawnPlacement.CenterOf(pos) : SpawnPlacement.Find(sapi.World.BlockAccessor, pos, GroupSpread, rand);
                if (at == null) break;
                var request = new MobSpawnRequest(entry.mob!, at, level, SpawnPlacement.RandomYaw(rand), FactionOverride: zone.faction, Tier: tier, ZoneId: zone.id);
                if (MobSpawner.TrySpawn(sapi, request, out error) is not { } agent) break;
                made.Add(agent);
            }
            return made;
        }

        // a column inside the polygon within spawnDistance of some player; null after a few misses (huge zone, player at its edge)
        private static (int x, int z)? RandomInZone(ZoneShape shape, List<(double X, double Y, double Z, int Dimension)> players, int[] range, Random rand)
        {
            double min = range[0], max = Math.Max(range[0], range[1]);
            for (int i = 0; i < ZonePointTries; i++)
            {
                int x = shape.MinX + rand.Next(shape.MaxX - shape.MinX + 1), z = shape.MinZ + rand.Next(shape.MaxZ - shape.MinZ + 1);
                if (!ZoneGeometry.Contains(shape.Corners, x + 0.5, z + 0.5)) continue;
                foreach (var p in players)
                {
                    double dx = p.X - x - 0.5, dz = p.Z - z - 0.5, sq = dx * dx + dz * dz;
                    if (sq >= min * min && sq <= max * max) return (x, z);
                }
            }
            return null;
        }

        // pos = feet block; spot is filled even on failure, for the debug marks
        private BlockPos? FindSpot(ICoreServerAPI sapi, canrpgmobsModSystem mod, ZoneShape shape, int x, int z,
            List<(double X, double Y, double Z, int Dimension)> players, int minDistance, out (double X, double Y, double Z) spot, out string reason)
        {
            var zone = shape.Model;
            var ba = sapi.World.BlockAccessor;
            int dim = zone.dimension;
            spot = (x + 0.5, 0, z + 0.5);
            int top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0)
            {
                reason = "chunk not loaded";
                return null;
            }
            spot.Y = top + 1;

            string surface = zone.surface == ZoneRules.AnySurface ? (sapi.World.Rand.Next(2) == 0 ? ZoneRules.Ground : ZoneRules.Cave) : zone.surface;
            int from, to;
            if (surface == ZoneRules.Cave)
            {
                from = top - 6 - sapi.World.Rand.Next(Math.Max(1, top - 12));
                to = from - 16;
            }
            else
            {
                from = top + 1;
                to = top - 20;
            }
            if (shape.YMin != int.MinValue) to = Math.Max(to, shape.YMin);
            if (shape.YMax != int.MaxValue) from = Math.Min(from, shape.YMax);

            probe.dimension = dim;
            int found = int.MinValue;
            for (int y = from; y >= Math.Max(2, to); y--)
            {
                probe.Set(x, y, z);
                if (!SpawnPlacement.CanStand(ba, probe)) continue;
                found = y;
                break;
            }
            if (found == int.MinValue)
            {
                reason = surface == ZoneRules.Cave ? "no cave floor" : "no ground to stand on";
                return null;
            }
            spot.Y = found;

            if (mod.Catalog.ZoneIndex.At(dim, x + 0.5, found, z + 0.5)?.Model.id != zone.id)
            {
                reason = "outside the zone";
                return null;
            }
            foreach (var p in players)
            {
                double dx = p.X - x - 0.5, dz = p.Z - z - 0.5;
                if (dx * dx + dz * dz >= (double)minDistance * minDistance) continue;
                reason = "too close to a player";
                return null;
            }
            if (zone.light is { Length: 2 } light)
            {
                int level = ba.GetLightLevel(probe.Set(x, found, z), EnumLightLevelType.MaxTimeOfDayLight);
                if (level < light[0] || level > light[1])
                {
                    reason = $"light {level}";
                    return null;
                }
            }
            reason = "";
            return new BlockPos(x, found, z, dim);
        }

        private void Prune(ICoreServerAPI sapi, ZoneModel? zone, State state, IReadOnlyList<(double X, double Y, double Z, int Dimension)> spots, long now)
        {
            while (state.SpawnedRecent.Count > 0 && now - state.SpawnedRecent.Peek() > WindowMs) state.SpawnedRecent.Dequeue();
            while (state.KilledRecent.Count > 0 && now - state.KilledRecent.Peek() > WindowMs) state.KilledRecent.Dequeue();
            state.Attempts.RemoveAll(a => now - a.AtMs > AttemptsKeptMs);
            if (state.Alive.Count == 0) return;

            double near = zone == null ? 0 : SpawnDistance(zone)[1] + 16;
            long despawnMs = zone == null ? 0 : (long)(zone.despawnAfterSeconds * 1000);
            aliveCopy.Clear();
            aliveCopy.AddRange(state.Alive);
            foreach (long id in aliveCopy)
            {
                // unloaded ones come back through Track when their chunk loads
                if (!sapi.World.LoadedEntities.TryGetValue(id, out var entity) || !entity.Alive)
                {
                    Forget(state, id);
                    continue;
                }
                if (despawnMs <= 0) continue;
                var pos = entity.Pos;
                bool seen = false;
                foreach (var s in spots)
                {
                    if (s.Dimension != pos.Dimension || (s.X - pos.X) * (s.X - pos.X) + (s.Z - pos.Z) * (s.Z - pos.Z) > near * near) continue;
                    seen = true;
                    break;
                }
                if (seen || !state.LastNearMs.TryGetValue(id, out long last))
                {
                    state.LastNearMs[id] = now;
                    continue;
                }
                if (now - last < despawnMs) continue;
                entity.Die(EnumDespawnReason.Removed);
                Forget(state, id);
                state.Despawned++;
            }
        }

        internal static bool IsDay(ICoreServerAPI sapi, double x, double z) => sapi.World.Calendar.GetDayLightStrength(x, z) > 0.45f;

        internal static ZoneSpawnModel? Pick(ZoneModel zone, bool day, Random rand)
        {
            if (zone.spawns == null) return null;
            float total = 0;
            foreach (var s in zone.spawns!)
                if (Usable(s, day)) total += s.weight;
            if (total <= 0) return null;
            double roll = rand.NextDouble() * total;
            foreach (var s in zone.spawns)
            {
                if (!Usable(s, day)) continue;
                roll -= s.weight;
                if (roll <= 0) return s;
            }
            return zone.spawns.LastOrDefault(s => Usable(s, day));
        }

        private static bool Usable(ZoneSpawnModel? s, bool day)
            => s != null && s.weight > 0 && (string.IsNullOrWhiteSpace(s.mob) != string.IsNullOrWhiteSpace(s.group))
               && (s.time == ZoneRules.AnyTime || (s.time == "day") == day);

        internal static string? PickTier(ZoneModel zone, Random rand)
        {
            if (zone.tiers is not { Count: > 0 } tiers) return null;
            float total = tiers.Values.Where(w => w > 0).Sum();
            if (total <= 0) return null;
            double roll = rand.NextDouble() * total;
            foreach (var (tier, weight) in tiers)
            {
                if (weight <= 0) continue;
                roll -= weight;
                if (roll <= 0) return tier;
            }
            return null;
        }

        internal static int? Roll(int[]? range, Random rand)
            => range is { Length: 2 } r ? r[0] + rand.Next(Math.Max(0, r[1] - r[0]) + 1) : null;

        private static void Fail(State state, double x, double y, double z, string reason, long now)
        {
            state.Failures[reason] = state.Failures.GetValueOrDefault(reason) + 1;
            Remember(state, new Attempt(x, y, z, false, reason, now));
        }

        private static void Remember(State state, Attempt attempt)
        {
            state.Attempts.Add(attempt);
            if (state.Attempts.Count > KeptAttempts) state.Attempts.RemoveAt(0);
        }

        public string Describe(ZoneModel zone, long now)
        {
            if (zone.spawns is not { Count: > 0 }) return "No spawns set, the zone spawns nothing by itself: add mobs on its Mobs tab in the editor.";
            if (Get(zone.id) is not { } state) return "Nobody has been inside since the server started.";
            var sb = new StringBuilder();
            sb.Append($"alive {state.Alive.Count}/{(state.PlayersInside > 0 ? state.Cap : Cap(zone, 1))}, players inside {state.PlayersInside}");
            sb.Append($"\nlast 10 min: spawned {state.SpawnedRecent.Count}, killed {state.KilledRecent.Count}; since start: {state.Spawned} / {state.Killed}, gone {state.Despawned}");
            if (state.PlayersInside > 0 && state.NextSpawnMs > now) sb.Append($"\nnext spawn in {(state.NextSpawnMs - now) / 1000}s");
            else if (state.PlayersInside > 0 && state.Alive.Count < state.Cap) sb.Append(state.Filling ? "\nfilling up" : "\nspawning");
            if (state.Failures.Count > 0)
                sb.Append("\nrefused: " + string.Join(", ", state.Failures.OrderByDescending(f => f.Value).Take(4).Select(f => $"{f.Key} {f.Value}")));
            if (state.LastError != null) sb.Append($"\nlast error: {state.LastError}");
            return sb.ToString();
        }

        // what a zone would spawn, and how often a random spot near the player works
        public string Simulate(ICoreServerAPI sapi, canrpgmobsModSystem mod, ZoneShape shape, (double X, double Y, double Z, int Dimension) at, int rolls)
        {
            var zone = shape.Model;
            var rand = sapi.World.Rand;
            var sb = new StringBuilder();
            foreach (bool day in new[] { true, false })
            {
                var counts = new Dictionary<string, int>();
                for (int i = 0; i < rolls; i++)
                {
                    var entry = Pick(zone, day, rand);
                    string key = entry == null ? "(nothing)" : entry.mob ?? "group " + entry.group;
                    string? tier = entry == null ? null : EntryTier(zone, entry, rand);
                    if (tier != null && tier != StatMath.DefaultTier) key += " " + ZoneText.ShortId(tier);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                sb.Append(day ? "By day: " : "\nAt night: ")
                    .Append(string.Join(", ", counts.OrderByDescending(c => c.Value).Take(8).Select(c => $"{c.Key} {c.Value * 100f / rolls:0}%")));
            }

            var range = SpawnDistance(zone);
            var players = new List<(double, double, double, int)> { at };
            var reasons = new Dictionary<string, int>();
            int ok = 0;
            const int probes = 60;
            for (int i = 0; i < probes; i++)
            {
                var (x, z) = RingPoint(at.X, at.Z, range, rand);
                if (FindSpot(sapi, mod, shape, x, z, players, range[0], out _, out string reason) != null) ok++;
                else reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            }
            sb.Append($"\nSpots around you: {ok} of {probes} usable");
            if (reasons.Count > 0) sb.Append(" (refused: " + string.Join(", ", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}")) + ")");
            return sb.ToString();
        }
    }
}
