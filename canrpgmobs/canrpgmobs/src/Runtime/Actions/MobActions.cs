using System;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Ai;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Factions;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Actions
{
    // Unknown types and refusals are skipped silently, the validator reports unknown types.
    public static class MobActions
    {
        public static bool TryRun(in MobActionContext ctx, SkillActionModel action)
        {
            var implementation = MobsApi.Actions.Get(action.type);
            if (implementation == null || implementation.Refusal(in ctx, action) != null) return false;
            try
            {
                implementation.Run(in ctx, action);
                return true;
            }
            catch (Exception e)
            {
                ctx.Self.Api.Logger.Error("[canrpgmobs] action {0} of {1} failed: {2}", action.type, ctx.Mob.MobKey, e);
                return false;
            }
        }
    }

    [MobActionType("summon", "Spawns count (default 1) of mob within radius (default 3) blocks, at most maxAlive (default count) at a time; "
                             + "level 0 or left out uses the summoner's level. Summons fight for the summoner's faction and drop no loot")]
    public sealed class SummonAction : IMobAction
    {
        public ParamInfo[] Params { get; } =
        {
            ParamInfo.Mob("mob"), ParamInfo.Count("count", required: false, max: 10f), ParamInfo.Count("maxAlive", required: false, max: 20f),
            ParamInfo.Blocks("radius", required: false, max: 16f), ParamInfo.Count("level", required: false, max: 1000f)
        };

        public string? Refusal(in MobActionContext ctx, SkillActionModel a)
        {
            if (string.IsNullOrWhiteSpace(a.mob)) return "no mob to summon";
            return ctx.Mob.AliveSummons() >= MaxAlive(a) ? "summons alive" : null;
        }

        public void Run(in MobActionContext ctx, SkillActionModel a)
        {
            if (ctx.Self.Api is not ICoreServerAPI sapi) return;
            int count = Math.Min(Count(a), MaxAlive(a) - ctx.Mob.AliveSummons());
            float radius = Math.Clamp(a.radius ?? 3f, 1f, 16f);
            int level = a.level is > 0f ? (int)a.level.Value : ctx.Mob.Level;
            string faction = FactionService.FactionOf(ctx.Self, ctx.Catalog);
            var enemy = ctx.Target != ctx.Self ? ctx.Target : ctx.Mob.CurrentEnemy;

            for (int i = 0; i < count; i++)
            {
                double angle = sapi.World.Rand.NextDouble() * GameMath.TWOPI;
                double distance = 1.0 + sapi.World.Rand.NextDouble() * (radius - 1f);
                var position = ctx.Self.Pos.XYZ.AddCopy(Math.Sin(angle) * distance, 0, Math.Cos(angle) * distance);
                // owner set before spawn so MobSpawned handlers see it
                var request = new MobSpawnRequest(a.mob!, position, level, ctx.Self.Pos.Yaw, FactionOverride: faction, Owner: ctx.Self.EntityId);
                var summoned = MobSpawner.TrySpawn(sapi, request, out var error);
                if (summoned == null)
                {
                    sapi.Logger.Warning("[canrpgmobs] {0} could not summon {1}: {2}", ctx.Mob.MobKey, a.mob, error);
                    return;
                }
                ctx.Mob.AdoptSummon(summoned, a.despawnWithOwner);
                if (enemy != null && enemy.Alive) summoned.GetBehavior<EBRpgMob>()?.AddThreat(enemy, 1f);
            }
        }

        private static int Count(SkillActionModel a) => Math.Clamp((int)(a.count ?? 1f), 1, 10);
        private static int MaxAlive(SkillActionModel a) => Math.Clamp((int)(a.maxAlive ?? Count(a)), 1, 20);
    }

    [MobActionType("enrage", "Multiplies melee damage by value and spell power by spellPower (both default 1.5) for seconds, "
                             + "or until the fight ends (death or walking home) when left out")]
    public sealed class EnrageAction : IMobAction
    {
        public ParamInfo[] Params { get; } =
        {
            ParamInfo.Multiplier("value", required: false), ParamInfo.Multiplier("spellPower", required: false), ParamInfo.Seconds("seconds", required: false)
        };

        public string? Refusal(in MobActionContext ctx, SkillActionModel a) => ctx.Mob.IsEnraged ? "already enraged" : null;

        public void Run(in MobActionContext ctx, SkillActionModel a) => ctx.Mob.Enrage(a.value ?? 1.5f, a.spellPower ?? 1.5f, a.seconds ?? 0f);
    }

    [MobActionType("steal", "Takes up to count (default 1) items of a random hotbar stack from a player target and runs away; "
                            + "what it carries drops when it dies")]
    public sealed class StealAction : IMobAction
    {
        public const int MaxCarried = 8;

        public ParamInfo[] Params { get; } = { ParamInfo.Count("count", required: false, max: 64f) };

        public string? Refusal(in MobActionContext ctx, SkillActionModel a)
        {
            if (ctx.Target is not EntityPlayer player || player.Player is not IServerPlayer) return "target is not a player";
            if (player.Player.WorldData?.CurrentGameMode is EnumGameMode.Creative or EnumGameMode.Spectator) return "target is not a player";
            if (ctx.Mob.StolenCount >= MaxCarried) return "pockets full";
            return Hotbar(player) is { } hotbar && Filled(hotbar) > 0 ? null : "nothing to steal";
        }

        public void Run(in MobActionContext ctx, SkillActionModel a)
        {
            if (ctx.Target is not EntityPlayer player || player.Player is not IServerPlayer serverPlayer || Hotbar(player) is not { } hotbar) return;
            int filled = Filled(hotbar);
            if (filled == 0) return;

            int pick = ctx.Self.World.Rand.Next(filled);
            ItemSlot? slot = null;
            for (int i = 0; i < hotbar.Count && slot == null; i++)
                if (hotbar[i] is { Empty: false } candidate && pick-- == 0) slot = candidate;
            if (slot == null) return;

            var taken = slot.TakeOut(Math.Clamp((int)(a.count ?? 1f), 1, 64));
            slot.MarkDirty();
            if (taken == null || taken.StackSize <= 0) return;

            ctx.Mob.AddStolen(taken);
            serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.GetL(serverPlayer.LanguageCode, "canrpgmobs:mob-stole", ctx.Self.GetName(), taken.StackSize, taken.GetName()), EnumChatType.Notification);
            ctx.Self.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.GetTask<AiTaskRpgFlee>()?.InstaFleeFrom(player);
        }

        private static IInventory? Hotbar(EntityPlayer player) => player.Player?.InventoryManager?.GetOwnInventory(GlobalConstants.hotBarInvClassName);

        private static int Filled(IInventory inventory)
        {
            int filled = 0;
            for (int i = 0; i < inventory.Count; i++)
                if (inventory[i] is { Empty: false }) filled++;
            return filled;
        }
    }
}
