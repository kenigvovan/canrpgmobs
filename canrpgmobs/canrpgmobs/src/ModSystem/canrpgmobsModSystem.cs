using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using canrpgclasses;
using canrpgclasses.Core;
using canrpgclasses.Core.Control;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Ai;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Factions;
using canrpgmobs.Runtime.Scaling;
using canrpgmobs.Runtime.Spawning;
using canrpgmobs.Runtime.Tracking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs
{
    public partial class canrpgmobsModSystem : ModSystem
    {
        public const string ModId = "canrpgmobs";
        public const string SettingsFile = "canrpgmobs-settings.json";
        // min canrpgclasses ExtensionApiLevel we need
        public const int RequiredCoreApiLevel = 5;

        // one per side, singleplayer runs both in one process
        public static canrpgmobsModSystem? ClientInstance { get; private set; }
        public static canrpgmobsModSystem? ServerInstance { get; private set; }

        public static canrpgmobsModSystem? For(ICoreAPI? api)
            => api == null ? null : api.Side == EnumAppSide.Server ? ServerInstance : ClientInstance;

        public ICoreAPI Api { get; private set; } = null!;
        public MobCatalog Catalog { get; private set; } = MobCatalog.Empty;
        public ValidationReport LastReport { get; private set; } = new();
        public MobsSettings Settings { get; private set; } = new();
        // in-game content; on the client it's the server's copy
        public CatalogStore Store { get; private set; } = CatalogStore.Empty();
        public int CatalogVersion { get; private set; }
        public MobTracker Tracker { get; } = new();

        private long reapplyTickId;
        private bool coreCompatible;
        // keep the same delegate instances for unregister
        private readonly System.Func<Entity, Entity, bool?> allyResolver = FactionService.ResolveAlly;
        private readonly System.Func<Entity, ControlType, float, Entity?, float> controlModifier = MobControlHooks.Modify;
        private readonly List<(double X, double Y, double Z, int Dimension)> playerSpots = new();
        private long playerSpotsAtMs = long.MinValue / 2;

        // after canrpgclasses (0.1), the catalog needs its spell registry
        public override double ExecuteOrder() => 0.5;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            if (api.Side == EnumAppSide.Server) ServerInstance = this; else ClientInstance = this;
            Api = api;
            // quiet lookup, missing keys fall back to inline text. Unformatted because our {race}/{name}/... placeholders break String.Format
            LocText.Translator ??= key => Lang.HasTranslation(key, false, false) ? Lang.GetUnformatted(key) : null;

            int coreLevel = CoreApiLevel();
            coreCompatible = coreLevel >= RequiredCoreApiLevel;
            if (!coreCompatible)
            {
                Mod.Logger.Error("[canrpgmobs] canrpgclasses add-on API level {0} is older than the required {1} - update canrpgclasses. Mobs are disabled.",
                    coreLevel, RequiredCoreApiLevel);
                return;
            }

            api.RegisterEntity(EntityRpgMob.ClassName, typeof(EntityRpgMob));
            api.RegisterEntityBehaviorClass(EBRpgMob.Name, typeof(EBRpgMob));
            api.RegisterEntityBehaviorClass(NpcSkinBehavior.Name, typeof(NpcSkinBehavior));
            api.RegisterEntityBehaviorClass(EBMobAppearance.Name, typeof(EBMobAppearance));
            api.RegisterEntityBehaviorClass(EBMobGear.Name, typeof(EBMobGear));
            api.RegisterBlockClass(BlockMobSpawner.ClassName, typeof(BlockMobSpawner));
            api.RegisterBlockEntityClass(BEMobSpawner.ClassName, typeof(BEMobSpawner));
            api.RegisterItemClass(Client.Zones.ItemZoneWand.ClassName, typeof(Client.Zones.ItemZoneWand));
            AiTaskRegistry.Register<AiTaskRpgSeek>(AiTaskRpgSeek.Code);
            AiTaskRegistry.Register<AiTaskRpgMelee>(AiTaskRpgMelee.Code);
            AiTaskRegistry.Register<AiTaskRpgCast>(AiTaskRpgCast.Code);
            AiTaskRegistry.Register<AiTaskRpgKite>(AiTaskRpgKite.Code);
            AiTaskRegistry.Register<AiTaskRpgFlee>(AiTaskRpgFlee.Code);
            AiTaskRegistry.Register<AiTaskRpgSupportFollow>(AiTaskRpgSupportFollow.Code);
            AiTaskRegistry.Register<AiTaskRpgShoot>(AiTaskRpgShoot.Code);
            MobsApi.Scan(api, Mod.Logger);
            RegisterWithCore(api);

            Mod.Logger.Notification("[canrpgmobs] loaded ({0}) coreApiLevel={1}", api.Side, coreLevel);
        }

        // server loads the catalog later in StartServerSide, after the core has registered in-game spells
        public override void AssetsLoaded(ICoreAPI api)
        {
            if (!coreCompatible) return;
            // add-ons register in Start, no more after this
            MobsApi.Freeze();
            if (api.Side == EnumAppSide.Client) ReloadCatalog(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (!coreCompatible) return;
            LoadSettings(api);
            Store = CatalogStore.LoadServer(Mod.Logger);
            ReloadCatalog(api);
            MobDamageHooks.Register();
            RegisterCommands(api);
            StartServerNetwork(api);
            reapplyTickId = api.Event.RegisterGameTickListener(_ => Tracker.Tick(this, Settings.reapplyPerTick), 50);
            StartZoneSpawning(api);
        }

        // assetChecks=false after a server sync, the server already checked shapes and lang keys
        public void ReloadCatalog(ICoreAPI api, bool assetChecks = true)
        {
            var catalog = CatalogLoader.Load(api, Mod.Logger, MobsApi.Vocabulary, Store, out var report, assetChecks: assetChecks);
            SwapCatalog(catalog, report);
        }

        // living non-spectator players, cached for 1s for all spawners (AllOnlinePlayers copies on every read)
        public IReadOnlyList<(double X, double Y, double Z, int Dimension)> PlayerSpots(ICoreServerAPI api)
        {
            long now = api.World.ElapsedMilliseconds;
            if (now - playerSpotsAtMs < 1000 && now >= playerSpotsAtMs) return playerSpots;
            playerSpotsAtMs = now;
            playerSpots.Clear();
            foreach (var player in api.World.AllOnlinePlayers)
            {
                var entity = player.Entity;
                if (entity == null || !entity.Alive || player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;
                playerSpots.Add((entity.Pos.X, entity.Pos.Y, entity.Pos.Z, entity.Pos.Dimension));
            }
            return playerSpots;
        }

        private void SwapCatalog(MobCatalog catalog, ValidationReport report)
        {
            Catalog = catalog;
            LastReport = report;
            CatalogVersion++;
            LangTables.Apply(catalog.Data.Lang, Mod.Logger);
            if (Api.Side == EnumAppSide.Server) Tracker.QueueAll();
            else ReapplyClientMobs();
            MobsApi.RaiseCatalogReloaded(new CatalogReloadedEvent(Api.Side, CatalogVersion, report), Mod.Logger);
        }

        // names read the catalog live, only anim/sound sets need a reapply
        private void ReapplyClientMobs()
        {
            if (Api is not ICoreClientAPI capi || capi.World?.LoadedEntities == null) return;
            foreach (var entity in capi.World.LoadedEntities.Values)
                if (entity is EntityRpgMob) entity.GetBehavior<EBMobAppearance>()?.ReapplySets();
        }

        // never overwrite a broken settings file, just run on defaults
        private void LoadSettings(ICoreServerAPI api)
        {
            try
            {
                var loaded = api.LoadModConfig<MobsSettings>(SettingsFile);
                if (loaded == null) api.StoreModConfig(Settings, SettingsFile);
                else Settings = loaded;
            }
            catch (Exception e)
            {
                Mod.Logger.Error("[canrpgmobs] {0} could not be read, using defaults (file left untouched): {1}", SettingsFile, e.Message);
            }

            Settings.brainEvalMs = Math.Clamp(Settings.brainEvalMs, 50, 5000);
            Settings.reapplyPerTick = Math.Clamp(Settings.reapplyPerTick, 1, 500);
            Settings.maxPreviewSeconds = Math.Clamp(Settings.maxPreviewSeconds, 5, 3600);
            if (Settings.mobSimulationRange != 0) Settings.mobSimulationRange = Math.Clamp(Settings.mobSimulationRange, 32, 512);
            if (!MobsSettings.IsValidPolicy(Settings.missingArchetypePolicy))
            {
                Mod.Logger.Warning("[canrpgmobs] {0}: missingArchetypePolicy '{1}' is not keepSnapshot, despawn or fallback:race|role - keepSnapshot is used",
                    SettingsFile, Settings.missingArchetypePolicy);
                Settings.missingArchetypePolicy = MobsSettings.KeepSnapshot;
            }
        }

        // NoInlining: with an old core DLL these members are missing and JIT of Start would fail before the level check logs it
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterWithCore(ICoreAPI api)
        {
            RpgCombatApi.RegisterAllyResolver(api, allyResolver);
            RpgCombatApi.RegisterControlDurationModifier(api, controlModifier);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UnregisterFromCore(ICoreAPI api)
        {
            RpgCombatApi.UnregisterAllyResolver(api, allyResolver);
            RpgCombatApi.UnregisterControlDurationModifier(api, controlModifier);
        }

        public override void Dispose()
        {
            if (coreCompatible && Api != null) UnregisterFromCore(Api);
            if (Api is ICoreServerAPI server)
            {
                server.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
                if (reapplyTickId != 0) server.Event.UnregisterGameTickListener(reapplyTickId);
                StopZoneSpawning(server);
            }
            if (ServerInstance == this)
            {
                BrainTrace.Clear();
                ServerInstance = null;
            }
            // don't let the dialog keep this instance alive after leaving the world
            editor?.TryClose();
            editor?.Dispose();
            editor = null;
            DisposeZoneTools();
            if (ClientInstance == this)
            {
                Client.Editor.Controllers.ItemCatalog.Clear();
                ClientInstance = null;
            }
            base.Dispose();
        }

        // reflection, not the const: a const gets baked in at compile time
        private static int CoreApiLevel()
            => typeof(canrpgclassesModSystem).GetField("ExtensionApiLevel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is int level ? level : 0;
    }
}
