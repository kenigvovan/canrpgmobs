using System;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Audio;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Factions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Behaviors
{
    // Targeting: factions, threat list, current enemy.
    public partial class EBRpgMob
    {
        private readonly Vec3d sightFrom = new(), sightTo = new();

        public bool IsEnemy(Entity other)
        {
            if (Mob == null || IsLeashing || other == entity || !other.Alive || other is not EntityAgent) return false;
            if (other.Pos.Dimension != entity.Pos.Dimension) return false;
            bool passiveWorld = false;
            if (other is EntityPlayer player)
            {
                if (player.Player?.WorldData?.CurrentGameMode is EnumGameMode.Creative or EnumGameMode.Spectator) return false;
                if (player.Player is IServerPlayer { ConnectionState: not EnumClientState.Playing }) return false;
                // creatureHostility can change via /worldconfig, re-read periodically
                long now = entity.World.ElapsedMilliseconds;
                if (now >= hostilityUntilMs)
                {
                    hostility = entity.World.Config.GetString("creatureHostility");
                    hostilityUntilMs = now + HostilityRecheckMs;
                }
                if (hostility == "off") return false;
                passiveWorld = hostility == "passive";
            }
            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            if (catalog == null) return false;

            // faction index is per matrix, recompute after a catalog reload
            var factions = catalog.Factions;
            if (!ReferenceEquals(factions, myFactionOf))
            {
                myFactionOf = factions;
                myFaction = FactionService.FactionOf(entity, catalog);
                myFactionIndex = factions.IndexOf(myFaction);
            }

            string theirs = FactionService.FactionOf(other, catalog);
            int theirsIndex = factions.IndexOf(theirs);
            bool ally = myFactionIndex >= 0 && theirsIndex >= 0
                ? factions.IsAlly(myFactionIndex, theirsIndex)
                : factions.IsAlly(myFaction!, theirs);
            if (ally) return false;
            if (HasThreat(other.EntityId)) return true;
            if (passiveWorld) return false;
            return myFactionIndex >= 0 && theirsIndex >= 0
                ? factions.AttacksOnSight(myFactionIndex, theirsIndex)
                : factions.AttacksOnSight(myFaction!, theirs);
        }

        // shared by all AI tasks, kept for switchCooldown
        public Entity? AcquireEnemy(float range)
        {
            if (Mob == null || IsLeashing || !entity.Alive) return null;
            long now = entity.World.ElapsedMilliseconds;
            double keepRange = range * 1.5;
            // AddThreat resets calmUntilMs
            if (currentEnemy == null && now < calmUntilMs && range <= calmRange) return null;

            if (currentEnemy != null && (!IsEnemy(currentEnemy) || entity.Pos.DistanceTo(currentEnemy.Pos) > keepRange)) SetEnemy(null);
            if (currentEnemy != null && now < enemyRecheckMs) return currentEnemy;

            var targeting = Mob.Ai.targeting;
            enemyRecheckMs = now + (long)(Math.Clamp(targeting?.switchCooldown ?? 1f, 0.25f, 30f) * 1000f);

            // unknown selector falls back to threat
            var selector = MobsApi.EnemySelectors.Get(targeting?.selector ?? TargetingModel.Threat)
                           ?? MobsApi.EnemySelectors.Get(TargetingModel.Threat);
            Entity? pick = selector?.Pick(new EnemyPickContext(this, range, keepRange));
            // avoid flip-flopping between similar attackers
            if (selector?.StickyByThreat == true && pick != null && currentEnemy != null && pick != currentEnemy
                && ThreatOf(pick) <= ThreatOf(currentEnemy) * ThreatSwitchMul)
                pick = currentEnemy;

            bool wasCalm = currentEnemy == null;
            SetEnemy(pick ?? currentEnemy);
            if (currentEnemy == null)
            {
                calmUntilMs = now + CalmRecheckMs;
                calmRange = range;
            }
            if (currentEnemy != null && combatStartMs == 0) combatStartMs = Math.Max(1, now);
            if (wasCalm && currentEnemy != null && now >= nextAggroSoundMs && Mob.Sounds.TryGetValue(SoundKeys.Aggro, out var aggro))
            {
                nextAggroSoundMs = now + AggroSoundCooldownMs;
                MobAudio.Play(entity, aggro);
            }
            return currentEnemy;
        }

        // all enemy changes go through here for the event
        internal void SetEnemy(Entity? next)
        {
            if (ReferenceEquals(currentEnemy, next)) return;
            var before = currentEnemy;
            currentEnemy = next;
            if (MobsApi.HasEnemyListeners) MobsApi.RaiseEnemyChanged(new EnemyChangedEvent(entity, before, next), entity.Api.Logger);
        }

        public void AddThreat(Entity source, float amount)
        {
            if (IsLeashing || source == entity) return;
            calmUntilMs = 0;
            long now = entity.World.ElapsedMilliseconds;
            float before = threat.TryGetValue(source.EntityId, out var entry) && now - entry.lastMs < ThreatMemoryMs ? entry.amount : 0f;
            float total = before + Math.Max(0f, amount);
            threat[source.EntityId] = (total, now);
            if (combatStartMs == 0) combatStartMs = Math.Max(1, now);
            if (MobsApi.HasThreatListeners) MobsApi.RaiseThreatAdded(new ThreatAddedEvent(entity, source, Math.Max(0f, amount), total), entity.Api.Logger);
        }

        public bool HasThreat(long entityId)
            => threat.TryGetValue(entityId, out var entry) && entity.World.ElapsedMilliseconds - entry.lastMs < ThreatMemoryMs;

        public float ThreatOf(Entity? other)
            => other != null && threat.TryGetValue(other.EntityId, out var entry) && entity.World.ElapsedMilliseconds - entry.lastMs < ThreatMemoryMs
                ? entry.amount : 0f;

        public Entity? TopThreat(System.Func<Entity, bool>? accept = null)
        {
            if (threat.Count == 0) return null;
            long now = entity.World.ElapsedMilliseconds;
            Entity? best = null;
            float bestAmount = 0f;
            foreach (var pair in threat)
            {
                if (now - pair.Value.lastMs >= ThreatMemoryMs || pair.Value.amount <= bestAmount) continue;
                var candidate = entity.World.GetEntityById(pair.Key);
                if (candidate == null || !candidate.Alive || candidate.Pos.Dimension != entity.Pos.Dimension) continue;
                if (accept != null && !accept(candidate)) continue;
                best = candidate;
                bestAmount = pair.Value.amount;
            }
            return best;
        }

        public Entity? ThreatEnemy(double keepRange)
            => TopThreat(candidate => IsEnemy(candidate) && entity.Pos.DistanceTo(candidate.Pos) <= keepRange);

        // only for new enemies, attackers come through ThreatEnemy without a sight check
        public bool CanSee(Entity other)
        {
            sightFrom.Set(entity.Pos.X, entity.Pos.InternalY + entity.Properties.EyeHeight, entity.Pos.Z);
            sightTo.Set(other.Pos.X, other.Pos.InternalY + other.SelectionBox.Y2 * 0.8f, other.Pos.Z);
            BlockSelection? blockSel = null;
            EntitySelection? entitySel = null;
            // only blocks with collision boxes block sight
            entity.World.RayTraceForSelection(sightFrom, sightTo, ref blockSel, ref entitySel,
                (_, block) => block.CollisionBoxes is { Length: > 0 }, _ => false);
            return blockSel == null;
        }

        public Entity? NearestEnemy(float range)
            => entity.World.GetNearestEntity(entity.Pos.XYZ, range, range / 2f + 4f, e => IsEnemy(e) && CanSee(e));

        public Entity? WeakestEnemy(float range)
        {
            Entity? best = null;
            float bestFraction = float.MaxValue;
            foreach (var candidate in entity.World.GetEntitiesAround(entity.Pos.XYZ, range, range / 2f + 4f, e => IsEnemy(e) && CanSee(e)))
            {
                float fraction = BrainContext.HpFraction(candidate);
                if (fraction >= bestFraction) continue;
                best = candidate;
                bestFraction = fraction;
            }
            return best;
        }

        private void PruneThreat(long now)
        {
            if (threat.Count > 0)
            {
                staleThreat.Clear();
                foreach (var pair in threat)
                {
                    if (now - pair.Value.lastMs >= ThreatMemoryMs) { staleThreat.Add(pair.Key); continue; }
                    var source = entity.World.GetEntityById(pair.Key);
                    if (source == null || !source.Alive) staleThreat.Add(pair.Key);
                }
                foreach (long id in staleThreat) threat.Remove(id);
            }

            if (currentEnemy != null && !IsEnemy(currentEnemy)) SetEnemy(null);
            if (threat.Count == 0 && currentEnemy == null && combatStartMs != 0)
            {
                combatStartMs = 0;
                // fight over; phases stay until the mob leashes home
                EndEnrage();
            }
        }

        private void CallForHelp(MobCatalog catalog, string mine, Entity attacker)
        {
            long now = entity.World.ElapsedMilliseconds;
            if (now < nextCallForHelpMs) return;
            nextCallForHelpMs = now + CallForHelpCooldownMs;

            float range = Mob!.Ai.callForHelpRange;
            if (range <= 0f) return;
            var factions = catalog.Factions;
            string attackerFaction = FactionService.FactionOf(attacker, catalog);

            var nearby = entity.World.GetEntitiesAround(entity.Pos.XYZ, range, range / 2f + 2f,
                e => e is EntityRpgMob && e != entity && e.Alive && e.Pos.Dimension == entity.Pos.Dimension);
            foreach (var other in nearby)
            {
                string theirs = FactionService.FactionOf(other, catalog);
                if (!factions.IsAlly(theirs, mine) || !factions.HelpsAllies(theirs) || factions.IsAlly(theirs, attackerFaction)) continue;
                other.GetBehavior<EBRpgMob>()?.AddThreat(attacker, HelpThreat);
            }
        }
    }
}
