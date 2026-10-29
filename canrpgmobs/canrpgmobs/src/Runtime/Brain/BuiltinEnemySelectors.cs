using System;
using canrpgmobs.Data;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Brain.Builtin
{
    [MobEnemySelector(TargetingModel.Threat, "Whoever hurt the mob most, or the nearest enemy while nobody has")]
    public sealed class ThreatEnemySelector : IEnemySelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();
        public bool StickyByThreat => true;

        public Entity? Pick(in EnemyPickContext ctx) => ctx.Mob.ThreatEnemy(ctx.KeepRange) ?? ctx.Mob.NearestEnemy(ctx.Range);
    }

    [MobEnemySelector(TargetingModel.Nearest, "The closest enemy, whoever hit the mob")]
    public sealed class NearestEnemySelector : IEnemySelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();

        public Entity? Pick(in EnemyPickContext ctx) => ctx.Mob.NearestEnemy(ctx.Range);
    }

    [MobEnemySelector(TargetingModel.LowestHp, "The enemy closest to death: finishers and executioners")]
    public sealed class LowestHpEnemySelector : IEnemySelector
    {
        public ParamInfo[] Params { get; } = Array.Empty<ParamInfo>();

        public Entity? Pick(in EnemyPickContext ctx) => ctx.Mob.WeakestEnemy(ctx.Range);
    }
}
