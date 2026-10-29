using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs
{
    // /canrpgmobs zone ..., and zone spawning
    public partial class canrpgmobsModSystem
    {
        private const long AnnounceAgainMs = 10 * 60 * 1000;

        public ZoneSpawner ZoneSpawns { get; } = new();
        private long zoneTickId;
        private Action<MobLoadedEvent>? onZoneMobLoaded;
        private readonly Dictionary<string, string?> playerZone = new();
        private readonly Dictionary<string, long> zoneAnnouncedMs = new();

        private void StartZoneSpawning(ICoreServerAPI api)
        {
            zoneTickId = api.Event.RegisterGameTickListener(_ =>
            {
                ZoneSpawns.Tick(api, this);
                TrackZoneCrossings(api);
            }, ZoneSpawner.TickMs);
            onZoneMobLoaded = e =>
            {
                if (e.Entity.Api?.Side == EnumAppSide.Server) ZoneSpawns.Track(e.Entity, api.World.ElapsedMilliseconds);
            };
            MobsApi.MobLoaded += onZoneMobLoaded;
            api.Event.OnEntityDeath += OnZoneEntityDeath;
            api.Event.OnTrySpawnEntity += OnTryVanillaSpawn;
        }

        private void StopZoneSpawning(ICoreServerAPI api)
        {
            if (zoneTickId != 0) api.Event.UnregisterGameTickListener(zoneTickId);
            zoneTickId = 0;
            if (onZoneMobLoaded != null) MobsApi.MobLoaded -= onZoneMobLoaded;
            onZoneMobLoaded = null;
            api.Event.OnEntityDeath -= OnZoneEntityDeath;
            api.Event.OnTrySpawnEntity -= OnTryVanillaSpawn;
        }

        // ZoneCrossed for add-ons, and the "you enter ..." line
        private void TrackZoneCrossings(ICoreServerAPI api)
        {
            long now = api.World.ElapsedMilliseconds;
            var index = Catalog.ZoneIndex;
            foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.Entity is not { } entity) continue;
                var pos = entity.Pos;
                var zone = index.At(pos.Dimension, pos.X, pos.Y, pos.Z)?.Model;
                playerZone.TryGetValue(player.PlayerUID, out var before);
                if (before == zone?.id) continue;
                playerZone[player.PlayerUID] = zone?.id;
                var from = before != null && Catalog.Zones.TryGetValue(before, out var old) ? old : null;
                MobsApi.RaiseZoneCrossed(new ZoneCrossedEvent(player, from, zone), Mod.Logger);

                if (zone == null || !zone.announce) continue;
                string key = player.PlayerUID + "\n" + zone.id;
                if (zoneAnnouncedMs.TryGetValue(key, out long at) && now - at < AnnounceAgainMs) continue;
                zoneAnnouncedMs[key] = now;
                string lang = player.LanguageCode;
                string name = ZoneText.Name(zone, lang), levels = ZoneText.Levels(zone);
                string text = levels.Length > 0 ? Lang.GetL(lang, "canrpgmobs:zone-entered-level", name, levels) : Lang.GetL(lang, "canrpgmobs:zone-entered", name);
                player.SendMessage(GlobalConstants.GeneralChatGroup, text, EnumChatType.Notification);
            }
        }

        // ModConfig/canrpgmobs-export/<id>.json, ready to drop into a mod's config/mobs
        private TextCommandResult OnZoneExport(TextCommandCallingArgs args)
        {
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            string id = shape.Model.id;
            var entry = Store.Get("zones", id)?.DeepClone() as JObject
                        ?? JObject.FromObject(shape.Model, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            var file = new JObject { ["formatVersion"] = FormatMigrator.CurrentVersion, ["zones"] = new JArray(entry) };
            string folder = Path.Combine(GamePaths.ModConfig, ExportFolder);
            string path = Path.Combine(folder, ZoneText.ShortId(id).Replace(':', '_') + ".json");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, file.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                return TextCommandResult.Error("Could not write the file: " + e.Message);
            }
            return TextCommandResult.Success($"Written to {path}.");
        }

        private void OnZoneEntityDeath(Entity entity, DamageSource damageSource)
            => ZoneSpawns.OnDied(entity, entity.World.ElapsedMilliseconds);

        private bool OnTryVanillaSpawn(IBlockAccessor blockAccessor, ref EntityProperties properties, Vec3d spawnPosition, long herdId)
            => ZoneSpawner.AllowVanilla(Catalog, properties, spawnPosition);

        private TextCommandResult OnZoneHere(TextCommandCallingArgs args)
        {
            if (args.Caller.Player?.Entity is not { } entity) return NoPlayer();
            var pos = entity.Pos;
            var found = Catalog.ZoneIndex.AllAt(pos.Dimension, pos.X, pos.Y, pos.Z);
            int bx = (int)Math.Floor(pos.X), bz = (int)Math.Floor(pos.Z);
            var (lx, lz) = ZoneText.ToLocal(sapi!, bx, bz);
            // zone files take absolute coords
            string at = $"{lx}, {lz} (in files [{bx}, {bz}])";
            if (found.Count == 0)
            {
                var nearest = NearestZone(pos.Dimension, pos.X, pos.Z, out double distance);
                return TextCommandResult.Success($"No zone at {at}." + (nearest != null ? $" Nearest: {Title(nearest.Model)}, about {distance:0} blocks away." : ""));
            }

            var winner = found.FirstOrDefault(s => s.Model.enabled);
            var sb = new StringBuilder();
            if (winner != null) sb.AppendLine($"At {at}: {Title(winner.Model)}").Append(SpawnSummary(winner.Model));
            else sb.Append($"At {at}: only switched off zones.");
            var others = found.Where(s => s != winner).ToList();
            if (others.Count > 0)
                sb.Append("\nAlso here, losing: " + string.Join(", ", others.Select(s => $"{ZoneText.ShortId(s.Model.id)} (priority {s.Model.priority}{(s.Model.enabled ? "" : ", off")})")));
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private TextCommandResult OnZoneList(TextCommandCallingArgs args)
        {
            var all = Catalog.ZoneIndex.All;
            if (all.Count == 0) return TextCommandResult.Success("No zones. Add a zones section to a config/mobs file, see docs/ZONES.md.");
            var sb = new StringBuilder($"{all.Count} zone(s):");
            foreach (var shape in all.OrderBy(s => s.Model.id, StringComparer.Ordinal))
            {
                var z = shape.Model;
                sb.Append($"\n{(z.enabled ? "" : "[off] ")}{Title(z)}, {shape.Corners.Length} corners, area {ZoneGeometry.Area(shape.Corners):0}");
                if (z.priority != 0) sb.Append($", priority {z.priority}");
                if (z.dimension != 0) sb.Append($", dimension {z.dimension}");
            }
            int dropped = LastReport.Problems.Count(p => p.Severity == Severity.Error && p.Path.Contains("> zone "));
            if (dropped > 0) sb.Append($"\n{dropped} zone error(s), see zone check.");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnZoneInfo(TextCommandCallingArgs args)
        {
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            var z = shape.Model;
            var center = ZoneGeometry.LabelPoint(shape.Corners);
            var (cx, cz) = ZoneText.ToLocal(sapi!, (int)Math.Floor(center.X), (int)Math.Floor(center.Z));
            var sb = new StringBuilder();
            sb.AppendLine($"{Title(z)}{(z.enabled ? "" : " - switched off")}");
            sb.AppendLine($"from {z.source}");
            sb.AppendLine($"center {cx}, {cz}, {shape.Corners.Length} corners, area {ZoneGeometry.Area(shape.Corners):0}, priority {z.priority}"
                          + (z.y is { Length: 2 } y ? $", height {y[0]}-{y[1]}" : "") + (z.dimension != 0 ? $", dimension {z.dimension}" : ""));
            sb.AppendLine($"up to {z.maxAlive} alive, {z.perPlayer} per player, respawn {z.respawnSeconds:0}s, vanilla {z.vanilla}, leash {z.leash}");
            sb.Append(SpawnSummary(z));
            sb.AppendLine(ZoneSpawns.Describe(z, sapi!.World.ElapsedMilliseconds));
            AppendZoneProblems(sb, z.id, 10);
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private TextCommandResult OnZoneCheck(TextCommandCallingArgs args)
        {
            var problems = LastReport.Problems.Where(p => p.Severity != Severity.Info && p.Path.Contains("> zone ")).ToList();
            if (problems.Count == 0) return TextCommandResult.Success($"{Catalog.Zones.Count} zone(s), no problems.");
            var sb = new StringBuilder($"{Catalog.Zones.Count} zone(s) loaded, {problems.Count} problem(s):");
            foreach (var p in problems.OrderByDescending(p => p.Severity).Take(20)) sb.Append('\n').Append(p.ToString());
            if (problems.Count > 20) sb.Append("\nThe full list is in server-main.log.");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnZoneTp(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player is not IServerPlayer player || player.Entity == null) return NoPlayer();
            string arg = (string)args[0];
            int corner = (int)args[1];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            if (shape.Model.dimension != player.Entity.Pos.Dimension)
                return TextCommandResult.Error($"The zone is in dimension {shape.Model.dimension}, you are in {player.Entity.Pos.Dimension}.");
            if (corner < 0 || corner > shape.Corners.Length) return TextCommandResult.Error($"The zone has corners 1 to {shape.Corners.Length}, 0 = center.");

            var (x, z) = corner == 0 ? ZoneGeometry.LabelPoint(shape.Corners) : (shape.Corners[corner - 1].X + 0.5, shape.Corners[corner - 1].Z + 0.5);
            TeleportToGround(player, x, z, shape.Model.y);
            return TextCommandResult.Success(corner == 0 ? $"Going to the middle of {ZoneText.ShortId(shape.Model.id)}." : $"Going to corner {corner} of {ZoneText.ShortId(shape.Model.id)}.");
        }

        private TextCommandResult OnZoneStats(TextCommandCallingArgs args)
        {
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            return TextCommandResult.Success($"{Title(shape.Model)}\n{ZoneSpawns.Describe(shape.Model, sapi!.World.ElapsedMilliseconds)}");
        }

        private TextCommandResult OnZoneFill(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not { } entity) return NoPlayer();
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            if (shape.Model.spawns is not { Count: > 0 }) return TextCommandResult.Error("The zone has no spawns of its own.");
            var pos = entity.Pos;
            int made = ZoneSpawns.Fill(sapi, this, shape, (pos.X, pos.Y, pos.Z, pos.Dimension), out var state);
            string why = made == 0 && state.Attempts.LastOrDefault(a => !a.Ok) is { } last ? $" Last refusal: {last.Reason}." : "";
            return TextCommandResult.Success($"Spawned {made} mob(s), {state.Alive.Count} of the zone's are alive.{why}");
        }

        private TextCommandResult OnZoneClear(TextCommandCallingArgs args)
        {
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            int removed = ZoneSpawns.Clear(sapi!, shape.Model.id);
            return TextCommandResult.Success($"Removed {removed} mob(s) of {ZoneText.ShortId(shape.Model.id)}; it fills up again while players are inside.");
        }

        private TextCommandResult OnZoneSimulate(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not { } entity) return NoPlayer();
            string arg = (string)args[0];
            if (FindZone(arg) is not { } shape) return ZoneNotFound(arg);
            int rolls = Math.Clamp((int)args[1], 10, 100000);
            var pos = entity.Pos;
            return TextCommandResult.Success($"{Title(shape.Model)}, {rolls} picks:\n{ZoneSpawns.Simulate(sapi, this, shape, (pos.X, pos.Y, pos.Z, pos.Dimension), rolls)}");
        }

        // absolute coords, used by the zone panel
        private TextCommandResult OnZoneGoto(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player is not IServerPlayer player || player.Entity == null) return NoPlayer();
            TeleportToGround(player, (int)args[0] + 0.5, (int)args[1] + 0.5, null);
            return TextCommandResult.Success();
        }

        private TextCommandResult OnZoneWand(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player is not IServerPlayer player || player.Entity == null) return NoPlayer();
            if (sapi.World.GetItem(new AssetLocation(ModId, "zonewand")) is not { } item) return TextCommandResult.Error("The zone wand item is missing.");
            var stack = new ItemStack(item);
            if (!player.InventoryManager.TryGiveItemstack(stack, true))
                sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
            return TextCommandResult.Success("Zone wand given. F switches its mode.");
        }

        private void TeleportToGround(IServerPlayer player, double x, double z, int[]? yRange)
        {
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            // surface height needs the chunk
            sapi!.WorldManager.LoadChunkColumnPriority(bx / GlobalConstants.ChunkSize, bz / GlobalConstants.ChunkSize, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    int y = sapi.World.BlockAccessor.GetRainMapHeightAt(bx, bz) + 1;
                    if (yRange is { Length: 2 } range) y = Math.Clamp(y, range[0], range[1]);
                    player.Entity?.TeleportToDouble(x, y, z);
                }
            });
        }

        // full id, or a short one in any domain
        private ZoneShape? FindZone(string arg)
        {
            var index = Catalog.ZoneIndex;
            if (index.Get(IdRules.Qualify(arg, ModId)) is { } own) return own;
            return index.All.FirstOrDefault(s => s.Model.id.EndsWith(":" + arg.Trim(), StringComparison.Ordinal));
        }

        private TextCommandResult ZoneNotFound(string arg)
        {
            string known = JoinOrDash(Catalog.Zones.Keys.Select(ZoneText.ShortId));
            var dropped = LastReport.Problems.FirstOrDefault(p => p.Severity == Severity.Error && Problem.PathMentions(p.Path, IdRules.Qualify(arg, ModId)));
            return TextCommandResult.Error(dropped.Path != null
                ? $"Zone '{arg}' was not loaded: {dropped.Message}"
                : $"No zone '{arg}'. Known: {known}");
        }

        private ZoneShape? NearestZone(int dimension, double x, double z, out double distance)
        {
            ZoneShape? best = null;
            distance = double.MaxValue;
            foreach (var shape in Catalog.ZoneIndex.All)
            {
                if (shape.Model.dimension != dimension || !shape.Model.enabled) continue;
                double dx = Math.Max(0, Math.Max(shape.MinX - x, x - shape.MaxX - 1));
                double dz = Math.Max(0, Math.Max(shape.MinZ - z, z - shape.MaxZ - 1));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < distance)
                {
                    distance = d;
                    best = shape;
                }
            }
            return best;
        }

        private static string Title(ZoneModel zone)
        {
            string levels = ZoneText.Levels(zone);
            return $"{ZoneText.Name(zone, "en")} ({ZoneText.ShortId(zone.id)}" + (levels.Length > 0 ? $", level {levels})" : ")");
        }

        private string SpawnSummary(ZoneModel zone)
        {
            var spawns = zone.spawns?.Where(s => s != null && s.weight > 0 && (s.mob ?? s.group) != null).ToList() ?? new List<ZoneSpawnModel>();
            if (spawns.Count == 0) return "Spawns: nothing of its own.\n";
            float total = spawns.Sum(s => s.weight);
            var parts = spawns.OrderByDescending(s => s.weight).Take(8).Select(s =>
            {
                string what = s.mob != null ? ShortKey(s.mob) : "group " + ZoneText.ShortId(s.group!);
                string extra = s.time != ZoneRules.AnyTime ? $", {s.time}" : "";
                return $"{what} {s.weight / total * 100f:0}%{extra}";
            });
            return "Spawns: " + string.Join(", ", parts) + (spawns.Count > 8 ? $" and {spawns.Count - 8} more" : "") + "\n";
        }

        private static string ShortKey(string key)
        {
            int sep = key.IndexOf(IdRules.KeySeparator);
            return sep < 0 ? ZoneText.ShortId(key) : ZoneText.ShortId(key.Substring(0, sep)) + IdRules.KeySeparator + ZoneText.ShortId(key.Substring(sep + 1));
        }

        private void AppendZoneProblems(StringBuilder sb, string zoneId, int max)
        {
            var problems = LastReport.Problems.Where(p => p.Severity != Severity.Info && Problem.PathMentions(p.Path, zoneId)).ToList();
            foreach (var p in problems.Take(max)) sb.Append($"\n{p.Severity.ToString().ToLowerInvariant()}: {p.Message}");
            if (problems.Count > max) sb.Append($"\n... {problems.Count - max} more, see zone check");
        }
    }
}
