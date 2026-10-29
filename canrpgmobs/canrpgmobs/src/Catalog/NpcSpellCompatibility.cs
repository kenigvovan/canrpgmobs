using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using canrpgclasses.Core;
using canrpgclasses.Core.Effects;
using canrpgclasses.Core.Spells;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using EffectsHud = effectshud.src.effectshud;

namespace canrpgmobs.Catalog
{
    // some spells need a player inventory, owner or talent to work
    public sealed class NpcSpellCompatibility : ISpellInfoSource
    {
        private readonly ICoreAPI api;
        private readonly ConcurrentDictionary<string, (SpellSupport level, string[] reasons)> cache = new();

        public NpcSpellCompatibility(ICoreAPI api) => this.api = api;

        public bool Exists(string spellId) => RpgCombatApi.GetSpell(api, spellId) != null;

        public SpellSupport Rate(string spellId, out string[] reasons)
        {
            var result = cache.GetOrAdd(RpgCombatApi.NormalizeSpellId(spellId), Compute);
            reasons = result.reasons;
            return result.level;
        }

        private (SpellSupport, string[]) Compute(string spellId)
        {
            var spell = RpgCombatApi.GetSpell(api, spellId);
            if (spell == null) return (SpellSupport.Unsupported, new[] { "unknown spell" });
            var reasons = new List<string>();
            var level = Rate(spell, EffectType, reasons);
            return (level, reasons.Distinct().ToArray());
        }

        public static SpellSupport Rate(Spell spell, System.Func<string, Type?> effectType, List<string> reasons)
        {
            var level = SpellSupport.Supported;
            void Block(string why) { level = SpellSupport.Unsupported; reasons.Add(why); }
            void Degrade(string why) { if (level == SpellSupport.Supported) level = SpellSupport.Degraded; reasons.Add(why); }

            if (spell.Type != SpellType.Active) Block("not an active spell");
            if (spell.RequiresBow || spell.Deliver.Type is DeliveryType.ShootArrow or DeliveryType.AffectArrow) Block("needs a real bow shot");
            if (!string.IsNullOrEmpty(spell.RequiresForm) || spell.AuraLocksActions) Block("shapeshift form spell");
            if (!string.IsNullOrEmpty(spell.SecondaryResourceId)) Block("spends a secondary resource NPCs don't have");
            if (spell.Target.Type == TargetType.FromTrigger) Block("fires from a player trigger");

            if (spell.RequiresShield) Degrade("needs a shield in hand");
            if (spell.RequiresAttributes is { Count: > 0 }) Degrade("needs attribute points");
            if (spell.RequiresOutOfCombat || spell.OpensStealthWindowSeconds > 0f) Degrade("combat and stealth state is tracked for players only");
            if (spell.ComboBuilder || spell.ComboFinisher) Degrade("combo points never decay on NPCs");
            if (spell.Deliver.Type == DeliveryType.StashEffect) Degrade("stash stacks are spent by player swings only");

            bool onlyGrantsResource = spell.Impacts.Count > 0;
            foreach (var imp in spell.Impacts)
            {
                if (imp.Action != ImpactAction.GainResource) onlyGrantsResource = false;
                switch (imp.Action)
                {
                    case ImpactAction.Spawn:
                    case ImpactAction.PetTarget:
                    case ImpactAction.PetCommand:
                    case ImpactAction.PlaceTotem:
                        Block($"{imp.Action} needs a player owner");
                        break;
                    case ImpactAction.EmpowerNextShot:
                    case ImpactAction.CoatWeapon:
                        Block($"{imp.Action} works on a player's held item");
                        break;
                    case ImpactAction.Aggro:
                        Block("taunt pulls every nearby mob, allies included");
                        break;
                    case ImpactAction.EmpowerNextMelee:
                        Block("the empowered swing is only consumed by player attacks");
                        break;
                    case ImpactAction.ToggleAura:
                        Degrade("auras are shared with players only");
                        break;
                    case ImpactAction.Heal when imp.ChainJumps > 0:
                        Degrade("heal chains jump to players only");
                        break;
                    case ImpactAction.StatusEffect when !string.IsNullOrEmpty(imp.StatusEffectId)
                                                        && typeof(IModelSwapEffect).IsAssignableFrom(effectType(imp.StatusEffectId!)):
                        Block("model swap effect");
                        break;
                }
                if (!string.IsNullOrEmpty(imp.RequiresTalentId)) Degrade("talent-gated riders are skipped");
                if (!string.IsNullOrEmpty(imp.DamageSplashHealStat)) Degrade("splash heal scales off a talent stat");
            }
            if (onlyGrantsResource) Block("only grants a class resource");

            return level;
        }

        private Type? EffectType(string effectId)
        {
            var effects = api.ModLoader.GetModSystem<EffectsHud>()?.effects;
            return effects != null && effects.TryGetValue(effectId, out var type) ? type : null;
        }
    }
}
