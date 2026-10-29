using System;
using System.IO;
using canrpgmobs.Catalog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canrpgmobs
{
    // server: translations out to a lang file
    public partial class canrpgmobsModSystem
    {
        // a ready lang file: our translation, else the lang files', else the English name to translate
        private TextCommandResult OnLangExport(TextCommandCallingArgs args)
        {
            string locale = ((string)args[0]).Trim().ToLowerInvariant();
            if (!LangTables.IsLocale(locale)) return TextCommandResult.Error($"'{locale}' is not a language code, like ru, de or pt-br.");
            var data = Catalog.Data;
            data.Lang.TryGetValue(locale, out var ours);
            var file = new JObject();
            int translated = 0;
            foreach (var (key, english) in LangTables.NameKeys(data))
            {
                string? text = locale == "en" ? english : ours != null && ours.TryGetValue(key, out var own) ? own : LangTables.Original(locale, key);
                if (text != null && locale != "en") translated++;
                file[key] = text ?? english;
            }
            string folder = Path.Combine(GamePaths.ModConfig, ExportFolder, "lang");
            string path = Path.Combine(folder, locale + ".json");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, file.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                return TextCommandResult.Error("Could not write the file: " + e.Message);
            }
            string counts = locale == "en" ? $"{file.Count} names" : $"{translated} of {file.Count} translated, the rest hold the English name";
            return TextCommandResult.Success($"Written to {path} ({counts}). Put it into a mod as assets/<your mod id>/lang/{locale}.json.");
        }
    }
}
