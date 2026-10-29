using System.Linq;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // "Create based on..." and copying a mob together with its own race and role.
    public partial class MobEditorDialog
    {
        private bool OnCreateFrom()
        {
            confirmDelete = false;
            pendingSection = null;
            if (ConfirmDiscard(ref pendingOpen, CopyMark)) return true;
            pendingOpen = null;
            string section = session.Section;
            string list = section switch
            {
                "races" => "racesNamed",
                "roles" => "rolesNamed",
                "mobs" => "mobsNamed",
                _ => section
            };
            return OpenPick(list, L("canrpgmobs:editor-copy-from-title", "Create based on"), null, id => CopyEntry(section, id));
        }

        private void CopyEntry(string section, string id)
        {
            var source = mod.Store.Get(section, id) ?? FromCatalog(section, id);
            if (source == null)
            {
                SetStatus(L("canrpgmobs:editor-not-found", "That entry no longer exists."), true);
                return;
            }
            var copy = (JObject)source.DeepClone();
            string newId = UniqueId(Q(Short(id) + "_copy"));
            copy["id"] = newId;
            MarkCopyName(copy);
            session.OpenNew(section, copy);
            page = Page.Editor;
            ResetPages();
            lootReturn = null;
            previewKey = SuggestPreviewKey();
            SetStatus(L("canrpgmobs:editor-duplicated", "Copied as {0}. Change it, then save.", newId), false);
        }

        private bool OnCopyWithRaceRole()
        {
            ClearPending();
            var d = session.Draft;
            if (d == null || session.Section != "mobs") return true;
            if (Busy()) return true;

            string domain = canrpgmobsModSystem.ModId;
            string raceId = IdRules.Qualify(ReadString(d, "race"), domain);
            string roleId = IdRules.Qualify(ReadString(d, "role"), domain);
            var race = mod.Store.Get("races", raceId) ?? FromCatalog("races", raceId);
            var role = mod.Store.Get("roles", roleId) ?? FromCatalog("roles", roleId);
            if (race == null || role == null)
            {
                SetStatus(L("canrpgmobs:editor-copy-not-found", "This mob's race or role wasn't found."), true);
                Compose();
                return true;
            }

            // server requires all bundle ids to be new, same rule as the wizard
            string root = Short(session.DraftId) + "_copy";
            string newId = root;
            for (int n = 2; WizardIdTaken(newId); n++) newId = root + n;
            string mobId = IdRules.Qualify(newId, domain);

            var raceCopy = (JObject)race.DeepClone();
            raceCopy["id"] = mobId + "_race";
            MarkCopyName(raceCopy);
            var roleCopy = (JObject)role.DeepClone();
            roleCopy["id"] = mobId + "_role";
            MarkCopyName(roleCopy);
            // includes unsaved changes
            var mobCopy = (JObject)d.DeepClone();
            mobCopy["id"] = mobId;
            mobCopy["race"] = mobId + "_race";
            mobCopy["role"] = mobId + "_role";
            MarkCopyName(mobCopy);
            var bundle = new JObject { ["races"] = new JArray(raceCopy), ["roles"] = new JArray(roleCopy), ["mobs"] = new JArray(mobCopy) };

            if (CatalogEditGuard.RejectValue(bundle) is { } refused)
            {
                SetStatus(L("canrpgmobs:editor-not-saved", "Not saved: {0}", refused), true);
                Compose();
                return true;
            }
            var candidate = mod.Store.Clone();
            candidate.Merge(bundle);
            var catalog = CatalogLoader.Load(capi, capi.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            if (!CatalogStore.IsInCatalog(catalog.Data, "mobs", mobId) || catalog.Resolve(mobId) == null)
            {
                var ids = new[] { mobId, mobId + "_race", mobId + "_role" };
                string details = string.Join("; ", report.Problems.Where(p => p.Severity == Severity.Error && ids.Any(id => Problem.PathMentions(p.Path, id)))
                    .Take(2).Select(p => p.Message));
                SetStatus(L("canrpgmobs:editor-copy-invalid", "The copy would not load: {0}", details), true);
                Compose();
                return true;
            }

            mod.SendBundle(bundle.ToString(Formatting.None));
            awaitingResult = true;
            pendingBundle = new PendingBundle(BundleKind.Copy, mobId,
                L("canrpgmobs:editor-copy-created", "Copied as {0} with its own race and role: changes won't touch the original.", Short(mobId)));
            SetStatus(L("canrpgmobs:editor-sending", "Sent, waiting for the server..."), false);
            Compose();
            return true;
        }

        // lang key names are turned into plain text first so the suffix is visible
        private static void MarkCopyName(JObject entry)
        {
            if (entry["name"] is not JObject name) return;
            if (name["langKey"] is JValue langKey)
            {
                string key = langKey.ToString();
                // the English text, unformatted: names may contain {race}/{role} which string.Format would choke on
                string text = Lang.AvailableLanguages.TryGetValue("en", out var english) && english.HasTranslation(key, false) ? english.GetUnformatted(key) : key;
                name.Remove("langKey");
                if (name["en"] == null) name["en"] = text;
            }
            // the copy has a new id, so its translations (by id) start empty and the English name shows
            if (name["en"] is JValue en) name["en"] = en + " (copy)";
        }
    }
}
