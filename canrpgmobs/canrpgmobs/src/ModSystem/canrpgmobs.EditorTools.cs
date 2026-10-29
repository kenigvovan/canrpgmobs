using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Spawning;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canrpgmobs
{
    // editor GUI tools: wizard bundle, test panel, live look
    public partial class canrpgmobsModSystem
    {
        private const long TestActionCooldownMs = 500;
        private const long TestRefreshCooldownMs = 250;
        private const long LookCooldownMs = 250;
        private const int MaxBundleChars = CatalogEditGuard.MaxJsonChars * 4;

        private readonly Dictionary<string, long> lastTestActionMs = new();
        private readonly Dictionary<string, long> lastTestRefreshMs = new();
        private readonly Dictionary<string, long> lastLookMs = new();
        private readonly Dictionary<string, long> watchedMob = new();
        private readonly Dictionary<string, long> lastPreviewEntity = new();

        private void OnCatalogBundle(IServerPlayer fromPlayer, CatalogBundlePacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            try
            {
                if (CheckBundle(packet.Json, out var file) is { } refused)
                {
                    Reply(fromPlayer, false, refused, null);
                    return;
                }
                string message = MergeContent(file!, "the wizard", out bool ok, allOrNothing: true);
                Reply(fromPlayer, ok, message, null);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[canrpgmobs] a wizard bundle from {0} failed: {1}", fromPlayer.PlayerName, e);
                Reply(fromPlayer, false, "the server hit an error creating the content, see server-main.log", null);
            }
        }

        // create only, the wizard must never overwrite existing entries
        private string? CheckBundle(string? json, out JObject? file)
        {
            file = null;
            if (Store.ReadOnly) return StoreUnreadable;
            if (string.IsNullOrEmpty(json) || json!.Length > MaxBundleChars) return "the bundle is empty or too large";
            try { file = JObject.Parse(json); }
            catch (Exception e) { return "the bundle isn't valid JSON - " + e.Message; }

            foreach (var property in file.Properties())
                if (!CatalogStore.Sections.Contains(property.Name)) return $"unknown section '{property.Name}'";

            int entries = 0;
            foreach (var section in CatalogStore.Sections)
            {
                if (file[section] is not JArray array) continue;
                foreach (var token in array)
                {
                    if (CatalogEditGuard.RejectEntry(token) is { } bad) return $"{section}: {bad}";
                    string id = IdRules.Qualify((string)token["id"]!, ModId);
                    if (CatalogStore.IsInCatalog(Catalog.Data, section, id) || Store.Contains(section, id)) return $"'{id}' already exists";
                    entries++;
                }
            }
            return entries == 0 ? "the bundle has no entries" : null;
        }

        private void OnMobTest(IServerPlayer fromPlayer, MobTestPacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            if (fromPlayer.Entity is not EntityPlayer player) return;

            string uid = fromPlayer.PlayerUID;
            string action = packet.Action ?? "";
            bool watching = action is "inspect" or "refresh" or "stop";
            if (Throttle(watching ? lastTestRefreshMs : lastTestActionMs, uid, watching ? TestRefreshCooldownMs : TestActionCooldownMs)) return;

            try
            {
                switch (action)
                {
                    case "spawn": TestSpawn(fromPlayer, player, packet); break;
                    case "group": TestGroup(fromPlayer, player, packet); break;
                    case "kill":
                        int radius = Math.Clamp(packet.Radius, 1, 128);
                        ReplyTest(fromPlayer, action, true, $"Removed {RemoveMobsAround(player, radius)} mob(s) within {radius} blocks.");
                        break;
                    case "inspect": TestInspect(fromPlayer, player); break;
                    case "refresh": TestRefresh(fromPlayer); break;
                    case "stop":
                        watchedMob.Remove(uid);
                        ReplyTest(fromPlayer, action, true, $"Stopped watching {BrainTrace.StopWatching(uid)} mob(s).");
                        break;
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[canrpgmobs] test action {0} from {1} failed: {2}", action, fromPlayer.PlayerName, e);
                ReplyTest(fromPlayer, action, false, "The server hit an error, see server-main.log.");
            }
        }

        private void TestSpawn(IServerPlayer fromPlayer, EntityPlayer player, MobTestPacket packet)
        {
            string key = (packet.MobKey ?? "").Trim();
            if (key.Length == 0)
            {
                ReplyTest(fromPlayer, "spawn", false, "Pick a mob first.");
                return;
            }
            int spawned = SpawnAhead(player, key, packet.Level > 0 ? packet.Level : null, NullIfBlank(packet.Tier), Math.Clamp(packet.Count, 1, 20), out var error);
            ReplyTest(fromPlayer, "spawn", spawned > 0,
                spawned > 0 ? $"Spawned {spawned} x {MobSpawner.NormalizeKey(key)}." : $"Could not spawn '{key}': {error}");
        }

        private void TestGroup(IServerPlayer fromPlayer, EntityPlayer player, MobTestPacket packet)
        {
            string group = (packet.MobKey ?? "").Trim();
            if (group.Length == 0)
            {
                ReplyTest(fromPlayer, "group", false, "Pick a group first.");
                return;
            }
            var spawned = SpawnGroupAhead(player, group, packet.Level > 0 ? packet.Level : null, out var error);
            ReplyTest(fromPlayer, "group", spawned.Count > 0,
                spawned.Count > 0 ? $"Spawned {spawned.Count} mob(s) of {IdRules.Qualify(group, ModId)}." : $"Could not spawn the group: {error}");
        }

        private void TestInspect(IServerPlayer fromPlayer, EntityPlayer player)
        {
            var mob = LookedAtMob(fromPlayer, player);
            if (mob == null)
            {
                ReplyTest(fromPlayer, "inspect", false, "No mob under the crosshair or within 8 blocks.");
                return;
            }
            string uid = fromPlayer.PlayerUID;
            BrainTrace.StopWatching(uid);
            BrainTrace.Watch(mob.EntityId, uid, toChat: false);
            watchedMob[uid] = mob.EntityId;
            ReplyTest(fromPlayer, "inspect", true, $"Watching {mob.GetName()}.", mob);
        }

        private void TestRefresh(IServerPlayer fromPlayer)
        {
            string uid = fromPlayer.PlayerUID;
            if (!watchedMob.TryGetValue(uid, out long id) || sapi!.World.GetEntityById(id) is not EntityRpgMob watched)
            {
                watchedMob.Remove(uid);
                BrainTrace.StopWatching(uid);
                ReplyTest(fromPlayer, "refresh", false, "The watched mob is gone.");
                return;
            }
            ReplyTest(fromPlayer, "refresh", true, watched.Alive ? "" : "The watched mob is dead.", watched);
        }

        private void OnMobLook(IServerPlayer fromPlayer, MobLookPacket packet)
        {
            if (fromPlayer == null || !fromPlayer.HasPrivilege(Privilege.controlserver) || packet == null || sapi == null) return;
            if (Throttle(lastLookMs, fromPlayer.PlayerUID, LookCooldownMs)) return;

            try
            {
                if (!lastPreviewEntity.TryGetValue(fromPlayer.PlayerUID, out long id) || sapi.World.GetEntityById(id) is not EntityRpgMob { Alive: true } preview)
                {
                    ReplyTest(fromPlayer, "look", false, "Spawn a preview first: the look goes onto your latest preview mob.");
                    return;
                }
                string appearanceJson = packet.AppearanceJson ?? "", presetJson = packet.PresetJson ?? "";
                if (appearanceJson.Length == 0 || appearanceJson.Length > CatalogEditGuard.MaxJsonChars || presetJson.Length > CatalogEditGuard.MaxJsonChars) return;

                var lookToken = JObject.Parse(appearanceJson);
                if (CatalogEditGuard.RejectValue(lookToken) is { } badLook)
                {
                    ReplyTest(fromPlayer, "look", false, badLook);
                    return;
                }
                var look = lookToken.ToObject<AppearanceModel>();
                AppearancePresetModel? preset = null;
                if (presetJson.Length > 0)
                {
                    var presetToken = JObject.Parse(presetJson);
                    if (CatalogEditGuard.RejectValue(presetToken) is { } badPreset)
                    {
                        ReplyTest(fromPlayer, "look", false, badPreset);
                        return;
                    }
                    preset = presetToken.ToObject<AppearancePresetModel>();
                }
                if (look == null) return;

                // PlayerModelLib and shape bodies are different entity types, can't switch in place
                string body = look.kind == AppearanceKinds.Shape ? MobBodies.Creature : MobBodies.Humanoid;
                if (preview.Code?.ToString() != MobBodies.EntityCode(body))
                {
                    ReplyTest(fromPlayer, "look", false, "The body kind changed: spawn a new preview to see it.");
                    return;
                }

                var identity = MobKeys.Identity(preview);
                identity?.SetString(MobKeys.Preset, preset?.id ?? "");
                MobSpawner.WriteAppearance(preview.Api, preview, look, preset, Catalog.Tier(identity?.GetString(MobKeys.Tier))?.size ?? 1f);
                // dirty paths make both body kinds redraw on the client
                preview.WatchedAttributes.MarkPathDirty(MobKeys.Tree);
                preview.WatchedAttributes.MarkPathDirty(MobKeys.PlmSkinConfig);
                preview.WatchedAttributes.MarkPathDirty(MobKeys.PlmModel);
                preview.WatchedAttributes.MarkPathDirty(MobKeys.PlmSize);
                ReplyTest(fromPlayer, "look", true, "The preview wears the draft's look.");
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[canrpgmobs] live look from {0} failed: {1}", fromPlayer.PlayerName, e.Message);
                ReplyTest(fromPlayer, "look", false, "That look could not be applied: " + e.Message);
            }
        }

        private void ReplyTest(IServerPlayer player, string action, bool ok, string message, EntityRpgMob? inspected = null)
        {
            var packet = new MobTestResultPacket { Action = action, Ok = ok, Message = message ?? "" };
            if (inspected != null)
            {
                packet.Inspect = DescribeMob(inspected);
                packet.Trace = BrainTrace.Recent(inspected.EntityId).ToArray();
            }
            serverChannel?.SendPacket(packet, player);
        }

        // true = too soon
        private bool Throttle(Dictionary<string, long> last, string uid, long cooldownMs)
        {
            long now = sapi!.World.ElapsedMilliseconds;
            if (last.TryGetValue(uid, out long at) && now >= at && now - at < cooldownMs) return true;
            last[uid] = now;
            return false;
        }

        private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();

        private void OnMobTestResult(MobTestResultPacket packet)
        {
            if (packet == null || Api is not ICoreClientAPI capi) return;
            if (TestResultReceived != null) TestResultReceived.Invoke(packet);
            else if (!string.IsNullOrEmpty(packet.Message)) capi.ShowChatMessage("canrpgmobs: " + packet.Message);
        }
    }
}
