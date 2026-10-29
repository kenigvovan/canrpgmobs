using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Brain
{
    // Server. Skill decisions for /canrpgmobs brain and the editor test panel.
    public static class BrainTrace
    {
        private const int RecentLines = 8;

        // mob id -> watcher uid
        private static readonly ConcurrentDictionary<long, (string uid, bool toChat)> watchers = new();
        private static readonly ConcurrentDictionary<long, Queue<string>> recent = new();

        public static void Watch(long mobId, string playerUid, bool toChat = true)
        {
            watchers[mobId] = (playerUid, toChat);
            recent.TryRemove(mobId, out _);
        }

        public static int StopWatching(string playerUid)
        {
            int removed = 0;
            foreach (var pair in watchers.Where(p => p.Value.uid == playerUid).ToList())
            {
                if (!watchers.TryRemove(pair.Key, out _)) continue;
                recent.TryRemove(pair.Key, out _);
                removed++;
            }
            return removed;
        }

        public static bool IsWatched(long mobId) => !watchers.IsEmpty && watchers.ContainsKey(mobId);

        // oldest first
        public static IReadOnlyList<string> Recent(long mobId)
        {
            if (!recent.TryGetValue(mobId, out var lines)) return Array.Empty<string>();
            lock (lines) return lines.ToArray();
        }

        public static void Log(Entity mob, string line)
        {
            if (!watchers.TryGetValue(mob.EntityId, out var watcher)) return;
            var lines = recent.GetOrAdd(mob.EntityId, _ => new Queue<string>());
            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > RecentLines) lines.Dequeue();
            }
            if (watcher.toChat && mob.World.PlayerByUid(watcher.uid) is IServerPlayer player)
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"[brain {mob.EntityId}] {line}", EnumChatType.Notification);
        }

        public static int PruneMissing(IWorldAccessor world)
        {
            int removed = 0;
            foreach (long id in watchers.Keys.ToList())
            {
                if (world.GetEntityById(id) != null || !watchers.TryRemove(id, out _)) continue;
                recent.TryRemove(id, out _);
                removed++;
            }
            return removed;
        }

        public static void Clear()
        {
            watchers.Clear();
            recent.Clear();
        }
    }
}
