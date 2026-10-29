using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public static partial class MobValidator
    {
        private static void CheckSetRefs(string? animSet, string? soundSet, string path, Scope scope)
        {
            var data = scope.Data;
            if (!string.IsNullOrEmpty(animSet) && !data.AnimSets.ContainsKey(MobResolver.ResolveAlias(animSet!, data.Aliases)))
                scope.Report.Warning(path, $"unknown animSet '{animSet}', skill and AI animation keys play as written");
            if (!string.IsNullOrEmpty(soundSet) && !data.SoundSets.ContainsKey(MobResolver.ResolveAlias(soundSet!, data.Aliases)))
                scope.Report.Warning(path, $"unknown soundSet '{soundSet}', the body's default sounds play");
        }

        private static void CheckAnimSet(AnimSetModel set, CatalogData data, ValidationReport report)
        {
            string path = Path(set.source, "animSet", set.id);
            CheckExtends(set.id, set.extends, data.AnimSets, s => s.extends, data.Aliases, "animation set", path, report);
            CheckAnimEntries(set.anims, path, report);
        }

        private static void CheckAnimEntries(Dictionary<string, AnimEntryModel>? anims, string path, ValidationReport report)
        {
            if (anims == null) return;
            foreach (var (key, entry) in anims)
            {
                if (entry == null) continue;
                string at = $"{path} > anim {key}";
                if (!AnimKeys.All.Contains(key)) report.Info(at, "custom key, used only where AI task configs or skill rules name it");
                if (string.IsNullOrWhiteSpace(entry.animation)) report.Warning(at, "animation is empty, nothing plays");
                if (!InRange(entry.speed, 0.01f, 20f)) report.Warning(at, "speed should be between 0.01 and 20");
                if (entry.blendMode != null && !BlendModes.Contains(entry.blendMode)) report.Warning(at, $"blendMode '{entry.blendMode}' is not Average, Add or AddAverage");
                if (entry.weight is { } weight && !NonNegative(weight)) report.Warning(at, "weight must be 0 or more");
                if (entry.onControls != null)
                {
                    foreach (var control in entry.onControls)
                        if (!EntityActivities.Contains(control, StringComparer.OrdinalIgnoreCase))
                            report.Warning(at, $"onControls '{control}' is not one of: {string.Join(", ", EntityActivities)} - ignored");
                }
            }
        }

        private static void CheckSoundSet(SoundSetModel set, CatalogData data, ValidationReport report)
        {
            string path = Path(set.source, "soundSet", set.id);
            CheckExtends(set.id, set.extends, data.SoundSets, s => s.extends, data.Aliases, "sound set", path, report);
            if (!InRange(set.idleChance, 0f, 100f)) report.Warning(path, "idleChance is a percent per tick, between 0 and 100");
            CheckSoundEntries(set.sounds, path, report);
        }

        private static void CheckSoundEntries(Dictionary<string, SoundEntryModel>? sounds, string path, ValidationReport report)
        {
            if (sounds == null) return;
            foreach (var (key, entry) in sounds)
            {
                if (entry == null) continue;
                string at = $"{path} > sound {key}";
                if (!SoundKeys.All.Contains(key)) report.Info(at, "custom key, used only where skill rules name it");
                if (string.IsNullOrWhiteSpace(entry.path)) report.Warning(at, "path is empty, nothing plays");
                if (!InRange(entry.chance, 0f, 1f)) report.Warning(at, "chance must be between 0 and 1");
                if (!InRange(entry.range, 1f, 128f)) report.Warning(at, "range should be between 1 and 128");
                if (!InRange(entry.volume, 0f, 4f)) report.Warning(at, "volume should be between 0 and 4");
                if (!InRange(entry.pitchVariance, 0f, 1f)) report.Warning(at, "pitchVariance should be between 0 and 1");
            }
        }

        private static void CheckExtends<T>(string id, string? extends, IReadOnlyDictionary<string, T> sets, Func<T, string?> parentOf,
            IReadOnlyDictionary<string, string> aliases, string kind, string path, ValidationReport report)
        {
            if (!string.IsNullOrEmpty(extends) && !sets.ContainsKey(MobResolver.ResolveAlias(extends!, aliases)))
                report.Warning(path, $"extends unknown {kind} '{extends}'");
            else if (ExtendsLoops(id, current => sets.TryGetValue(current, out var s) && parentOf(s) is { } parent ? MobResolver.ResolveAlias(parent, aliases) : null))
                report.Warning(path, "the extends chain loops, sets after the loop are ignored");
        }

        private static bool ExtendsLoops(string start, Func<string, string?> next)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            for (string? id = next(start); id != null; id = next(id))
                if (!seen.Add(id)) return true;
            return false;
        }
    }
}
