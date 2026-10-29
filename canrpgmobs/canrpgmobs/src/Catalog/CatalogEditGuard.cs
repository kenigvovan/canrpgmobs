using System.Linq;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Catalog
{
    // Size/sanity limits against bad clients. Content rules are left to the validator.
    public static class CatalogEditGuard
    {
        public const int MaxJsonChars = 64 * 1024;
        public const int MaxDepth = 24;
        public const int MaxStringLength = 4000;

        public static string? RejectSection(string? section)
            => section != null && CatalogStore.Sections.Contains(section)
                ? null
                : $"unknown section '{section}' (known: {string.Join(", ", CatalogStore.Sections)})";

        public static string? RejectEntry(JToken token)
        {
            if (token is not JObject entry) return "the entry must be a JSON object";
            if (entry["id"]?.Type != JTokenType.String || !IdRules.IsClean((string)entry["id"]!))
                return "the entry needs an id made of letters, digits and : _ - .";
            return Scan(entry, 0);
        }

        // for preview looks, entry bundles etc
        public static string? RejectValue(JToken token) => Scan(token, 0);

        private static string? Scan(JToken token, int depth)
        {
            if (depth > MaxDepth) return "the entry is nested too deeply";
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        string? problem = Scan(property.Value, depth + 1);
                        if (problem != null) return problem;
                    }
                    return null;

                case JArray array:
                    foreach (var item in array)
                    {
                        string? problem = Scan(item, depth + 1);
                        if (problem != null) return problem;
                    }
                    return null;

                case JValue { Type: JTokenType.Float } number:
                    double value = number.Value<double>();
                    return double.IsNaN(value) || double.IsInfinity(value) ? $"{token.Path}: numbers must be finite" : null;

                case JValue { Type: JTokenType.String } text:
                    string s = (string)text!;
                    if (s.Length > MaxStringLength) return $"{token.Path}: text is longer than {MaxStringLength} characters";
                    if (s.Contains('—') || s.Contains('–')) return $"{token.Path}: the game font can't draw long dashes, use a spaced hyphen";
                    return null;

                default:
                    return null;
            }
        }
    }
}
