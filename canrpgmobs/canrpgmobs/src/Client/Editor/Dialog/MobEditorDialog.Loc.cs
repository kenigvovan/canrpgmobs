using canrpgmobs.Catalog;
using Newtonsoft.Json.Linq;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;

namespace canrpgmobs.Client.Editor
{
    // Names are typed in English; other languages come from lang files under the entry's key.
    public partial class MobEditorDialog
    {
        // canrpgmobs:mob-bone_lord, or the entry's own langKey
        private string? TranslationKey(JObject d)
        {
            if ((d["name"] as JObject)?[canrpgmobs.Data.LocText.LangKey] is JValue { Type: JTokenType.String } own && own.ToString().Length > 0) return own.ToString();
            string kind = session.Section switch
            {
                "mobs" => "mob", "races" => "race", "roles" => "role", "zones" => "zone", "groups" => "group", "tiers" => "tier", _ => ""
            };
            string id = ReadString(d, "id");
            return kind.Length == 0 || id.Length == 0 ? null : CatalogLoader.AutoKeyFor(kind, Q(id));
        }
    }
}
