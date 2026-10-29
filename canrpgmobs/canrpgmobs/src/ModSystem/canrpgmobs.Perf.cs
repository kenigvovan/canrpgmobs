using System;
using System.Diagnostics;
using System.Linq;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs
{
    // /canrpgmobs debug perf|stress
    public partial class canrpgmobsModSystem
    {
        private const double MobBudgetMs = 5.0;
        private const int MobBudgetCount = 60;

        private bool measuring;

        private TextCommandResult OnPerf(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server only.");
            int seconds = GameMath.Clamp((int)args[0], 2, 120);
            return Measure(seconds, args.Caller.Player as IServerPlayer, "perf")
                ? TextCommandResult.Success($"Measuring for {seconds}s...")
                : TextCommandResult.Error("A measurement is already running.");
        }

        private TextCommandResult OnStress(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not EntityPlayer player) return NoPlayer();
            if (measuring) return TextCommandResult.Error("A measurement is already running.");

            int count = GameMath.Clamp((int)args[0], 2, 200);
            string keyA = MobSpawner.NormalizeKey(args[1] as string ?? "goblin|brute");
            string keyB = MobSpawner.NormalizeKey(args[2] as string ?? "human|brute");
            int seconds = GameMath.Clamp((int)args[3], 5, 120);

            var mobA = Catalog.Resolve(keyA, out var errorA);
            if (mobA == null) return TextCommandResult.Error($"'{keyA}': {errorA}");
            var mobB = Catalog.Resolve(keyB, out var errorB);
            if (mobB == null) return TextCommandResult.Error($"'{keyB}': {errorB}");
            bool hostile = Catalog.Factions.AttacksOnSight(mobA.Faction, mobB.Faction) || Catalog.Factions.AttacksOnSight(mobB.Faction, mobA.Faction);

            // two groups 8 blocks apart, 10 per row, 12 blocks ahead
            float yaw = player.Pos.Yaw;
            int half = count / 2, spawned = 0;
            string? error = null;
            for (int i = 0; i < count && error == null; i++)
            {
                bool first = i < half;
                int index = first ? i : i - half;
                double across = (index % 10 - 4.5) * 1.5;
                double ahead = 12.0 + (first ? -4.0 - index / 10 * 1.5 : 4.0 + index / 10 * 1.5);
                var position = player.Pos.XYZ.AheadCopy(ahead, 0f, yaw).AddCopy(Math.Cos(yaw) * across, 0, -Math.Sin(yaw) * across);
                var request = new MobSpawnRequest(first ? keyA : keyB, position, null, first ? yaw : yaw + GameMath.PI);
                if (MobSpawner.TrySpawn(sapi, request, out error) != null) spawned++;
            }
            if (spawned == 0) return TextCommandResult.Error($"Could not spawn: {error}");

            var caller = args.Caller.Player as IServerPlayer;
            // let them meet first
            sapi.World.RegisterCallback(_ => Measure(seconds, caller, $"stress {spawned} mobs"), 3000);
            return TextCommandResult.Success($"Spawned {spawned} mob(s) ahead{(error != null ? $" (stopped early: {error})" : "")}; measuring for {seconds}s in 3s."
                                             + (hostile ? "" : " Warning: these factions don't attack each other on sight, the mobs may stay idle.")
                                             + " /canrpgmobs kill 64 removes them.");
        }

        private bool Measure(int seconds, IServerPlayer? caller, string label)
        {
            if (sapi == null || measuring) return false;
            measuring = true;

            int serverTicks = 0;
            long listener = sapi.Event.RegisterGameTickListener(_ => serverTicks++, 1);
            Debug.MobPerf.Take();
            long started = Stopwatch.GetTimestamp();

            sapi.World.RegisterCallback(_ =>
            {
                sapi.Event.UnregisterGameTickListener(listener);
                measuring = false;
                var (elapsed, entityTicks) = Debug.MobPerf.Take();
                double wallSeconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                double totalMs = elapsed * 1000.0 / Stopwatch.Frequency;
                int mobs = sapi.World.LoadedEntities.Values.Count(e => e is EntityRpgMob && e.Alive);
                double perServerTick = serverTicks > 0 ? totalMs / serverTicks : 0;
                double perMobTickUs = entityTicks > 0 ? totalMs * 1000.0 / entityTicks : 0;

                string budget = "";
                if (mobs > 0)
                {
                    double per60 = perServerTick * MobBudgetCount / mobs;
                    budget = $"; scaled to {MobBudgetCount} mobs {per60:0.##} ms, {(per60 < MobBudgetMs ? "within" : "OVER")} the {MobBudgetMs:0} ms budget";
                }
                string message = $"[{label}] {wallSeconds:0.#}s, {serverTicks} server ticks, {mobs} mob(s) loaded: "
                                 + $"{perServerTick:0.###} ms of each server tick in mobs, {perMobTickUs:0} us per mob tick{budget}.";
                caller?.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.CommandSuccess);
                Mod.Logger.Notification("[canrpgmobs] " + message);
            }, seconds * 1000);
            return true;
        }
    }
}
