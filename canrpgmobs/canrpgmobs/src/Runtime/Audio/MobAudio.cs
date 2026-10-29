using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Audio
{
    // Hurt/death/idle go into the entity sound list (played by the game), the rest is played here.
    // Trailing * in a path picks a random variant.
    public static class MobAudio
    {
        private static readonly ConcurrentDictionary<string, AssetLocation[]> Variants = new();

        public static void ApplyEntitySounds(Entity entity, ResolvedMob mob)
        {
            var properties = entity.Properties;
            bool any = false;
            foreach (var key in SoundKeys.EntityKeys)
            {
                if (!mob.Sounds.TryGetValue(key, out var entry) || string.IsNullOrWhiteSpace(entry.path)) continue;
                properties.Sounds ??= new Dictionary<string, SoundAttributes>();
                properties.Sounds[key] = new SoundAttributes(Location(entry.path), false)
                {
                    Range = entry.range,
                    Volume = NatFloat.createUniform(entry.volume, 0f),
                    Pitch = NatFloat.createUniform(1f, entry.pitchVariance)
                };
                any = true;
            }
            if (!any) return;

            properties.ResolvedSounds = new Dictionary<string, AssetLocation[]>();
            properties.InitSounds(entity.Api.Assets);
            if (mob.Sounds.ContainsKey(SoundKeys.Idle)) properties.IdleSoundChance = mob.IdleSoundChance;
        }

        // server, synced to clients
        public static void Play(Entity entity, SoundEntryModel? entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.path)) return;
            if (entry.chance < 1f && entity.World.Rand.NextDouble() >= entry.chance) return;
            PlayPath(entity, entry.path, entry.range, entry.volume, entry.pitchVariance > 0f);
        }

        public static void PlayPath(Entity entity, string path, float range, float volume = 1f, bool randomizePitch = true)
        {
            var location = Pick(entity, Location(path));
            if (location == null) return;
            entity.World.PlaySoundAt(location, entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z, null, randomizePitch, range, volume);
        }

        public static AssetLocation Location(string path) => AssetLocation.Create(path, "game").WithPathPrefixOnce("sounds/");

        // PlaySoundAt takes a single file, so resolve wildcards ourselves (cached)
        private static AssetLocation? Pick(Entity entity, AssetLocation location)
        {
            if (!location.Path.EndsWith("*")) return location;
            var options = Variants.GetOrAdd(location.ToString(), _ =>
                entity.Api.Assets.GetMany(location.Path.TrimEnd('*'), location.Domain, false).Select(a => a.Location).ToArray());
            return options.Length == 0 ? null : options[entity.World.Rand.Next(options.Length)];
        }
    }
}
