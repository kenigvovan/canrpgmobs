using System;
using canrpgclasses.Core;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Actions;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Audio;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Scaling;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Behaviors
{
    // Boss phases and enrage.
    public partial class EBRpgMob
    {
        private void CheckPhases()
        {
            float fraction = HpFraction;
            while (Mob != null && phaseIndex < Mob.Phases.Count && fraction <= Mob.Phases[phaseIndex].hpBelow)
            {
                var phase = Mob.Phases[phaseIndex];
                phaseIndex++;
                EnterPhase(phase);
            }
        }

        private void EnterPhase(BossPhaseModel phase)
        {
            Identity?.SetInt(MobKeys.Phase, phaseIndex);
            ApplyStatLayers();
            if (!string.IsNullOrEmpty(phase.sound)) MobAudio.PlayPath(entity, phase.sound!, 32f);
            if (!string.IsNullOrEmpty(phase.animation)) PlayOnce(phase.animation!);
            Announce(phase);

            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            if (catalog != null && phase.actions != null && entity is EntityAgent agent)
            {
                var ctx = new MobActionContext(agent, this, catalog, currentEnemy ?? entity);
                foreach (var action in phase.actions)
                    if (action != null) MobActions.TryRun(in ctx, action);
            }
            MobsApi.RaiseMobPhaseEntered(new MobPhaseEnteredEvent(entity, Mob!, phase, phaseIndex), entity.Api.Logger);
        }

        private void ResetPhases()
        {
            if (phaseIndex == 0) return;
            phaseIndex = 0;
            Identity?.SetInt(MobKeys.Phase, 0);
            ApplyStatLayers();
        }

        private void Announce(BossPhaseModel phase)
        {
            if (phase.announce == null) return;
            string name = entity.GetName();
            foreach (var player in entity.World.AllOnlinePlayers)
            {
                if (player is not IServerPlayer serverPlayer || serverPlayer.Entity is not { } playerEntity) continue;
                if (playerEntity.Pos.Dimension != entity.Pos.Dimension || playerEntity.Pos.DistanceTo(entity.Pos) > AnnounceRange) continue;
                string text = phase.announce.Resolve(serverPlayer.LanguageCode, "");
                if (text.Length > 0) serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, text.Replace("{name}", name), EnumChatType.Notification);
            }
        }

        // animation set key or raw code
        private void PlayOnce(string code)
        {
            var meta = MobAnimations.Meta(entity, code);
            entity.AnimManager?.StartAnimation(meta);
            entity.World.RegisterCallback(_ => entity.AnimManager?.StopAnimation(meta.Code), 2000);
        }

        // seconds = 0: until the fight ends
        public void Enrage(float damageMul, float spellPowerMul, float seconds)
        {
            IsEnraged = true;
            int token = ++enrageToken;
            enrageDamageMul = SafeMul(damageMul);
            enrageSpellPowerMul = SafeMul(spellPowerMul);
            ApplyStatLayers();
            if (seconds > 0f && float.IsFinite(seconds))
                entity.World.RegisterCallback(_ => { if (token == enrageToken && entity.Alive) EndEnrage(); }, (int)(Math.Min(seconds, 3600f) * 1000f));
        }

        private void EndEnrage()
        {
            if (!IsEnraged) return;
            IsEnraged = false;
            enrageToken++;
            enrageDamageMul = enrageSpellPowerMul = 1f;
            ApplyStatLayers();
        }

        // One stat source for both, otherwise VS sums them across sources.
        // Damage stats have base 1, so they get mul - 1.
        private void ApplyStatLayers()
        {
            var phase = CurrentPhase;
            float damageMul = SafeMul(phase?.damageMul ?? 1f) * enrageDamageMul;
            float spellPowerMul = SafeMul(phase?.spellPowerMul ?? 1f) * enrageSpellPowerMul;

            // melee via MobDamageHooks, arrows via EntityProjectileBase
            foreach (var key in WeaponDamageKeys)
            {
                if (damageMul == 1f) entity.Stats.Remove(key, LayerStatSource);
                else entity.Stats.Set(key, LayerStatSource, damageMul - 1f);
            }

            float spellPower = ScaledStats?.SpellPower ?? 1f;
            foreach (var key in MobStatApplier.SpellPowerKeys)
            {
                if (spellPowerMul == 1f) entity.Stats.Remove(key, LayerStatSource);
                else entity.Stats.Set(key, LayerStatSource, spellPower * (spellPowerMul - 1f));
            }
        }

        private static readonly string[] WeaponDamageKeys = { StatKeys.MeleeWeaponsDamage, StatKeys.RangedWeaponsDamage };

        private static float SafeMul(float value) => float.IsFinite(value) && value >= 0f ? value : 1f;
    }
}
