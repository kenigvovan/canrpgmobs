using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using canrpgclasses.Core;
using canrpgclasses.Core.EB;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Ai;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Audio;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Factions;
using canrpgmobs.Runtime.Scaling;
using canrpgmobs.Runtime.Spawning;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Behaviors
{
    // Server-side mob state: archetype, stats, AI, threat, leash.
    // Must come after "health" and "taskai" in the entity JSON.
    public partial class EBRpgMob : EntityBehavior
    {
        public const string Name = "canrpgmobs:mob";
        // phase and enrage multipliers share one stat source so they multiply
        public const string LayerStatSource = "canrpgmobs:layers";

        private const long ThreatMemoryMs = 20000;
        private const long CallForHelpCooldownMs = 2000;
        private const long LeashCheckMs = 1000;
        private const long LeashTimeoutMs = 20000;
        private const long ZoneLeashCheckMs = 1000;
        private const float LeashArrivalDistance = 1.5f;
        // token threat for helpers, easy to out-threaten
        private const float HelpThreat = 1f;
        // threat margin needed to pull the mob off its current enemy
        private const float ThreatSwitchMul = 1.3f;
        private const long CalmRecheckMs = 750;
        private const long HostilityRecheckMs = 5000;
        private const long AggroSoundCooldownMs = 10000;
        private const float AnnounceRange = 48f;
        private const string StolenTree = "canrpgmobsStolen";
        private const string SummonsAttr = "canrpgmobsSummons";
        private const string BoundSummonsAttr = "canrpgmobsSummonsBound";

        // warn once per key
        private static readonly ConcurrentDictionary<string, byte> warnedKeys = new();

        private readonly Dictionary<long, (float amount, long lastMs)> threat = new();
        private readonly List<long> staleThreat = new();
        private Vec3d? anchor;
        private Entity? currentEnemy;
        private long enemyRecheckMs;
        // nobody found in calmRange; every AI task asks, so throttle the rescan
        private long calmUntilMs;
        private float calmRange;
        // unchanged signature = skip reapply
        private string appliedSignature = "";
        private long combatStartMs;
        private long nextLeashCheckMs;
        private long nextCallForHelpMs;
        private long leashStartedMs;
        private long zoneLeashAtMs;
        private bool lootDropped;
        private long nextAggroSoundMs;
        private string aiHash = "";

        private readonly List<(long id, bool despawnWithOwner)> summons = new();
        private int phaseIndex;
        private EntityBehaviorHealth? health;
        private bool healthSearched;
        // cached for IsEnemy, called per candidate
        private string? hostility;
        private long hostilityUntilMs;
        private string? myFaction;
        private int myFactionIndex = -1;
        private FactionMatrix? myFactionOf;
        private int enrageToken;
        private float enrageDamageMul = 1f;
        private float enrageSpellPowerMul = 1f;

        public EBRpgMob(Entity entity) : base(entity) { }
        public override string PropertyName() => Name;

        public ResolvedMob? Mob { get; private set; }
        public MobStats? ScaledStats { get; private set; }

        // walking home after a too-long chase, ignores enemies and damage
        public bool IsLeashing { get; private set; }
        // set by the cast brain, movement tasks yield
        public bool HoldPosition { get; set; }
        public bool IsFleeing { get; set; }
        // bumped on leash reset so once-per-life rules can fire again
        public int LifeEpoch { get; private set; }
        public Vec3d? Anchor => anchor;
        public int ThreatCount => threat.Count;
        public Entity? CurrentEnemy => currentEnemy;
        public float CombatSeconds => combatStartMs == 0 ? 0f : (entity.World.ElapsedMilliseconds - combatStartMs) / 1000f;
        // 0 = no phase yet
        public int PhaseIndex => phaseIndex;
        public BossPhaseModel? CurrentPhase => Mob != null && phaseIndex > 0 && phaseIndex <= Mob.Phases.Count ? Mob.Phases[phaseIndex - 1] : null;
        public bool IsEnraged { get; private set; }
        public int StolenCount => entity.Attributes.GetTreeAttribute(StolenTree)?.Count ?? 0;

        // cached, asked for every tick
        public EntityBehaviorHealth? Health
        {
            get
            {
                if (healthSearched) return health;
                healthSearched = true;
                return health = entity.GetBehavior<EntityBehaviorHealth>();
            }
        }

        public float HpFraction => Health is { MaxHealth: > 0f } h ? h.Health / h.MaxHealth : 1f;

        private ITreeAttribute? Identity => MobKeys.Identity(entity);
        public string? MobKey => Identity?.GetString(MobKeys.Key);
        public int Level => Identity?.GetInt(MobKeys.Level, 1) ?? 1;

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            if (entity.Api.Side != EnumAppSide.Server || entity is not EntityAgent agent) return;
            var mod = canrpgmobsModSystem.For(entity.Api);
            if (mod == null) return;

            mod.Tracker.Add(this);

            var identity = Identity;
            if (identity != null)
            {
                int version = identity.GetInt(MobKeys.FormatVersion, 1);
                int migrated = FormatMigrator.MigrateIdentity(version);
                if (migrated != version) identity.SetInt(MobKeys.FormatVersion, migrated);
            }

            string? key = identity?.GetString(MobKeys.Key);
            Mob = key == null ? null : mod.Catalog.Resolve(key);
            Mob ??= ApplyMissingPolicy(mod, identity, key);
            if (Mob == null) return;
            if (identity != null) SyncFaction(identity, Mob);

            // the brain handles interrupts per rule, pathing must not cancel casts
            var caster = entity.GetBehavior<EBSpellCaster>();
            if (caster != null) caster.InterruptFlags = CastInterruptFlags.None;

            ScaledStats = StatMath.Compute(Mob, Level, mod.Catalog.Tier(identity?.GetString(MobKeys.Tier)), mod.Catalog.Scaling, MobGear.Weapon(entity.World, identity));
            appliedSignature = ReapplySignature(mod, Mob, identity);

            try { aiHash = AiComposer.Compose(agent, Mob, ScaledStats); }
            catch (Exception e) { entity.Api.Logger.Error("[canrpgmobs] could not build AI for {0}: {1}", Mob.Key, e); }
        }

        public override void AfterInitialized(bool onFirstSpawn)
        {
            base.AfterInitialized(onFirstSpawn);
            if (entity.Api.Side != EnumAppSide.Server || Mob == null || !entity.Alive) return;
            int simRange = canrpgmobsModSystem.For(entity.Api)?.Settings.mobSimulationRange ?? 0;
            if (simRange > 0) entity.SimulationRange = simRange;
            if (ScaledStats != null) MobStatApplier.Apply(entity, ScaledStats, onFirstSpawn);
            if (Identity?.GetBool(MobKeys.Preview) == true)
            {
                int seconds = canrpgmobsModSystem.For(entity.Api)?.Settings.maxPreviewSeconds ?? 60;
                // preview loaded from a save is stale, remove it now
                RemoveLater(onFirstSpawn ? seconds * 1000 : 1);
            }
            // old saves without an anchor
            anchor = ReadAnchor();
            if (anchor == null) SetAnchor(entity.Pos.XYZ);
            // restore the saved phase silently
            phaseIndex = Math.Clamp(Identity?.GetInt(MobKeys.Phase) ?? 0, 0, Mob.Phases.Count);
            ApplyStatLayers();
            LoadSummons();
            // also fires for mobs loaded with their chunk
            if (ScaledStats != null) MobsApi.RaiseMobLoaded(new MobLoadedEvent(entity, Mob, ScaledStats, onFirstSpawn), entity.Api.Logger);
        }

        public override void OnGameTick(float deltaTime)
        {
            if (entity.Api.Side != EnumAppSide.Server || Mob == null || !entity.Alive) return;
            // no player near, the AI sleeps too
            if (entity.State != EnumEntityState.Active) return;
            long now = entity.World.ElapsedMilliseconds;
            // every tick so a big hit triggers the phase right away
            if (!IsLeashing && phaseIndex < Mob.Phases.Count) CheckPhases();
            if (now < nextLeashCheckMs) return;
            nextLeashCheckMs = now + LeashCheckMs;

            if (IsLeashing)
            {
                if (now - leashStartedMs > LeashTimeoutMs) EndLeash(false);
                return;
            }

            PruneThreat(now);
            if (anchor != null && now >= zoneLeashAtMs)
            {
                zoneLeashAtMs = now + ZoneLeashCheckMs;
                if (OutsideOwnZone())
                {
                    StartLeash(now);
                    return;
                }
            }
            float leash = Mob.Ai.leashRange;
            if (leash <= 0f || anchor == null) return;
            double dx = entity.Pos.X - anchor.X, dy = entity.Pos.InternalY - anchor.Y, dz = entity.Pos.Z - anchor.Z;
            if (dx * dx + dy * dy + dz * dz > leash * leash) StartLeash(now);
        }

        public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        {
            base.OnEntityReceiveDamage(damageSource, ref damage);
            if (entity.Api.Side != EnumAppSide.Server || Mob == null || !entity.Alive || IsLeashing) return;
            if (damageSource.Type == EnumDamageType.Heal) return;

            var cause = damageSource.GetCauseEntity();
            if (cause == null || cause == entity || !cause.Alive) return;
            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            if (catalog == null) return;

            // ignore friendly fire
            string mine = FactionService.FactionOf(entity, catalog);
            if (catalog.Factions.IsAlly(mine, FactionService.FactionOf(cause, catalog))) return;

            AddThreat(cause, Math.Max(1f, damage));
            CallForHelp(catalog, mine, cause);
        }

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            base.OnEntityDeath(damageSourceForDeath);
            DropCombat();
            IsLeashing = false;
            if (entity.Api.Side != EnumAppSide.Server) return;
            DropLoot(damageSourceForDeath);
            DropStolen();
            DespawnSummons();
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            base.OnEntityDespawn(despawn);
            if (entity.Api.Side != EnumAppSide.Server) return;
            canrpgmobsModSystem.For(entity.Api)?.Tracker.Remove(this);
            // on unload the mob keeps its stolen items
            if (despawn?.Reason != EnumDespawnReason.Unload)
            {
                DropStolen();
                DespawnSummons();
            }
            // deaths have their own event
            if (Mob != null && entity.Alive)
                MobsApi.RaiseMobDespawned(new MobDespawnedEvent(entity, Mob, despawn?.Reason ?? EnumDespawnReason.Removed), entity.Api.Logger);
        }

        // Server, after a catalog change. AI is rebuilt only if task configs changed,
        // appearance only with hotApplyAppearance. Never throws.
        public void Reapply(canrpgmobsModSystem mod)
        {
            if (entity.Api.Side != EnumAppSide.Server || entity is not EntityAgent agent || !entity.Alive) return;
            try { ReapplyCore(mod, agent); }
            catch (Exception e) { WarnOnce("reapply:" + (MobKey ?? "?"), $"could not update {MobKey} after a catalog change: {e}"); }
        }

        private void ReapplyCore(canrpgmobsModSystem mod, EntityAgent agent)
        {
            var identity = Identity;
            string? key = identity?.GetString(MobKeys.Key);
            var fresh = key == null ? null : mod.Catalog.Resolve(key);
            if (fresh == null)
            {
                // missing-archetype policy applies on next load
                WarnOnce("gone:" + key, $"mob archetype '{key}' was removed; live mobs keep their current setup until they reload");
                return;
            }

            // unchanged mob: just take the new rule list (cooldowns carry over by id)
            string signature = ReapplySignature(mod, fresh, identity);
            if (signature == appliedSignature)
            {
                Mob = fresh;
                return;
            }
            appliedSignature = signature;

            Mob = fresh;
            var tier = mod.Catalog.Tier(identity!.GetString(MobKeys.Tier));
            SyncFaction(identity, fresh);
            identity.SetString(MobKeys.FallbackName, tier?.FormatName(fresh.DisplayName("en"), Level, "en") ?? fresh.DisplayName("en"));

            // before stats: the held weapon affects melee damage
            if (mod.Settings.hotApplyAppearance) RefreshAppearance(agent, identity, fresh, tier);
            else WarnIfArcherWithoutWeapon(fresh);

            ScaledStats = StatMath.Compute(fresh, Level, tier, mod.Catalog.Scaling, MobGear.Weapon(entity.World, identity));
            MobStatApplier.Apply(entity, ScaledStats, false);
            phaseIndex = Math.Min(phaseIndex, fresh.Phases.Count);
            ApplyStatLayers();
            MobAnimations.Apply(entity, fresh.Anims);
            MobAudio.ApplyEntitySounds(entity, fresh);

            string hash = AiComposer.SpecHash(agent, fresh, ScaledStats);
            if (hash != aiHash) aiHash = AiComposer.Rebuild(agent, fresh, ScaledStats);
            else entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.GetTask<AiTaskRpgCast>()?.OnSkillsChanged(fresh.Skills);
            entity.WatchedAttributes.MarkPathDirty(MobKeys.Tree);
        }

        // without hotApplyAppearance live mobs keep their old gear, so a new archer may have no bow
        private void WarnIfArcherWithoutWeapon(ResolvedMob fresh)
        {
            if (fresh.Ai.archetype != MobValidator.ArcherArchetype) return;
            string code = MobGear.ReadRight(Identity);
            if (code.Length > 0 && AiTaskRpgShoot.IsRangedWeapon(entity.World.GetItem(new AssetLocation(code)))) return;
            WarnOnce("archer:" + fresh.Key,
                $"{fresh.Key} became a rangedArcher, but mobs already in the world hold no bow: they shoot again after a respawn, " +
                "or turn on hotApplyAppearance in the settings to re-dress them now");
        }

        private static readonly JsonSerializerSettings HashSettings = new() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
        // keyed by model instance, shared by all mobs of the catalog
        private static readonly ConditionalWeakTable<object, string> contentHashes = new();

        private static string ContentHash(object? model)
            => model == null ? "" : contentHashes.GetValue(model, m =>
            {
                try
                {
                    string json = JsonConvert.SerializeObject(m, HashSettings);
                    return json.Length.ToString() + ":" + json.GetHashCode().ToString();
                }
                catch (Exception)
                {
                    // unique value, always counts as changed
                    return Guid.NewGuid().ToString("N");
                }
            });

        private static string ReapplySignature(canrpgmobsModSystem mod, ResolvedMob mob, ITreeAttribute? identity)
            => ContentHash(mob) + "|" + ContentHash(mod.Catalog.Tier(identity?.GetString(MobKeys.Tier))) + "|" + ContentHash(mod.Catalog.Scaling);

        // same seed, same preset if the list didn't change
        private void RefreshAppearance(EntityAgent agent, ITreeAttribute identity, ResolvedMob mob, TierModel? tier)
        {
            var preset = MobResolver.PickPreset(mob.Appearance, identity.GetLong(MobKeys.Seed), identity.GetString(MobKeys.Preset));
            identity.SetString(MobKeys.Preset, preset?.id ?? "");
            MobSpawner.WriteAppearance(entity.Api, agent, mob.Appearance, preset, tier?.size ?? 1f);
        }

        // a faction pinned by the spawner stays
        private void SyncFaction(ITreeAttribute identity, ResolvedMob mob)
        {
            if (identity.GetBool(MobKeys.FactionOverride) || identity.GetString(MobKeys.Faction) == mob.Faction) return;
            identity.SetString(MobKeys.Faction, mob.Faction);
            myFactionOf = null;
            entity.WatchedAttributes.MarkPathDirty(MobKeys.Tree);
        }

        // archetype removed or renamed without an alias
        private ResolvedMob? ApplyMissingPolicy(canrpgmobsModSystem mod, ITreeAttribute? identity, string? key)
        {
            string label = key ?? "(no identity)";
            string policy = mod.Settings.missingArchetypePolicy;

            if (policy.StartsWith(MobsSettings.FallbackPrefix))
            {
                string fallback = IdRules.QualifyKey(policy.Substring(MobsSettings.FallbackPrefix.Length), canrpgmobsModSystem.ModId);
                var mob = mod.Catalog.Resolve(fallback);
                if (mob != null && identity != null)
                {
                    WarnOnce(label, $"mob archetype '{label}' is gone, saved mobs become '{mob.Key}'");
                    identity.SetString(MobKeys.Key, mob.Key);
                    entity.WatchedAttributes.MarkPathDirty(MobKeys.Tree);
                    return mob;
                }
                WarnOnce(label, $"mob archetype '{label}' is gone and fallback '{fallback}' does not resolve either - these mobs stay idle");
                return null;
            }

            if (policy == MobsSettings.Despawn)
            {
                WarnOnce(label, $"mob archetype '{label}' is gone, saved mobs are removed");
                // can't kill from inside Initialize
                RemoveLater(1);
                return null;
            }

            WarnOnce(label, $"mob archetype '{label}' is gone, saved mobs keep their look and stay idle");
            return null;
        }

        private void RemoveLater(int delayMs)
            => entity.World.RegisterCallback(_ => { if (entity.Alive) entity.Die(EnumDespawnReason.Removed); }, delayMs);

        // threat, enemy and the brain's movement holds
        private void DropCombat()
        {
            threat.Clear();
            SetEnemy(null);
            HoldPosition = false;
            IsFleeing = false;
        }

        private void WarnOnce(string key, string message)
        {
            if (warnedKeys.TryAdd(key, 0)) entity.Api.Logger.Warning("[canrpgmobs] " + message);
        }
    }
}
