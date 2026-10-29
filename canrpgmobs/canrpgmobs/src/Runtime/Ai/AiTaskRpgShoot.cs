using System;
using canrpgclasses.Core;
using canrpgclasses.Core.Control;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Behaviors;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Vanilla projectile shot with the ranged weapon in the right hand. Doesn't run without one.
    // Phase/enrage bonuses come through the shooter's rangedWeaponsDamage stat.
    public class AiTaskRpgShoot : AiTaskShootAtEntityR, IOffensiveAiTask
    {
        public const string Code = "rpgmobshoot";

        // search does raytraces
        private const int TargetSearchMs = 750;

        // projectile is always ranged.arrow
        private static readonly EnumTool[] RangedTools =
            { EnumTool.Bow, EnumTool.Crossbow, EnumTool.Sling, EnumTool.Javelin, EnumTool.Spear };

        public static bool IsRangedWeapon(CollectibleObject? item)
            => item?.Tool is { } tool && Array.IndexOf(RangedTools, tool) >= 0;

        private readonly bool hasArrow;
        private readonly float minRange;
        private readonly float damageMul;
        private readonly bool useMeleeAdd;
        private readonly float arrowDamage;
        private readonly int arrowTier;

        private EBRpgMob? mob;
        private string weaponCode = "";
        private CollectibleObject? weapon;

        public AiTaskRpgShoot(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            var config = GetConfig<AiTaskShootAtEntityConfig>();
            string arrowCode = taskConfig["arrow"].AsString("game:arrow-flint");
            var arrow = entity.World.GetItem(new AssetLocation(arrowCode));
            config.ProjectileItem = arrow?.Code ?? new AssetLocation(arrowCode);
            config.ProjectileCode = arrow == null ? new AssetLocation("game", "arrow-flint") : ArrowEntityCode(arrow);
            // no projectile entity type = can't shoot (the catalog warns)
            hasArrow = arrow != null && entity.World.GetEntityType(config.ProjectileCode) != null;
            config.ReplaceRockVariant = false;
            // Misleading name: vanilla assigns it straight to the projectile's Collectible, so false = not collectible.
            config.NonCollectible = false;
            config.DropOnImpactChance = 0f;
            config.RetaliateUnconditionally = false;
            config.TargetSearchCooldownMs = TargetSearchMs;
            config.ProjectileDamageType = Enum.TryParse<EnumDamageType>(taskConfig["damageType"].AsString("PiercingAttack"), true, out var type)
                ? type : EnumDamageType.PiercingAttack;
            config.ProjectileSpeed = Math.Clamp(taskConfig["speed"].AsFloat(30f), 1f, 100f);
            float spread = Math.Clamp(taskConfig["dispersion"].AsFloat(2f), 0f, 45f);
            config.YawDispersionDeg = config.PitchDispersionDeg = spread;
            config.MaxYawDispersionDeg = config.MaxPitchDispersionDeg = spread * 2f;
            config.DispersionReductionSpeedDeg = spread / 2f;
            // config Init already ran in the base ctor, so add "sounds/" ourselves
            string sound = taskConfig["sound"].AsString("game:bow-release");
            config.ShootSound = sound.Length == 0 || sound == "none" ? null : new AssetLocation(sound).WithPathPrefixOnce("sounds/");

            // 1 = arrow drop, 0 = straight
            config.ProjectileGravityFactor = Math.Clamp(taskConfig["gravityFactor"].AsFloat(1f), 0f, 5f);

            minRange = Math.Max(0f, taskConfig["minRange"].AsFloat(3f));
            damageMul = Math.Max(0f, taskConfig["damageMul"].AsFloat(1f));
            useMeleeAdd = taskConfig["useMeleeAdd"].AsBool(false);
            arrowDamage = arrow?.Attributes?["damage"].AsFloat(0f) ?? 0f;
            arrowTier = arrow?.Attributes?["damageTier"].AsInt(0) ?? 0;
        }

        // same lookup as the vanilla bow
        public static AssetLocation ArrowEntityCode(CollectibleObject arrow)
            => AssetLocation.Create(arrow.Attributes?["arrowEntityCode"].AsString(null) ?? arrow.Code.Path, arrow.Code.Domain);

        private EBRpgMob? Mob => mob ??= entity.GetBehavior<EBRpgMob>();

        public override bool ShouldExecute()
            => hasArrow && Mob is { IsLeashing: false, IsFleeing: false } && !RpgCombatApi.IsCasting(entity) && base.ShouldExecute();

        public override bool ContinueExecute(float dt)
        {
            if (targetEntity is not { Alive: true } || Mob is not { IsLeashing: false, IsFleeing: false } || RpgCombatApi.IsCasting(entity)) return false;
            return base.ContinueExecute(dt);
        }

        // use the mob's enemy instead of the vanilla entity code scan
        protected override bool SearchForTarget()
        {
            targetEntity = null;
            if (Weapon() == null) return false;
            float range = GetSeekingRange();
            var enemy = Mob?.AcquireEnemy(range);
            if (enemy == null || entity.Pos.DistanceTo(enemy.Pos) < minRange) return false;
            if (!HasDirectContact(enemy, range, range * GetConfig<AiTaskShootAtEntityConfig>().VerticalRangeFactor)) return false;
            targetEntity = enemy;
            return true;
        }

        protected override void ShootProjectile()
        {
            if (Weapon() is not { } held || Mob is not { Mob: { } resolved, ScaledStats: { } stats }) return;
            var config = GetConfig<AiTaskShootAtEntityConfig>();
            float weaponDamage = held.Attributes?["damage"].AsFloat(0f) ?? 0f;
            // MeleeAdd only with useMeleeAdd; an explicit melee damage is used as is
            float damage = resolved.MeleeOverridden
                ? stats.MeleeDamage
                : (weaponDamage + arrowDamage + (useMeleeAdd ? resolved.MeleeAdd : 0f)) * resolved.MeleeMul * stats.DamageFactor;
            config.ProjectileDamage = Math.Max(0f, damage * damageMul);
            config.ProjectileDamageTier = Math.Max(held.Attributes?["damageTier"].AsInt(0) ?? 0, arrowTier);
            base.ShootProjectile();
        }

        // cached by item code
        private CollectibleObject? Weapon()
        {
            string code = MobGear.ReadRight(MobKeys.Identity(entity));
            if (code == weaponCode) return weapon;
            weaponCode = code;
            var item = code.Length == 0 ? null : entity.World.GetItem(new AssetLocation(code));
            weapon = IsRangedWeapon(item) ? item : null;
            return weapon;
        }
    }
}
