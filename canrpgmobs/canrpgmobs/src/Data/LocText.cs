using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    // {"langKey": "canrpgmobs:race-goblin"} and/or inline text per locale {"en": "Goblin", "ru": "Гоблин"}.
    // In-game content keeps text inline since lang files can't be sent to clients.
    public class LocText : Dictionary<string, string>
    {
        public const string LangKey = "langKey";

        // null in tests, then only inline text is used
        public static Func<string, string?>? Translator { get; set; }

        public LocText() : base(StringComparer.OrdinalIgnoreCase) { }

        public string? Key => TryGetValue(LangKey, out var key) && !string.IsNullOrWhiteSpace(key) ? key : null;

        // set by the loader from the entry id (canrpgmobs:mob-bone_lord), never saved: a dictionary's properties don't serialize
        public string? AutoKey { get; set; }

        public IEnumerable<KeyValuePair<string, string>> Texts => this.Where(p => !IsKeyEntry(p.Key));

        // lang key, then locale text, then the id's lang key, then English, then anything, then fallback
        public string Resolve(string? locale, string fallback)
        {
            if (Key is { } key && Translator?.Invoke(key) is { Length: > 0 } translated) return translated;
            if (locale != null && !IsKeyEntry(locale) && TryGetValue(locale, out var text) && !string.IsNullOrEmpty(text)) return text;
            if (AutoKey != null && Translator?.Invoke(AutoKey) is { Length: > 0 } auto) return auto;
            if (TryGetValue("en", out text) && !string.IsNullOrEmpty(text)) return text;
            foreach (var (entry, value) in this)
                if (!IsKeyEntry(entry) && !string.IsNullOrEmpty(value)) return value;
            return fallback;
        }

        private static bool IsKeyEntry(string entry) => string.Equals(entry, LangKey, StringComparison.OrdinalIgnoreCase);
    }
}
