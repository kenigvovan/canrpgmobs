using System;
using System.Linq;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    public partial class MobEditorDialog
    {
        // entry is closed only after the server confirms
        private string? pendingDelete;

        private bool OnValidate()
        {
            ClearPending();
            ValidateDraft();
            if (session.Problems.Count > 0) ShowProblems();
            Compose();
            return true;
        }

        // loads the catalog as the server would, keeps only problems about this entry
        private bool ValidateDraft()
        {
            var draft = session.Draft;
            if (draft == null) return false;
            if (CatalogEditGuard.RejectEntry(draft) is { } refused)
            {
                session.Problems.Clear();
                SetStatus(L("canrpgmobs:editor-not-saved", "Not saved: {0}", refused), true);
                return false;
            }

            string id = Q(session.DraftId);
            // don't let new/copied/renamed entries overwrite an existing id
            bool openedAsThis = session.OpenedId != null && Q(session.OpenedId) == id;
            if (!openedAsThis && EntryIds(session.Section).Contains(id))
            {
                session.Problems.Clear();
                SetStatus(L("canrpgmobs:editor-id-taken", "{0} already exists: pick another id or open that entry.", id), true);
                return false;
            }
            var candidate = mod.Store.Clone();
            if (session.RenamedFrom is { } old) candidate.Remove(session.Section, old);
            var entry = (JObject)draft.DeepClone();
            entry["id"] = id;
            candidate.Replace(session.Section, entry);

            var catalog = CatalogLoader.Load(capi, capi.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            session.Problems.Clear();
            session.Problems.AddRange(report.Problems.Where(p => p.Severity != Severity.Info && Problem.PathMentions(p.Path, id)));
            problemPage = 0;

            bool kept = CatalogStore.IsInCatalog(catalog.Data, session.Section, id);
            if (!kept) SetStatus(L("canrpgmobs:editor-invalid", "Has errors and would not be saved, see Problems."), true);
            else if (session.Problems.Count > 0) SetStatus(L("canrpgmobs:editor-valid-warnings", "Valid, {0} warning(s).", session.Problems.Count), true);
            else SetStatus(L("canrpgmobs:editor-valid", "No problems found."), false);
            return kept;
        }

        private bool OnSave()
        {
            ClearPending();
            if (session.Draft == null) return true;
            if (Busy()) return true;
            if (!ValidateDraft())
            {
                if (session.Problems.Count > 0) ShowProblems();
                Compose();
                return true;
            }
            mod.SendEdit(session.Section, session.DraftId, session.RenamedFrom, session.Draft.ToString(Formatting.None));
            awaitingResult = true;
            SetStatus(L("canrpgmobs:editor-sending", "Sent, waiting for the server..."), false);
            Compose();
            return true;
        }

        private bool OnDuplicate()
        {
            ClearPending();
            if (session.Draft == null) return true;
            string id = UniqueId(Q(session.DraftId) + "_copy");
            session.Duplicate(id);
            previewKey = SuggestPreviewKey();
            ResetPages();
            lootReturn = null;
            SetStatus(L("canrpgmobs:editor-duplicated", "Copied as {0}. Change it, then save.", id), false);
            Compose();
            return true;
        }

        // one request at a time, all replies use the same packet and can't be told apart
        private bool Busy()
        {
            if (!awaitingResult) return false;
            SetStatus(L("canrpgmobs:editor-wait", "Wait for the server's answer to the last save first."), true);
            Compose();
            return true;
        }

        private bool OnDelete()
        {
            pendingOpen = null;
            pendingSection = null;
            string? id = session.OpenedId;
            if (id == null || !mod.Store.Contains(session.Section, id))
            {
                SetStatus(L("canrpgmobs:editor-delete-modfile", "Only entries edited in game can be deleted; entries from mod files can only be changed."), true);
                Compose();
                return true;
            }
            if (!confirmDelete)
            {
                confirmDelete = true;
                Compose();
                return true;
            }
            if (Busy()) return true;
            confirmDelete = false;
            mod.SendDelete(session.Section, id);
            awaitingResult = true;
            // keep the draft open in case the server refuses
            pendingDelete = id;
            SetStatus(L("canrpgmobs:editor-sending", "Sent, waiting for the server..."), false);
            Compose();
            return true;
        }

        private bool OnPreview()
        {
            ClearPending();
            if (previewKey.Length == 0) return true;
            if (Busy()) return true;
            mod.RequestPreview(previewKey, previewLevel, previewTier);
            return true;
        }

        // "Goblin brute" or "Goblin + Archer"
        private string MobKeyName(string key)
        {
            if (key.Length == 0) return "-";
            int sep = key.IndexOf(IdRules.KeySeparator);
            return sep < 0 ? NameOf("mobs", key) : $"{NameOf("races", key.Substring(0, sep))} + {NameOf("roles", key.Substring(sep + 1))}";
        }

        private string SuggestPreviewKey()
        {
            var data = mod.Catalog.Data;
            string id = session.DraftId;
            return session.Section switch
            {
                "mobs" => id,
                "races" => $"{id}|{data.Roles.Keys.OrderBy(k => k).FirstOrDefault() ?? ""}",
                "roles" => $"{data.Races.Keys.OrderBy(k => k).FirstOrDefault() ?? ""}|{id}",
                _ => previewKey
            };
        }

        private void OnEditResult(CatalogEditResultPacket packet)
        {
            if (!IsOpened()) return;
            if (packet.Tag == TranslationsTag)
            {
                OnTranslationsSaved(packet);
                return;
            }
            if (packet.Tag.Length > 0) return;
            SetStatus(packet.Ok ? packet.Message : L("canrpgmobs:editor-not-saved", "Not saved: {0}", packet.Message), !packet.Ok);
            if (awaitingResult && pendingBundle is { } bundle)
            {
                awaitingResult = false;
                FinishWizard(packet, bundle);
            }
            else if (awaitingResult && pendingDelete != null)
            {
                awaitingResult = false;
                pendingDelete = null;
                if (packet.Ok)
                {
                    session.Close();
                    lootReturn = null;
                    ResetPages();
                }
            }
            else if (awaitingResult)
            {
                awaitingResult = false;
                if (packet.Ok && session.Draft != null)
                {
                    session.MarkSaved();
                    AdoptPages();
                }
                session.Problems.Clear();
                foreach (var line in packet.Problems ?? Array.Empty<string>()) session.Problems.Add(ParseProblem(line));
                if (!packet.Ok && session.Problems.Count > 0) ShowProblems();
                // table created from a mob: go back and add it to the mob's loot
                if (packet.Ok && lootReturn is { AddTable: true } && session.Section == "lootTables" && session.Draft != null)
                {
                    ReturnFromLoot(Q(session.DraftId));
                    return;
                }
            }
            Compose();
        }

        private void OnCatalogChanged()
        {
            if (!IsOpened()) return;
            // reload clean drafts from the server, never touch unsaved work
            if (session.Draft != null && !session.IsDirty && session.OpenedId != null)
            {
                string id = session.OpenedId;
                var current = mod.Store.Get(session.Section, id) ?? FromCatalog(session.Section, id);
                // reopening resets navigation, so only do it if the entry actually changed
                if (current != null && !session.Matches(current))
                {
                    var keepSimpleTab = simpleTab;
                    var keepTab = tab;
                    session.Open(session.Section, current, id);
                    // pickers/skill pages hold refs to the old draft
                    ResetPages();
                    simpleTab = keepSimpleTab;
                    tab = keepTab;
                }
            }
            Compose();
        }

        private static Problem ParseProblem(string line)
        {
            // "severity: path: message"; asset paths have ':' but never ": "
            var parts = line.Split(new[] { ": " }, 3, StringSplitOptions.None);
            if (parts.Length < 3) return new Problem(Severity.Warning, "", line);
            var severity = Enum.TryParse<Severity>(parts[0], true, out var parsed) ? parsed : Severity.Warning;
            return new Problem(severity, parts[1], parts[2]);
        }
    }
}
