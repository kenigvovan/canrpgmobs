using System;
using System.Collections.Generic;

namespace canrpgmobs.Data
{
    public enum ParamKind { Fraction, Blocks, Seconds, Count, Effect, Mob, Multiplier, Section }

    // Used by the validator for range checks and by the editor to build fields.
    // Effect = effectshud effect id, Mob = template id or race|role.
    public sealed record ParamInfo(string Name, ParamKind Kind, bool Required, float Min, float Max, string? PickList = null)
    {
        public static ParamInfo Fraction(string name, bool required = true) => new(name, ParamKind.Fraction, required, 0f, 1f);
        public static ParamInfo Blocks(string name, bool required = true, float max = 128f) => new(name, ParamKind.Blocks, required, 0f, max);
        public static ParamInfo Seconds(string name, bool required = true, float max = 3600f) => new(name, ParamKind.Seconds, required, 0f, max);
        public static ParamInfo Count(string name, bool required = true, float max = 100f) => new(name, ParamKind.Count, required, 0f, max);
        public static ParamInfo Effect(string name, bool required = true) => new(name, ParamKind.Effect, required, 0f, 0f, "effects");
        public static ParamInfo Mob(string name, bool required = true) => new(name, ParamKind.Mob, required, 0f, 0f, "mobKeys");
        public static ParamInfo Multiplier(string name, bool required = true, float max = 10f) => new(name, ParamKind.Multiplier, required, 0f, max);
        // a whole settings block like melee or targeting, help text only
        public static ParamInfo Section(string name) => new(name, ParamKind.Section, false, 0f, 0f);
    }

    public sealed record VocabularyEntry(string Id, string Help, IReadOnlyList<ParamInfo> Params);

    // everything registered at runtime, passed in so the data layer has no game types
    public sealed class RuleVocabulary
    {
        public RuleVocabulary(IEnumerable<VocabularyEntry> conditions, IEnumerable<VocabularyEntry> selectors, IEnumerable<VocabularyEntry> archetypes,
            IEnumerable<VocabularyEntry>? actions = null, IEnumerable<VocabularyEntry>? enemySelectors = null)
        {
            Conditions = ToMap(conditions);
            Selectors = ToMap(selectors);
            Archetypes = ToMap(archetypes);
            Actions = ToMap(actions ?? Array.Empty<VocabularyEntry>());
            EnemySelectors = ToMap(enemySelectors ?? Array.Empty<VocabularyEntry>());
        }

        public IReadOnlyDictionary<string, VocabularyEntry> Conditions { get; }
        // rule.target
        public IReadOnlyDictionary<string, VocabularyEntry> Selectors { get; }
        public IReadOnlyDictionary<string, VocabularyEntry> Archetypes { get; }
        public IReadOnlyDictionary<string, VocabularyEntry> Actions { get; }
        // aiProfile.targeting.selector
        public IReadOnlyDictionary<string, VocabularyEntry> EnemySelectors { get; }

        private static Dictionary<string, VocabularyEntry> ToMap(IEnumerable<VocabularyEntry> entries)
        {
            var map = new Dictionary<string, VocabularyEntry>(StringComparer.Ordinal);
            foreach (var entry in entries) map[entry.Id] = entry;
            return map;
        }
    }
}
