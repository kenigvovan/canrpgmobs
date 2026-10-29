using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Spawning;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs
{
    // content sync and in-game edits, server owns the store and clients mirror it
    public partial class canrpgmobsModSystem
    {
        public const string ChannelName = "canrpgmobs";
        public const string ImportFolder = "canrpgmobs-import";
        public const string ExportFolder = "canrpgmobs-export";

        // Section "lang": Id = language code, Json = {key: text}, empty text removes the key
        public const string LangSection = "lang";
        private const long PreviewCooldownMs = 1000;

        private IServerNetworkChannel? serverChannel;
        private IClientNetworkChannel? clientChannel;
        private readonly Dictionary<string, long> lastZoneStatsMs = new();
        private readonly Dictionary<string, long> lastPreviewMs = new();
        // serialized once per change, not per join
        private string? storeJson;
        private string StoreJson => storeJson ??= Store.Serialize();

        // client-side events for the editor
        public event Action? CatalogChanged;
        public event Action<CatalogEditResultPacket>? EditResultReceived;
        public event Action<MobTestResultPacket>? TestResultReceived;
        public event Action<ZoneStatsPacket>? ZoneStatsReceived;

        // same order on both sides, ids follow it
        private static void RegisterPackets(INetworkChannel channel)
        {
            channel.RegisterMessageType<CatalogSyncPacket>();
            channel.RegisterMessageType<CatalogEditPacket>();
            channel.RegisterMessageType<CatalogEditResultPacket>();
            channel.RegisterMessageType<MobPreviewPacket>();
            channel.RegisterMessageType<CatalogBundlePacket>();
            channel.RegisterMessageType<MobTestPacket>();
            channel.RegisterMessageType<MobTestResultPacket>();
            channel.RegisterMessageType<MobLookPacket>();
            channel.RegisterMessageType<ZoneStatsRequestPacket>();
            channel.RegisterMessageType<ZoneStatsPacket>();
        }

        private void StartServerNetwork(ICoreServerAPI api)
        {
            var channel = api.Network.RegisterChannel(ChannelName);
            RegisterPackets(channel);
            serverChannel = channel
                .SetMessageHandler<ZoneStatsRequestPacket>(OnZoneStatsRequest)
                .SetMessageHandler<CatalogEditPacket>(OnCatalogEdit)
                .SetMessageHandler<MobPreviewPacket>(OnMobPreview)
                .SetMessageHandler<CatalogBundlePacket>(OnCatalogBundle)
                .SetMessageHandler<MobTestPacket>(OnMobTest)
                .SetMessageHandler<MobLookPacket>(OnMobLook);
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        }

        private void StartClientNetwork(ICoreClientAPI api)
        {
            var channel = api.Network.RegisterChannel(ChannelName);
            RegisterPackets(channel);
            clientChannel = channel
                .SetMessageHandler<ZoneStatsPacket>(p => ZoneStatsReceived?.Invoke(p))
                .SetMessageHandler<CatalogSyncPacket>(OnCatalogSync)
                .SetMessageHandler<CatalogEditResultPacket>(OnCatalogEditResult)
                .SetMessageHandler<MobTestResultPacket>(OnMobTestResult);
        }

        public void RequestZoneStats(string zoneId) => clientChannel?.SendPacket(new ZoneStatsRequestPacket { ZoneId = zoneId });

        public void SendBundle(string json) => clientChannel?.SendPacket(new CatalogBundlePacket { Json = json });

        public void SendTest(MobTestPacket packet) => clientChannel?.SendPacket(packet);

        public void SendLook(string appearanceJson, string presetJson)
            => clientChannel?.SendPacket(new MobLookPacket { AppearanceJson = appearanceJson, PresetJson = presetJson });

        public void SendEdit(string section, string id, string? oldId, string json, string tag = "")
            => clientChannel?.SendPacket(new CatalogEditPacket { Section = section, Id = id, OldId = oldId ?? "", Json = json, Tag = tag });

        public void SendDelete(string section, string id, string tag = "")
            => clientChannel?.SendPacket(new CatalogEditPacket { Section = section, Id = id, Delete = true, Tag = tag });

        public void RequestPreview(string mobKey, int level, string? tier)
            => clientChannel?.SendPacket(new MobPreviewPacket { MobKey = mobKey, Level = level, Tier = tier ?? "" });

        private void OnZoneStatsRequest(IServerPlayer fromPlayer, ZoneStatsRequestPacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            if (Throttle(lastZoneStatsMs, fromPlayer.PlayerUID, 400)) return;
            string id = IdRules.Qualify(packet.ZoneId ?? "", ModId);
            var reply = new ZoneStatsPacket { ZoneId = id };
            if (Catalog.Zones.TryGetValue(id, out var zone)) reply.Text = ZoneSpawns.Describe(zone, sapi.World.ElapsedMilliseconds);
            else reply.Text = "Not saved yet, nothing spawns from it.";
            if (ZoneSpawns.Get(id) is { } state)
            {
                var attempts = state.Attempts;
                reply.Points = attempts.SelectMany(a => new[] { a.X, a.Y, a.Z }).ToArray();
                reply.Ok = attempts.Select(a => a.Ok).ToArray();
                reply.Reasons = attempts.Select(a => a.Reason).ToArray();
            }
            serverChannel?.SendPacket(reply, fromPlayer);
        }

        private static string StoreUnreadable => $"{CatalogStore.SourceName} could not be read at startup, fix or remove it first";
        private static string NotWritten => $" But {CatalogStore.SourceName} could not be written (see the server log): the change is lost on restart.";

        private CatalogSyncPacket SyncPacket() => new() { StoreJson = StoreJson, ZonesForPlayers = Settings.zonesOnMapForPlayers };

        private void OnPlayerNowPlaying(IServerPlayer player) => serverChannel?.SendPacket(SyncPacket(), player);

        private void OnCatalogEdit(IServerPlayer fromPlayer, CatalogEditPacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            try
            {
                var problems = new List<string>();
                string? refused = ApplyEdit(packet, problems, out string message);
                if (refused != null)
                    sapi.Logger.Notification("[canrpgmobs] refused a {0} edit of '{1}' from {2}: {3}", packet.Section, packet.Id, fromPlayer.PlayerName, refused);
                Reply(fromPlayer, refused == null, refused ?? message, problems, packet.Tag);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[canrpgmobs] an edit from {0} failed: {1}", fromPlayer.PlayerName, e);
                Reply(fromPlayer, false, "the server hit an error applying the edit, see server-main.log", null, packet.Tag);
            }
        }

        // null = saved. Validates the whole candidate catalog, broken entries never reach the file
        private string? ApplyEdit(CatalogEditPacket packet, List<string> problems, out string message)
        {
            message = "";
            if (packet.Section == LangSection) return ApplyLangEdit(packet, out message);
            if (CatalogEditGuard.RejectSection(packet.Section) is { } badSection) return badSection;
            if (Store.ReadOnly) return StoreUnreadable;
            if (!IdRules.IsClean(packet.Id)) return "invalid id";
            string id = IdRules.Qualify(packet.Id, ModId);
            var candidate = Store.Clone();

            if (packet.Delete)
            {
                if (!candidate.Remove(packet.Section, id))
                    return CatalogStore.IsInCatalog(Catalog.Data, packet.Section, id)
                        ? $"'{id}' comes from a mod file and can't be deleted in game, only changed"
                        : $"there is no '{id}' in {packet.Section}";
                candidate.RemoveAliasesTo(id);
            }
            else
            {
                if (packet.Json == null || packet.Json.Length > CatalogEditGuard.MaxJsonChars) return "the edit is too large";
                JToken token;
                try { token = JToken.Parse(packet.Json); }
                catch (Exception e) { return "the edit isn't valid JSON - " + e.Message; }
                if (CatalogEditGuard.RejectEntry(token) is { } badEntry) return badEntry;

                var entry = (JObject)token;
                entry["id"] = id;
                if (!string.IsNullOrWhiteSpace(packet.OldId) && IdRules.IsClean(packet.OldId))
                {
                    string oldId = IdRules.Qualify(packet.OldId, ModId);
                    // rename: old id becomes an alias for saved mobs. Not if it lives in a mod file, the alias would shadow it
                    if (oldId != id && candidate.Remove(packet.Section, oldId)) candidate.SetAlias(oldId, id);
                }
                candidate.Replace(packet.Section, entry);
            }

            var catalog = CatalogLoader.Load(sapi!, Mod.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            // file-level errors (wrong value type, migration) drop the whole store and don't name the id
            problems.AddRange(report.Problems.Where(p => p.Severity != Severity.Info && (Problem.PathMentions(p.Path, id) || p.Severity == Severity.Error && p.Path == CatalogStore.SourceName))
                .Take(20).Select(p => p.ToString()));
            if (!packet.Delete && !CatalogStore.IsInCatalog(catalog.Data, packet.Section, id))
            {
                var error = report.Problems.FirstOrDefault(p => p.Severity == Severity.Error && Problem.PathMentions(p.Path, id));
                if (error.Message == null) error = report.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.Path == CatalogStore.SourceName);
                return error.Message != null ? $"'{id}' was not saved: {error.Message}" : $"'{id}' has errors and was not saved";
            }

            bool written = Commit(candidate, catalog, report);
            if (packet.Delete)
                message = CatalogStore.IsInCatalog(catalog.Data, packet.Section, id) ? "Deleted the in-game version; the mod file's version is back." : "Deleted.";
            else
                message = problems.Count == 0 ? "Saved." : $"Saved with {problems.Count} warning(s).";
            if (!written) message += NotWritten;
            return null;
        }

        private string? ApplyLangEdit(CatalogEditPacket packet, out string message)
        {
            message = "";
            if (Store.ReadOnly) return StoreUnreadable;
            string locale = (packet.Id ?? "").Trim().ToLowerInvariant();
            if (!LangTables.IsLocale(locale) || locale == "en") return "pick a language other than English, English is the name itself";
            if (packet.Json == null || packet.Json.Length > CatalogEditGuard.MaxJsonChars) return "the edit is too large";
            JObject changes;
            try { changes = JObject.Parse(packet.Json); }
            catch (Exception e) { return "the edit isn't valid JSON - " + e.Message; }
            if (CatalogEditGuard.RejectValue(changes) is { } bad) return bad;

            var candidate = Store.Clone();
            int count = 0;
            foreach (var entry in changes.Properties())
            {
                string key = entry.Name.Trim();
                if (entry.Value.Type != JTokenType.String || key.Length == 0 || key.Length > 200 || !key.Contains(':')) continue;
                candidate.SetTranslation(locale, key, ((string)entry.Value!).Trim());
                count++;
            }
            if (count == 0) return "nothing to save";

            var catalog = CatalogLoader.Load(sapi!, Mod.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            bool written = Commit(candidate, catalog, report);
            message = $"Saved {count} translation(s) for {locale}.";
            if (!written) message += NotWritten;
            return null;
        }

        private void OnMobPreview(IServerPlayer fromPlayer, MobPreviewPacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            if (fromPlayer.Entity is not EntityPlayer player) return;

            // 1/s per player, against spam and forged packets
            if (Throttle(lastPreviewMs, fromPlayer.PlayerUID, PreviewCooldownMs)) return;

            try
            {
                float yaw = player.Pos.Yaw;
                var request = new MobSpawnRequest(packet.MobKey ?? "", player.Pos.XYZ.AheadCopy(3.0, 0f, yaw), packet.Level > 0 ? packet.Level : null,
                    yaw + GameMath.PI, Tier: string.IsNullOrWhiteSpace(packet.Tier) ? null : packet.Tier, Preview: true);
                var mob = MobSpawner.TrySpawn(sapi, request, out string? error);
                // live look targets this one
                if (mob != null) lastPreviewEntity[fromPlayer.PlayerUID] = mob.EntityId;
                Reply(fromPlayer, mob != null,
                    mob != null ? $"Preview spawned, it disappears in {Settings.maxPreviewSeconds} s." : $"No preview: {error}", null);
            }
            catch (Exception e)
            {
                Mod.Logger.Error("[canrpgmobs] preview of {0} for {1} failed: {2}", packet.MobKey, fromPlayer.PlayerName, e);
                Reply(fromPlayer, false, "The server hit an error spawning the preview, see its log.", null);
            }
        }

        // ModConfig/canrpgmobs-import/<file> -> store
        internal string ImportContent(string fileName, out bool ok)
        {
            ok = false;
            if (sapi == null) return "Server only.";
            if (Store.ReadOnly) return StoreUnreadable + ".";
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..")) return "Give a plain file name, no folders.";
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";
            string folder = Path.Combine(GamePaths.ModConfig, ImportFolder);
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return $"No file {fileName} in {folder}.";

            JObject file;
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) return "The file is larger than 4 MB.";
                file = JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                return "The file isn't valid JSON: " + e.Message;
            }
            return MergeContent(file, fileName, out ok);
        }

        // refused entries are skipped and keep their old in-game version; allOrNothing cancels on any refusal
        internal string MergeContent(JObject file, string label, out bool ok, bool allOrNothing = false)
        {
            ok = false;
            if (sapi == null) return "Server only.";
            var refused = new List<string>();
            foreach (var section in CatalogStore.Sections)
            {
                if (file[section] is not JArray entries) continue;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (CatalogEditGuard.RejectEntry(entries[i]) is not { } why) continue;
                    refused.Add($"{section}[{i}]: {why}");
                    entries.RemoveAt(i);
                }
            }

            var candidate = Store.Clone();
            var taken = candidate.Merge(file);
            if (taken.Count == 0 && refused.Count == 0 && file["aliases"] == null && file["scaling"] == null && file["lang"] == null)
                return "The file has no mob content.";

            var catalog = CatalogLoader.Load(sapi, Mod.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            var invalid = taken.Where(e => !CatalogStore.IsInCatalog(catalog.Data, e.section, IdRules.Qualify(e.id, ModId))).ToList();
            foreach (var (section, id) in invalid)
            {
                string qualified = IdRules.Qualify(id, ModId);
                string reason = report.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.Path.Contains(qualified)).Message ?? "invalid";
                refused.Add($"{section} {qualified}: {reason}");
                // restore the pre-import version
                if (Store.Get(section, id) is { } previous) candidate.Replace(section, (JObject)previous.DeepClone());
                else candidate.Remove(section, id);
            }
            if (allOrNothing && refused.Count > 0) return "Nothing was saved:\n" + string.Join("\n", refused.Take(8));
            if (invalid.Count > 0) catalog = CatalogLoader.Load(sapi, Mod.Logger, MobsApi.Vocabulary, candidate, out report, logProblems: false);

            int imported = taken.Count - invalid.Count;
            var text = new StringBuilder();
            if (imported > 0 || file["aliases"] != null || file["scaling"] != null || file["lang"] != null)
            {
                if (!Commit(candidate, catalog, report))
                    text.Append($"{CatalogStore.SourceName} could not be written (see the server log), the import is lost on restart. ");
                ok = true;
                text.Append($"Imported {imported} entr{(imported == 1 ? "y" : "ies")} from {label}; {report.Summary}. Live mobs update over the next seconds.");
            }
            else text.Append($"Nothing imported from {label}.");
            if (refused.Count > 0) text.Append("\nLeft out:\n").Append(string.Join("\n", refused.Take(15)));
            if (refused.Count > 15) text.Append($"\n... and {refused.Count - 15} more");
            return text.ToString();
        }

        // false = write failed, change is live until restart
        private bool Commit(CatalogStore candidate, MobCatalog catalog, ValidationReport report)
        {
            Store = candidate;
            storeJson = null;
            bool written = Store.Save(Mod.Logger);
            CatalogLoader.LogProblems(Mod.Logger, report);
            SwapCatalog(catalog, report);
            BroadcastCatalog();
            return written;
        }

        internal void BroadcastCatalog()
        {
            if (sapi == null || serverChannel == null) return;
            var players = sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().ToArray();
            if (players.Length > 0) serverChannel.SendPacket(SyncPacket(), players);
        }

        private void Reply(IServerPlayer player, bool ok, string message, List<string>? problems, string? tag = null)
            => serverChannel?.SendPacket(new CatalogEditResultPacket { Ok = ok, Message = message ?? "", Problems = problems?.ToArray() ?? Array.Empty<string>(), Tag = tag ?? "" }, player);

        private void OnCatalogSync(CatalogSyncPacket packet)
        {
            if (packet == null || Api is not ICoreClientAPI) return;
            ZonesOnMapForPlayers = packet.ZonesForPlayers;
            Store = CatalogStore.Adopt(packet.StoreJson, Mod.Logger);
            ReloadCatalog(Api, assetChecks: false);
            CatalogChanged?.Invoke();
        }

        private void OnCatalogEditResult(CatalogEditResultPacket packet)
        {
            if (packet == null || Api is not ICoreClientAPI capi) return;
            EditResultReceived?.Invoke(packet);

            var problems = packet.Problems ?? Array.Empty<string>();
            var text = new StringBuilder(packet.Ok ? "canrpgmobs: " : "canrpgmobs, not saved: ").Append(packet.Message);
            foreach (var problem in problems.Take(5)) text.Append('\n').Append(problem);
            if (problems.Length > 5) text.Append($"\n... {problems.Length - 5} more");
            capi.ShowChatMessage(text.ToString());
        }
    }
}
