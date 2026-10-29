using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Appearance
{
    // Maps logical anim keys ("attack") to each body's own shape animations.
    public static class MobAnimations
    {
        // adds or replaces by code, other entries stay
        public static void Apply(Entity entity, IReadOnlyDictionary<string, AnimEntryModel> anims)
        {
            var client = entity.Properties.Client;
            if (client == null || anims.Count == 0) return;

            var list = new List<AnimationMetaData>(client.Animations ?? Array.Empty<AnimationMetaData>());
            foreach (var pair in anims)
            {
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.animation)) continue;
                var meta = Build(pair.Key, pair.Value);
                int existing = list.FindIndex(a => string.Equals(a.Code, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) list[existing] = meta;
                else list.Add(meta);
            }

            client.Animations = list.ToArray();
            client.AnimationsByMetaCode.Clear();
            client.AnimationsByCrc32.Clear();
            // null world: only rebuild lookups, keep the loaded shape
            client.Init(entity.Code, null);
        }

        // entity's own meta if known, else a plain one
        public static AnimationMetaData Meta(Entity entity, string code)
            => entity.Properties.Client?.Animations?.FirstOrDefault(a => a.Code == code)
               ?? new AnimationMetaData { Code = code, Animation = code, AnimationSpeed = 1f }.Init();

        public static AnimationMetaData Build(string code, AnimEntryModel entry)
        {
            var meta = new AnimationMetaData
            {
                Code = code,
                Animation = entry.animation,
                AnimationSpeed = entry.speed > 0f ? entry.speed : 1f
            };
            if (entry.weight is { } weight) meta.Weight = weight;
            if (entry.easeIn is { } easeIn) meta.EaseInSpeed = easeIn;
            if (entry.easeOut is { } easeOut) meta.EaseOutSpeed = easeOut;
            if (entry.blendMode != null && Enum.TryParse<EnumAnimationBlendMode>(entry.blendMode, true, out var blend)) meta.BlendMode = blend;

            var controls = (entry.onControls ?? Array.Empty<string>())
                .Select(c => Enum.TryParse<EnumEntityActivity>(c, true, out var activity) ? activity : EnumEntityActivity.None)
                .Where(a => a != EnumEntityActivity.None)
                .ToArray();
            if (controls.Length > 0 || entry.defaultAnim)
                meta.TriggeredBy = new AnimationTrigger { OnControls = controls.Length > 0 ? controls : null, DefaultAnim = entry.defaultAnim };

            return meta.Init();
        }
    }
}
