using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Data
{
    // catches typos like "maxHealt"
    public static class SchemaCheck
    {
        public static void ReportUnknownFields(JObject obj, Type type, string path, ValidationReport report)
        {
            foreach (var prop in obj.Properties())
            {
                if (prop.Name.StartsWith("_")) continue; // "_comment" notes

                var info = type.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (info == null || info.IsDefined(typeof(JsonIgnoreAttribute)))
                {
                    report.Warning(path, $"unknown field '{prop.Name}' is ignored");
                    continue;
                }

                var fieldType = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
                string child = path + "." + prop.Name;
                if (prop.Value is JObject nested && IsModel(fieldType))
                {
                    ReportUnknownFields(nested, fieldType, child, report);
                }
                else if (prop.Value is JArray array && ListItemType(fieldType) is { } itemType && IsModel(itemType))
                {
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is JObject element)
                            ReportUnknownFields(element, itemType, $"{child}[{IdOf(element) ?? i.ToString()}]", report);
                }
            }
        }

        private static bool IsModel(Type t)
            => t.IsClass && t.Namespace == typeof(SchemaCheck).Namespace && !typeof(IEnumerable).IsAssignableFrom(t);

        private static Type? ListItemType(Type t)
            => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;

        private static string? IdOf(JObject o) => o["id"]?.Type == JTokenType.String ? o["id"]!.Value<string>() : null;
    }
}
