using System;
using System.Diagnostics;
using canrpgmobs.Runtime.Appearance;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Entities
{
    // Shared entity class for all mobs; the identity attributes define the actual mob.
    public class EntityRpgMob : EntityAgent
    {
        public const string ClassName = "canrpgmobs.EntityRpgMob";

        private EBMobGear? gear;
        private bool gearLooked;

        // Client only, for rendering. Server hands stay empty so nothing drops.
        public override ItemSlot RightHandItemSlot
        {
            get => Api?.Side == EnumAppSide.Client && Gear?.RightHand is { } slot ? slot : base.RightHandItemSlot;
            set => base.RightHandItemSlot = value;
        }

        public override ItemSlot LeftHandItemSlot
        {
            get => Api?.Side == EnumAppSide.Client && Gear?.LeftHand is { } slot ? slot : base.LeftHandItemSlot;
            set => base.LeftHandItemSlot = value;
        }

        // cache null too, it's asked every frame
        private EBMobGear? Gear
        {
            get
            {
                if (!gearLooked && SidedProperties != null)
                {
                    gear = GetBehavior<EBMobGear>();
                    gearLooked = true;
                }
                return gear;
            }
        }

        public override string GetName() => DisplayName(Lang.CurrentLocale);

        // damage log and death message; vanilla would show "a wild animal" for our shared entity codes
        public override string GetPrefixAndCreatureName(string languageCode = null!)
            => DisplayName(languageCode ?? Lang.CurrentLocale);

        private string DisplayName(string locale)
        {
            var tree = MobKeys.Identity(this);
            string name = ResolveName(tree, locale);
            return tree?.GetBool(MobKeys.Preview) == true ? "[Preview] " + name : name;
        }

        // timing for /canrpgmobs debug perf
        public override void OnGameTick(float dt)
        {
            if (Api.Side != EnumAppSide.Server)
            {
                base.OnGameTick(dt);
                return;
            }
            long started = Stopwatch.GetTimestamp();
            base.OnGameTick(dt);
            canrpgmobs.Debug.MobPerf.AddEntityTick(Stopwatch.GetTimestamp() - started);
        }

        // loot comes from loot tables
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer) => Array.Empty<ItemStack>();

        // per player locale where available
        private string ResolveName(ITreeAttribute? tree, string locale)
        {
            string? key = tree?.GetString(MobKeys.Key);
            if (key != null && canrpgmobsModSystem.For(Api)?.Catalog is { } catalog && catalog.Resolve(key) is { } mob)
            {
                string name = mob.DisplayName(locale);
                return catalog.Tier(tree!.GetString(MobKeys.Tier))?.FormatName(name, tree.GetInt(MobKeys.Level, 1), locale) ?? name;
            }
            return tree?.GetString(MobKeys.FallbackName) ?? base.GetName();
        }
    }
}
