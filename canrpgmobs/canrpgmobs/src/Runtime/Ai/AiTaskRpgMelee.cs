using canrpgclasses.Core;
using canrpgclasses.Core.Control;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Audio;
using canrpgmobs.Runtime.Behaviors;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    public class AiTaskRpgMelee : AiTaskMeleeAttack, IOffensiveAiTask
    {
        public const string Code = "rpgmobmelee";

        private EBRpgMob? mob;

        public AiTaskRpgMelee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig) { }

        private EBRpgMob? Mob => mob ??= entity.GetBehavior<EBRpgMob>();

        public override bool ShouldExecute()
            => !RpgCombatApi.IsCasting(entity) && Mob is { IsLeashing: false, IsFleeing: false } && base.ShouldExecute();

        protected override void attackTarget()
        {
            if (Mob?.Mob?.Sounds.TryGetValue(SoundKeys.Attack, out var sound) == true) MobAudio.Play(entity, sound);
            base.attackTarget();
        }

        public override bool CanSense(Entity e, double range) => (Mob?.IsEnemy(e) ?? false) && base.CanSense(e, range);

        // IsEnemy already handles creatureHostility; vanilla's passive check needs emotion states we don't have
        public override bool CanSensePlayer(EntityPlayer eplr, double range)
            => targetablePlayerMode(eplr.Player) && entity.Pos.Dimension == eplr.Pos.Dimension;
    }
}
