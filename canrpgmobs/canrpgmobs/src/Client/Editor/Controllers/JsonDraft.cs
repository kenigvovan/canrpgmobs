using System;
using System.Globalization;
using System.Linq;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Client.Editor.Controllers
{
    // Raw JSON helpers for drafts. Dialog files use "using static" on this.
    public static class JsonDraft
    {
        // with our domain, as the catalog stores it
        public static string Q(string id) => IdRules.Qualify(id, canrpgmobsModSystem.ModId);

        // without our domain, as shown in the UI
        public static string Short(string id)
        {
            string qualified = Q(id);
            return qualified.StartsWith(canrpgmobsModSystem.ModId + ":", StringComparison.Ordinal)
                ? qualified.Substring(canrpgmobsModSystem.ModId.Length + 1) : qualified;
        }

        // both halves of race|role
        public static string ShortKey(string key)
        {
            int sep = key.IndexOf(IdRules.KeySeparator);
            return sep < 0 ? Short(key) : Short(key.Substring(0, sep)) + IdRules.KeySeparator + Short(key.Substring(sep + 1));
        }

        public static bool SameId(string a, string b) => Q(a) == Q(b);

        // for number inputs
        public static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static JArray EnsureArray(JObject token, string key)
        {
            if (token[key] is JArray array) return array;
            var fresh = new JArray();
            token[key] = fresh;
            return fresh;
        }

        public static JObject EnsureObject(JObject token, string key)
        {
            if (token[key] is JObject obj) return obj;
            var fresh = new JObject();
            token[key] = fresh;
            return fresh;
        }

        public static void RemoveIfEmpty(JObject token, string key)
        {
            if (token[key] is JArray { Count: 0 } or JObject { Count: 0 }) token.Remove(key);
        }

        public static string ReadString(JObject token, string key) => token[key] is JValue value && value.Value != null ? value.ToString() : "";

        public static string ReadNumber(JToken? token)
            => token is JValue { Type: JTokenType.Integer or JTokenType.Float } value
                ? Convert.ToDouble(value.Value, CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture)
                : "";

        // for reading, not for inputs: fewer decimals the bigger the number
        public static string Display(double value)
        {
            double abs = Math.Abs(value);
            string format = abs >= 100 ? "0" : abs >= 10 ? "0.#" : abs >= 1 ? "0.##" : "0.###";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        public static float ReadFloat(JObject token, string key, float fallback)
            => token[key] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? Convert.ToSingle(value.Value, CultureInfo.InvariantCulture) : fallback;

        public static bool TryParse(string text, out float value)
            => float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        // empty removes the key so the default applies; keepEmpty is for the id
        public static void SetString(JObject token, string key, string value, bool keepEmpty)
        {
            value = value.Trim();
            if (value.Length == 0 && !keepEmpty) token.Remove(key);
            else token[key] = value;
        }

        public static void SetNumber(JObject token, string key, string text, FieldSpec field)
        {
            if (text.Trim().Length == 0)
            {
                token.Remove(key);
                return;
            }
            if (!TryParse(text, out float value)) return;
            value = Math.Clamp(value, field.Min, field.Max);
            if (field.Kind == FieldKind.Int || field.Kind == FieldKind.IntRangePair) token[key] = (long)Math.Round(value);
            else token[key] = value;
        }

        public static void SetRange(JObject token, string key, string[] parts, FieldSpec field)
        {
            if (parts[0].Trim().Length == 0 && parts[1].Trim().Length == 0)
            {
                token.Remove(key);
                return;
            }
            if (!TryParse(parts[0], out float a) || !TryParse(parts[1], out float b)) return;
            a = Math.Clamp(a, field.Min, field.Max);
            b = Math.Clamp(b, field.Min, field.Max);
            token[key] = field.Kind == FieldKind.IntRangePair ? new JArray((long)Math.Round(a), (long)Math.Round(b)) : new JArray(a, b);
        }

        public static void SetList(JObject token, string key, string text)
        {
            var items = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (items.Length == 0) token.Remove(key);
            else token[key] = new JArray(items.Cast<object>().ToArray());
        }

        public static void SetLocText(JObject token, string key, string part, string value)
        {
            if (token[key] is not JObject text)
            {
                if (value.Trim().Length == 0) return;
                token[key] = text = new JObject();
            }
            if (value.Trim().Length == 0) text.Remove(part);
            else text[part] = value.Trim();
            if (text.Count == 0) token.Remove(key);
        }
    }
}
