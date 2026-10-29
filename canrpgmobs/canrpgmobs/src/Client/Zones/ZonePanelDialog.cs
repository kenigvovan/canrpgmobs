using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Zones
{
    // Side panel of markup mode. Not modal, the map and the game stay usable.
    public class ZonePanelDialog : GuiDialog
    {
        private const string ComposerKey = "canrpgmobszones";
        private const double Width = 380;
        private const double LabelW = 130;
        private const double RowStep = 28;
        private const int ListRows = 9;
        // no recompose while typing, it would steal the focus
        private const long TypingPauseMs = 1200;

        private enum Page { Zone, Corners, Check, List }

        private readonly canrpgmobsModSystem mod;
        private readonly ZoneDraft draft;
        private readonly List<(string key, string value, bool number)> fills = new();
        private Page page = Page.Zone;
        private int cornerPage, listPage;
        private bool nearbyOnly = true;
        private bool confirmClose, confirmDelete;
        private bool filling;
        private int keyCounter;
        private int composedVersion = -1;
        private long typedAtMs;
        private long tickId;

        public ZonePanelDialog(ICoreClientAPI capi, canrpgmobsModSystem mod, ZoneDraft draft) : base(capi)
        {
            this.mod = mod;
            this.draft = draft;
        }

        public override string ToggleKeyCombinationCode => null!;

        // stays open while walking and marking with the wand; Alt frees the cursor for the panel
        public override bool PrefersUngrabbedMouse => false;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            draft.SetActive(true);
            confirmClose = confirmDelete = false;
            Compose();
            tickId = capi.Event.RegisterGameTickListener(_ => Tick(), 250);
            mod.ZoneStatsReceived += OnStats;
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            mod.ZoneStatsReceived -= OnStats;
            if (tickId != 0) capi.Event.UnregisterGameTickListener(tickId);
            tickId = 0;
            draft.SetActive(false);
        }

        // unsaved changes: the first close only warns
        public override bool TryClose()
        {
            if (draft.IsDirty && !confirmClose)
            {
                confirmClose = true;
                draft.Notify(Lang.Get("canrpgmobs:zones-close-unsaved"), true);
                return false;
            }
            return base.TryClose();
        }

        private void Tick()
        {
            PollStats();
            if (draft.Version == composedVersion) return;
            if (capi.World.ElapsedMilliseconds - typedAtMs < TypingPauseMs)
            {
                SingleComposer?.GetDynamicText("status")?.SetNewText(StatusText(), false, false, false);
                return;
            }
            Compose();
        }

        private void Compose()
        {
            composedVersion = draft.Version;
            fills.Clear();
            keyCounter = 0;
            var background = ElementBounds.Fixed(0, 0, Width + 30, 15 * RowStep + 60);

            ClearComposers();
            var compo = capi.Gui.CreateCompo(ComposerKey, ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle).WithFixedAlignmentOffset(-10, 0))
                .AddShadedDialogBG(background, true, 5.0, 0.75f)
                .AddDialogTitleBar(Title(), () => TryClose())
                .BeginChildElements(ElementBounds.Fixed(15, 32, Width, background.fixedHeight - 32));

            double y = 0;
            double tab = (Width - 12) / 4;
            PageButton(compo, Page.Zone, L("canrpgmobs:zones-page-zone", "Zone"), 0, y, tab);
            PageButton(compo, Page.Corners, L("canrpgmobs:zones-page-corners", "Corners ({0})", draft.Corners.Count), tab + 4, y, tab);
            PageButton(compo, Page.Check, L("canrpgmobs:zones-page-check", "Check"), (tab + 4) * 2, y, tab);
            PageButton(compo, Page.List, L("canrpgmobs:zones-page-list", "All zones"), (tab + 4) * 3, y, tab);
            y += RowStep + 4;

            switch (page)
            {
                case Page.Zone: ZonePage(compo, ref y); break;
                case Page.Corners: CornersPage(compo, ref y); break;
                case Page.Check: CheckPage(compo, ref y); break;
                default: ListPage(compo, ref y); break;
            }

            y = 12 * RowStep + 8;
            compo.AddDynamicText(StatusText(), EditorStyle.Status(draft.StatusIsError), B(0, y, Width, 40), "status");
            y += 44;
            double quarter = (Width - 12) / 4;
            bool open = draft.Draft != null;
            Button(compo, L("canrpgmobs:zones-save", "Save"), () => { draft.Save(); return true; }, 0, y, quarter, open && draft.IsDirty && !draft.Awaiting);
            Button(compo, L("canrpgmobs:zones-revert", "Revert"), () => { draft.Revert(); return true; }, quarter + 4, y, quarter, open && draft.IsDirty);
            Button(compo, L("canrpgmobs:zones-undo", "Undo"), () => { draft.Undo(); return true; }, (quarter + 4) * 2, y, quarter, draft.CanUndo);
            Button(compo, L("canrpgmobs:zones-redo", "Redo"), () => { draft.Redo(); return true; }, (quarter + 4) * 3, y, quarter, draft.CanRedo);

            SingleComposer = compo.EndChildElements().Compose(false);
            filling = true;
            try
            {
                foreach (var (key, value, number) in fills)
                {
                    if (number) SingleComposer.GetNumberInput(key)?.SetValue(value);
                    else SingleComposer.GetTextInput(key)?.SetValue(value);
                }
            }
            finally
            {
                filling = false;
            }
        }

        private string Title()
            => draft.Draft == null
                ? L("canrpgmobs:zones-title", "Mob zones")
                : (draft.IsDirty ? "* " : "") + Short(draft.Id!);

        private void ZonePage(GuiComposer compo, ref double y)
        {
            var d = draft.Draft;
            if (d == null)
            {
                compo.AddStaticText(L("canrpgmobs:zones-none-open", "Click a zone on the world map to edit it, or right click an empty spot there to start a new one."),
                    EditorStyle.Body(), B(0, y, Width, 66));
                y += 72;
                Button(compo, L("canrpgmobs:zones-new-here", "New zone where I stand"), NewHere, 0, y, Width, true);
                return;
            }

            // English; other languages come from lang files under <domain>:zone-<id>
            Label(compo, y, L("canrpgmobs:zones-name", "Name"));
            Text(compo, LabelW, y, Width - LabelW, "name-en", (d["name"] as JObject)?["en"]?.ToString() ?? "", v => SetLocText(d, "name", "en", v));
            y += RowStep;
            Label(compo, y, L("canrpgmobs:zones-id", "Id"));
            Text(compo, LabelW, y, Width - LabelW, "id", Short(ReadString(d, "id")), v => { if (IdRules.IsClean(v.Trim())) d["id"] = Q(v.Trim()); });
            y += RowStep;

            Label(compo, y, L("canrpgmobs:zones-enabled", "Works"));
            bool enabled = d["enabled"] is not JValue { Type: JTokenType.Boolean } on || (bool)on;
            Button(compo, enabled ? L("canrpgmobs:editor-yes", "yes") : L("canrpgmobs:editor-no", "no"), () =>
            {
                draft.Edit(z => { if (enabled) z["enabled"] = false; else z.Remove("enabled"); });
                return true;
            }, LabelW, y, 80, true);
            compo.AddStaticText(L("canrpgmobs:zones-priority", "Priority"), EditorStyle.FieldLabel(), B(LabelW + 96, y + 4, 70));
            Number(compo, LabelW + 170, y, 70, "priority", ReadNumber(d["priority"]), v => SetInt(d, "priority", v, 0));
            y += RowStep;

            Label(compo, y, L("canrpgmobs:zones-level", "Level (min / max)"));
            var level = d["level"] as JArray;
            string lo = level is { Count: 2 } ? level[0].ToString() : "", hi = level is { Count: 2 } ? level[1].ToString() : "";
            Number(compo, LabelW, y, 70, "level-min", lo, v => SetPair(d, "level", v, 0));
            Number(compo, LabelW + 76, y, 70, "level-max", hi, v => SetPair(d, "level", v, 1));
            y += RowStep;

            Label(compo, y, L("canrpgmobs:zones-alive", "Alive: cap / per player"));
            Number(compo, LabelW, y, 70, "maxAlive", ReadNumber(d["maxAlive"]), v => SetInt(d, "maxAlive", v, null));
            Number(compo, LabelW + 76, y, 70, "perPlayer", ReadNumber(d["perPlayer"]), v => SetInt(d, "perPlayer", v, null));
            y += RowStep;
            Label(compo, y, L("canrpgmobs:zones-respawn", "Respawn, seconds"));
            Number(compo, LabelW, y, 70, "respawn", ReadNumber(d["respawnSeconds"]), v =>
            {
                if (v.Trim().Length == 0) d.Remove("respawnSeconds");
                else if (TryParse(v, out float seconds)) d["respawnSeconds"] = Math.Max(0, seconds);
            });
            y += RowStep;

            Label(compo, y, L("canrpgmobs:zones-vanilla", "Vanilla mobs"));
            var modes = ZoneRules.VanillaModes;
            string mode = ReadString(d, "vanilla");
            compo.AddDropDown(modes, modes.Select(m => L("canrpgmobs:zones-vanilla-" + m, m)).ToArray(), Math.Max(0, Array.IndexOf(modes, mode.Length == 0 ? ZoneRules.Keep : mode)),
                (code, _) => draft.Edit(z => { if (code == ZoneRules.Keep) z.Remove("vanilla"); else z["vanilla"] = code; }), B(LabelW, y, 160), NextKey());
            y += RowStep;

            var corners = draft.Corners.ToArray();
            string size = corners.Length >= 3
                ? L("canrpgmobs:zones-size", "{0} corners, area {1}, border {2} blocks", corners.Length,
                    ZoneGeometry.Area(corners).ToString("0", CultureInfo.InvariantCulture), ZoneGeometry.Perimeter(corners).ToString("0", CultureInfo.InvariantCulture))
                : L("canrpgmobs:zones-size-open", "{0} of at least 3 corners", corners.Length);
            compo.AddStaticText(size, EditorStyle.FieldLabel(), B(0, y + 4, Width, RowH));
            y += RowStep + 4;

            double third = (Width - 8) / 3;
            Button(compo, L("canrpgmobs:zones-open-editor", "In editor"), () => { mod.ShowInEditor("zones", draft.OpenedId ?? draft.Id!); return true; }, 0, y, third, draft.OpenedId != null);
            Button(compo, confirmDelete ? L("canrpgmobs:editor-delete-confirm", "Really delete?") : L("canrpgmobs:zones-delete", "Delete"), () =>
            {
                if (!confirmDelete)
                {
                    confirmDelete = true;
                    Compose();
                    return true;
                }
                confirmDelete = false;
                draft.Delete();
                return true;
            }, third + 4, y, third, draft.InStore && !draft.Awaiting);
            Button(compo, L("canrpgmobs:zones-close-zone", "Close zone"), () =>
            {
                if (draft.IsDirty && !confirmClose)
                {
                    confirmClose = true;
                    draft.Notify(Lang.Get("canrpgmobs:zones-close-unsaved"), true);
                    return true;
                }
                confirmClose = false;
                draft.Close();
                return true;
            }, (third + 4) * 2, y, third, true);
        }

        private void CornersPage(GuiComposer compo, ref double y)
        {
            if (draft.Draft == null)
            {
                compo.AddStaticText(L("canrpgmobs:zones-none-open-short", "No zone open."), EditorStyle.Body(), B(0, y, Width, RowH));
                return;
            }
            var corners = draft.Corners;
            var bad = draft.BadEdges();
            int pages = Math.Max(1, (corners.Count + ListRows - 1) / ListRows);
            cornerPage = Math.Clamp(cornerPage, 0, pages - 1);
            for (int i = cornerPage * ListRows; i < Math.Min(corners.Count, (cornerPage + 1) * ListRows); i++)
            {
                int index = i;
                var (lx, lz) = ZoneText.ToLocal(capi, corners[i].X, corners[i].Z);
                string text = $"{i + 1}:  {lx}, {lz}" + (bad.Contains(i) ? "  !" : "");
                compo.AddStaticText(text, bad.Contains(i) ? EditorStyle.Status(true) : EditorStyle.Body(), B(0, y + 4, 150, RowH));
                Button(compo, L("canrpgmobs:zones-corner-here", "Here"), () => { MoveCornerHere(index); return true; }, 156, y, 70, true);
                Button(compo, L("canrpgmobs:zones-corner-tp", "Go"), () => { GoTo(corners[index].X, corners[index].Z); return true; }, 230, y, 60, true);
                Button(compo, "x", () =>
                {
                    draft.RemoveCornerIfAllowed(index);
                    return true;
                }, Width - 36, y, 36, true);
                y += RowStep;
            }
            y = 10 * RowStep;
            Pager(compo, y, cornerPage, pages, p => cornerPage = p);
            Button(compo, L("canrpgmobs:zones-corner-add", "Add where I stand"), () =>
            {
                var pos = capi.World.Player.Entity.Pos;
                int at = draft.AddCorner((int)Math.Floor(pos.X), (int)Math.Floor(pos.Z));
                cornerPage = at / ListRows;
                return true;
            }, 150, y, Width - 150, true);
        }

        private void MoveCornerHere(int index)
        {
            var pos = capi.World.Player.Entity.Pos;
            draft.BeginDrag();
            draft.MoveCorner(index, (int)Math.Floor(pos.X), (int)Math.Floor(pos.Z));
        }

        private void CheckPage(GuiComposer compo, ref double y)
        {
            if (draft.OpenedId == null)
            {
                compo.AddStaticText(L("canrpgmobs:zones-check-unsaved", "Save the zone first, only saved zones spawn."), EditorStyle.Body(), B(0, y, Width, RowH * 2));
                return;
            }
            compo.AddDynamicText(StatsText(), EditorStyle.Body(), B(0, y, Width, 6 * RowStep - 8), "stats");
            y += 6 * RowStep;

            string yes = L("canrpgmobs:editor-yes", "yes"), no = L("canrpgmobs:editor-no", "no");
            double half = (Width - 4) / 2;
            Button(compo, L("canrpgmobs:zones-show-attempts", "Spawn attempts: {0}", mod.ShowZoneAttempts ? yes : no), () =>
            {
                mod.ShowZoneAttempts = !mod.ShowZoneAttempts;
                draft.Notify(draft.Status, draft.StatusIsError);
                return true;
            }, 0, y, half, true);
            Button(compo, L("canrpgmobs:zones-show-ring", "Spawn ring: {0}", mod.ShowZoneRing ? yes : no), () =>
            {
                mod.ShowZoneRing = !mod.ShowZoneRing;
                draft.Notify(draft.Status, draft.StatusIsError);
                return true;
            }, half + 4, y, half, true);
            y += RowStep;

            string id = Short(draft.OpenedId);
            double third = (Width - 8) / 3;
            Button(compo, L("canrpgmobs:zones-fill", "Fill now"), () => { capi.SendChatMessage($"/canrpgmobs zone fill {draft.OpenedId}"); return true; }, 0, y, third, true);
            Button(compo, L("canrpgmobs:zones-clear", "Remove mobs"), () => { capi.SendChatMessage($"/canrpgmobs zone clear {draft.OpenedId}"); return true; }, third + 4, y, third, true);
            Button(compo, L("canrpgmobs:zones-simulate", "Dry run"), () => { capi.SendChatMessage($"/canrpgmobs zone simulate {draft.OpenedId}"); return true; }, (third + 4) * 2, y, third, true);
            y += RowStep;
            compo.AddStaticText(L("canrpgmobs:zones-check-hint", "Results of {0} go to the chat. Stand inside the zone for these.", id), EditorStyle.FieldLabel(), B(0, y + 4, Width, RowH));
        }

        private string StatsText()
        {
            var stats = mod.LastZoneStats;
            if (stats == null || stats.ZoneId != draft.OpenedId) return L("canrpgmobs:zones-stats-wait", "Asking the server...");
            return stats.Text + (draft.IsDirty ? "\n" + L("canrpgmobs:zones-stats-dirty", "(for the saved version)") : "");
        }

        private long statsAskedMs;

        private void PollStats()
        {
            if (page != Page.Check || draft.OpenedId == null) return;
            long now = capi.World.ElapsedMilliseconds;
            if (now - statsAskedMs < 1000) return;
            statsAskedMs = now;
            mod.RequestZoneStats(draft.OpenedId);
        }

        private void OnStats(ZoneStatsPacket packet)
        {
            if (page == Page.Check) SingleComposer?.GetDynamicText("stats")?.SetNewText(StatsText(), false, false, false);
        }

        private void ListPage(GuiComposer compo, ref double y)
        {
            Button(compo, nearbyOnly ? L("canrpgmobs:zones-list-nearby", "Near me") : L("canrpgmobs:zones-list-all", "All"), () =>
            {
                nearbyOnly = !nearbyOnly;
                listPage = 0;
                Compose();
                return true;
            }, 0, y, 120, true);
            Button(compo, L("canrpgmobs:zones-outlines", "Outlines in the world: {0}", mod.ZoneOutlinesWanted ? L("canrpgmobs:editor-yes", "yes") : L("canrpgmobs:editor-no", "no")), () =>
            {
                mod.SetZoneOutlines(!mod.ZoneOutlinesWanted);
                Compose();
                return true;
            }, 126, y, Width - 126, true);
            y += RowStep + 4;

            var pos = capi.World.Player.Entity.Pos;
            int maxLevel = mod.Catalog.Scaling.maxLevel;
            var zones = mod.Catalog.ZoneIndex.All
                .Select(s => (Shape: s, Distance: Distance(s, pos.X, pos.Z)))
                .Where(z => !nearbyOnly || (z.Shape.Model.dimension == pos.Dimension && z.Distance < 1000))
                .OrderBy(z => z.Distance).ToList();
            if (zones.Count == 0)
            {
                compo.AddStaticText(L("canrpgmobs:zones-list-empty", "No zones here yet."), EditorStyle.Body(), B(0, y + 4, Width, RowH));
                return;
            }
            const int rows = ListRows - 1;
            int pages = Math.Max(1, (zones.Count + rows - 1) / rows);
            listPage = Math.Clamp(listPage, 0, pages - 1);
            foreach (var (shape, distance) in zones.Skip(listPage * rows).Take(rows))
            {
                var z = shape.Model;
                string levels = ZoneText.Levels(z);
                string text = ZoneText.Name(z, Lang.CurrentLocale) + (levels.Length > 0 ? $" ({levels})" : "") + (z.enabled ? "" : " " + Lang.Get("canrpgmobs:zone-off"));
                string far = distance < 1 ? L("canrpgmobs:zones-list-inside", "here") : $"{distance:0} m";
                compo.AddStaticText(Clip(text, 30), EditorStyle.Body(), B(0, y + 4, 230, RowH))
                    .AddStaticText(far, EditorStyle.FieldLabel(), B(232, y + 4, 60, RowH));
                string id = z.id;
                Button(compo, L("canrpgmobs:zones-list-open", "Edit"), () =>
                {
                    if (draft.Id != id && draft.WarnUnsaved()) return true;
                    draft.Open(id);
                    page = Page.Zone;
                    return true;
                }, Width - 84, y, 84, draft.OpenedId != id);
                y += RowStep;
            }
            Pager(compo, 10 * RowStep, listPage, pages, p => listPage = p);
        }

        private static double Distance(ZoneShape s, double x, double z)
        {
            double dx = Math.Max(0, Math.Max(s.MinX - x, x - s.MaxX - 1));
            double dz = Math.Max(0, Math.Max(s.MinZ - z, z - s.MaxZ - 1));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private bool NewHere()
        {
            var pos = capi.World.Player.Entity.Pos;
            draft.New((int)Math.Floor(pos.X), (int)Math.Floor(pos.Z));
            if (pos.Dimension != 0) draft.Edit(z => z["dimension"] = pos.Dimension);
            return true;
        }

        // absolute coords, the server finds the ground
        private void GoTo(int x, int z) => capi.SendChatMessage($"/canrpgmobs zone goto {x} {z}");

        private void SetInt(JObject d, string key, string text, int? removeAt)
        {
            if (text.Trim().Length == 0 || (removeAt != null && int.TryParse(text.Trim(), out int n0) && n0 == removeAt)) d.Remove(key);
            else if (int.TryParse(text.Trim(), out int n)) d[key] = n;
        }

        // both ends of a pair, the other one copied when missing
        private static void SetPair(JObject d, string key, string text, int part)
        {
            if (text.Trim().Length == 0)
            {
                d.Remove(key);
                return;
            }
            if (!int.TryParse(text.Trim(), out int n)) return;
            var pair = (d[key] as JArray) is { Count: 2 } old ? new[] { (int)old[0], (int)old[1] } : new[] { n, n };
            pair[part] = n;
            d[key] = new JArray(pair[0], pair[1]);
        }

        private void PageButton(GuiComposer compo, Page target, string text, double x, double y, double w)
            => compo.AddSmallButton((page == target ? "> " : "") + text, () =>
            {
                page = target;
                confirmDelete = false;
                Compose();
                return true;
            }, B(x, y, w), EnumButtonStyle.Small);

        private void Button(GuiComposer compo, string text, ActionConsumable onClick, double x, double y, double w, bool enabled)
        {
            string key = NextKey();
            compo.AddSmallButton(text, () =>
            {
                if (!enabled) return true;
                confirmClose = false;
                onClick();
                Compose();
                return true;
            }, B(x, y, w), EnumButtonStyle.Small, key);
            compo.GetButton(key).Enabled = enabled;
        }

        private void Pager(GuiComposer compo, double y, int current, int pages, Action<int> set)
        {
            if (pages <= 1) return;
            compo.AddSmallButton("<", () => { if (current > 0) { set(current - 1); Compose(); } return true; }, B(0, y, 36), EnumButtonStyle.Small)
                .AddStaticText($"{current + 1}/{pages}", EditorStyle.FieldLabel(), B(42, y + 4, 60))
                .AddSmallButton(">", () => { if (current < pages - 1) { set(current + 1); Compose(); } return true; }, B(100, y, 36), EnumButtonStyle.Small);
        }

        private void Label(GuiComposer compo, double y, string text)
            => compo.AddStaticText(text, EditorStyle.FieldLabel(), B(0, y + 4, LabelW - 6));

        private void Text(GuiComposer compo, double x, double y, double w, string field, string value, Action<string> apply)
        {
            string key = NextKey();
            compo.AddTextInput(B(x, y, w), v => Typed(field, v, apply), CairoFont.WhiteDetailText(), key);
            fills.Add((key, value, false));
        }

        private void Number(GuiComposer compo, double x, double y, double w, string field, string value, Action<string> apply)
        {
            string key = NextKey();
            compo.AddNumberInput(B(x, y, w), v => Typed(field, v, apply), CairoFont.WhiteDetailText(), key);
            fills.Add((key, value, true));
        }

        private void Typed(string field, string value, Action<string> apply)
        {
            if (filling) return;
            typedAtMs = capi.World.ElapsedMilliseconds;
            draft.Type(field, _ => apply(value));
        }

        private string StatusText()
        {
            if (draft.Status.Length > 0) return draft.Status;
            string state = draft.Draft == null ? L("canrpgmobs:zones-status-none", "Markup mode: the map edits zones.")
                : draft.IsDirty ? L("canrpgmobs:zones-status-dirty", "Unsaved changes.") : L("canrpgmobs:zones-status-clean", "Saved.");
            return state + "\n" + L("canrpgmobs:zones-hint-alt", "Hold Alt to click the panel.");
        }

        private string NextKey() => "z" + keyCounter++;

        private static string Clip(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + ".";
    }
}
