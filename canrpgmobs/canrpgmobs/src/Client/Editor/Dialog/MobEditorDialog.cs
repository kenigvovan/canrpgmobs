using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // In-game content editor (controlserver). Recomposed on every structural change, like the canrpgclasses editors.
    // Drafts go through the same loader/validator as on the server before sending.
    public partial class MobEditorDialog : GuiDialog
    {
        private const string ComposerKey = "canrpgmobseditor";
        private const double ListW = 250;
        private const double PanelW = 600;
        private const double BodyH = 440;
        private const double RowStep = 28;
        private const long LiveTextDelayMs = 250;
        private const long SearchApplyMs = 500;
        // never a valid id
        private const string NewEntryMark = "\0new";
        private const string CopyMark = "\0from";

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private enum Tab { Fields, Lists, Problems }

        private enum Page { Editor, Wizard, Test, Reference, Translations }

        private readonly canrpgmobsModSystem mod;
        private readonly EditorSession session;
        private readonly List<(string key, string value, bool number)> fills = new();
        // runs after Compose(), needs the composed elements
        private readonly List<Action> afterCompose = new();
        // dynamic texts refreshed while typing; a full rebuild would steal focus from the text field
        private readonly List<(string key, Func<string> text)> liveTexts = new();
        // remembered page numbers per screen, so going back doesn't reset to page one
        private readonly Dictionary<string, (int Form, int Child, int Problem, int Simple)> pageMemory = new(StringComparer.Ordinal);

        private Tab tab = Tab.Fields;
        private string filter = "";
        private string filterDraft = "";
        private int listPage;
        private int formPage;
        private int childPage;
        private int problemPage;
        private string status = "";
        private bool statusIsProblem;
        private string? pendingOpen;
        private string? pendingSection;
        private bool confirmDelete;
        private bool awaitingResult;
        private string previewKey = "";
        private int previewLevel;
        private string previewTier = "";
        private int keyCounter;
        private Page page;
        private string pageKey = "";
        private bool footerMore;
        private bool filling;
        private int composedVersion = -1;
        private int seenVersion = -1;
        private long lastEditMs;
        private long searchTypedMs;
        private string? searchKey;
        private string? refocusKey;

        public MobEditorDialog(ICoreClientAPI capi, canrpgmobsModSystem mod) : base(capi)
        {
            this.mod = mod;
            session = new EditorSession(new EditorSchema(MobsApi.Vocabulary));
        }

        public override string ToggleKeyCombinationCode => null!;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // replies that arrived while closed are lost, don't wait for them
            awaitingResult = false;
            pendingBundle = null;
            pendingDelete = null;
            mod.CatalogChanged += OnCatalogChanged;
            mod.EditResultReceived += OnEditResult;
            mod.TestResultReceived += OnTestResult;
            previewTickId = capi.Event.RegisterGameTickListener(OnPreviewTick, 100);
            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            mod.CatalogChanged -= OnCatalogChanged;
            mod.EditResultReceived -= OnEditResult;
            mod.TestResultReceived -= OnTestResult;
            StopTest();
            if (previewTickId != 0) capi.Event.UnregisterGameTickListener(previewTickId);
            previewTickId = 0;
            DisposePreview();
        }

        private bool OpenPage(Page target)
        {
            ClearPending();
            if (page == Page.Test && target != Page.Test) StopWatching();
            pick = null;
            itemPick = null;
            page = target;
            SetStatus("", false);
            Compose();
            return true;
        }

        private void Compose()
        {
            ForgetEffective();
            RestorePages();
            fills.Clear();
            afterCompose.Clear();
            liveTexts.Clear();
            keyCounter = 0;
            const double top = 4;
            double panelX = ListW + 24;
            previewShown = PreviewWanted && PreviewFits;
            previewBounds = null;
            double extraW = previewShown ? PreviewW + 16 : 0;
            var background = ElementBounds.Fixed(0, 0, ListW + PanelW + 70 + extraW, BodyH + 190);

            ClearComposers();
            var compo = capi.Gui.CreateCompo(ComposerKey, ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
                .AddShadedDialogBG(background, true, 5.0, 0.75f)
                .AddDialogTitleBar(L("canrpgmobs:editor-title", "Mob content editor"), () => TryClose())
                .BeginChildElements(ElementBounds.Fixed(20, 30, ListW + PanelW + 30 + extraW, background.fixedHeight - 30));

            AddEntryList(compo, 0, top);
            bool simple = SimpleActive;
            bool hasTabs = page == Page.Editor && session.Draft != null && pick == null && itemPick == null && !simple;
            if (itemPick != null) AddItemPickPage(compo, panelX, top);
            else if (pick != null) AddPickPage(compo, panelX, top);
            else if (page == Page.Wizard) AddWizardPage(compo, panelX, top);
            else if (page == Page.Test) AddTestPage(compo, panelX, top);
            else if (page == Page.Reference) AddReferencePage(compo, panelX, top);
            else if (page == Page.Translations) AddTranslationsPage(compo, panelX, top);
            else if (session.Draft == null) AddEmptyPanel(compo, panelX, top);
            else if (simple) AddSimplePanel(compo, panelX, top);
            else AddDraftPanel(compo, panelX, top);
            AddFooter(compo, panelX, top + BodyH + 10);
            if (previewShown) AddPreviewColumn(compo, panelX + PanelW + 16, top);

            SingleComposer = compo.EndChildElements().Compose();
            if (hasTabs) SingleComposer.GetHorizontalTabs("tabs").activeElement = (int)tab;
            foreach (var action in afterCompose) action();
            SchedulePreview();
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
            // give focus back to a search box that auto-applied while typing (caret is already at the end)
            if (refocusKey is { } focus)
            {
                refocusKey = null;
                if (SingleComposer.GetTextInput(focus) is { } input) SingleComposer.FocusElement(input.TabIndex);
            }
            composedVersion = session.Version;
            SendLiveLookIfChanged();
        }

        // 100 ms tick: refresh live texts once typing stops, auto-apply the search filter
        private void FollowTyping()
        {
            long now = capi.World.ElapsedMilliseconds;
            if (session.Version != composedVersion)
            {
                if (session.Version != seenVersion)
                {
                    seenVersion = session.Version;
                    lastEditMs = now;
                }
                else if (now - lastEditMs >= LiveTextDelayMs)
                {
                    composedVersion = session.Version;
                    InvalidateEffective();
                    UpdateLiveTexts();
                }
            }

            if (searchTypedMs == 0 || now - searchTypedMs < SearchApplyMs) return;
            ApplyTypedFilter();
        }

        private void UpdateLiveTexts()
        {
            if (SingleComposer == null) return;
            foreach (var (key, text) in liveTexts) SingleComposer.GetDynamicText(key)?.SetNewText(text(), false, false, false);
        }

        private void LiveText(GuiComposer compo, ElementBounds bounds, CairoFont font, Func<string> text)
        {
            string key = NextKey();
            compo.AddDynamicText(text(), font, bounds, key);
            liveTexts.Add((key, text));
        }

        // A focused text box swallows almost every key, so grab these first. None of them type anything.
        public override void OnKeyDown(KeyEvent args)
        {
            if (args.CtrlPressed && args.KeyCode == (int)GlKeys.S)
            {
                args.Handled = true;
                OnSave();
                return;
            }
            switch ((GlKeys)args.KeyCode)
            {
                case GlKeys.Enter:
                case GlKeys.KeypadEnter:
                    if (!ApplyTypedFilter()) break;
                    args.Handled = true;
                    return;
                case GlKeys.PageUp:
                    args.Handled = FlipVisiblePage(-1, args.CtrlPressed);
                    return;
                case GlKeys.PageDown:
                    args.Handled = FlipVisiblePage(1, args.CtrlPressed);
                    return;
            }
            base.OnKeyDown(args);
        }

        // keep focus in the box after Enter
        private bool ApplyTypedFilter()
        {
            searchTypedMs = 0;
            refocusKey = searchKey;
            if (ApplyVisibleFilter()) return true;
            refocusKey = null;
            return false;
        }

        // Escape backs out of the current screen first, closes only at the top
        public override bool OnEscapePressed()
        {
            if (itemPick != null) itemPick = null;
            else if (pick != null) pick = null;
            else if (spellBrowser) { spellBrowser = false; spellReplace = null; }
            else if (skillEdit != null) skillEdit = null;
            else if (page != Page.Editor) return OpenPage(Page.Editor);
            else return base.OnEscapePressed();
            Compose();
            return true;
        }

        // false = nothing changed, let the key through
        private bool ApplyVisibleFilter()
        {
            if (itemPick != null)
            {
                string typed = itemFilterDraft.Trim().ToLowerInvariant();
                if (typed == itemFilter) return false;
                itemFilter = typed;
                itemPage = 0;
            }
            else if (pick != null)
            {
                if (pickFilterDraft.Trim() == pickFilter) return false;
                pickFilter = pickFilterDraft.Trim();
                pickPage = 0;
            }
            else if (page == Page.Reference)
            {
                if (refFilterDraft.Trim() == refFilter) return false;
                refFilter = refFilterDraft.Trim();
                refPage = 0;
            }
            else if (page == Page.Translations)
            {
                if (trFilterDraft.Trim() == trFilter) return false;
                trFilter = trFilterDraft.Trim();
                trPage = 0;
            }
            else if (spellBrowser)
            {
                if (spellFilterDraft.Trim() == spellFilter) return false;
                spellFilter = spellFilterDraft.Trim();
                spellPage = 0;
            }
            else
            {
                if (filterDraft.Trim() == filter) return false;
                filter = filterDraft.Trim();
                listPage = 0;
            }
            Compose();
            return true;
        }

        // ctrl+PgUp/PgDn flips the entry list on the left
        private bool FlipVisiblePage(int step, bool entryList)
        {
            int Next(int page) => Math.Max(0, page + step);
            if (entryList) listPage = Next(listPage);
            else if (itemPick != null) itemPage = Next(itemPage);
            else if (pick != null) pickPage = Next(pickPage);
            else if (page == Page.Reference) refPage = Next(refPage);
            else if (page == Page.Translations) trPage = Next(trPage);
            else if (spellBrowser) spellPage = Next(spellPage);
            else if (SimpleActive) simplePage = Next(simplePage);
            else if (page == Page.Editor && session.Draft != null)
            {
                if (tab == Tab.Fields) formPage = Next(formPage);
                else if (tab == Tab.Lists) childPage = Next(childPage);
                else problemPage = Next(problemPage);
            }
            else listPage = Next(listPage);
            Compose();
            return true;
        }

        // SetValue fires the change callback; refilling inputs after compose must not touch the draft
        private Action<string> Guard(Action<string> onChanged) => value =>
        {
            if (filling) return;
            onChanged(value);
            ForgetEffective();
        };

        // any other action cancels "click again to confirm"
        private void ClearPending()
        {
            pendingOpen = null;
            pendingSection = null;
            confirmDelete = false;
        }

        private void AddEmptyPanel(GuiComposer compo, double x, double y)
        {
            PanelInset(compo, x, y, -4)
                .AddStaticText(L("canrpgmobs:editor-empty", "Pick an entry on the left or create a new one. * = edited in game, (!) = has errors."),
                    EditorStyle.Body(), B(x + 6, y + 10, PanelW - 12, 60));
        }

        private void AddDraftPanel(GuiComposer compo, double x, double y)
        {
            var frame = session.Current!;
            PanelInset(compo, x, y, 58);
            AddBreadcrumbs(compo, x, y);
            AddModeToggle(compo, x + PanelW - 130, y);

            var tabs = new[]
            {
                new GuiTab { Name = L("canrpgmobs:editor-tab-fields", "Fields"), DataInt = 0 },
                new GuiTab { Name = L("canrpgmobs:editor-tab-lists", "Lists ({0})", frame.Node.Children.Count), DataInt = 1 },
                new GuiTab { Name = L("canrpgmobs:editor-tab-problems", "Problems ({0})", session.Problems.Count), DataInt = 2 }
            };
            compo.AddHorizontalTabs(tabs, B(x, y + 30, PanelW, 26), i => { tab = (Tab)i; ClearPending(); Compose(); }, CairoFont.WhiteSmallText(),
                CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor), "tabs");

            double bodyY = y + 66;
            switch (tab)
            {
                case Tab.Fields: AddFieldsTab(compo, x, bodyY, frame); break;
                case Tab.Lists: AddListsTab(compo, x, bodyY, frame); break;
                default: AddProblemsTab(compo, x, bodyY); break;
            }
        }

        private void AddBreadcrumbs(GuiComposer compo, double x, double y)
        {
            var stack = session.Stack;
            double cx = x;
            // drop the oldest crumbs if the path doesn't fit
            int first = 0;
            double width = stack.Sum(f => CrumbWidth(f.Label) + 4);
            // leave room for the mode toggle
            double room = SimpleAvailable ? PanelW - 136 : PanelW;
            while (width > room && first < stack.Count - 1)
            {
                width -= CrumbWidth(stack[first].Label) + 4;
                first++;
            }
            if (first > 0)
            {
                compo.AddStaticText("...", EditorStyle.FieldLabel(), B(cx, y + 4, 20));
                cx += 24;
            }
            for (int i = first; i < stack.Count; i++)
            {
                int depth = i;
                string label = Clip(stack[i].Label, 22);
                double w = CrumbWidth(stack[i].Label);
                compo.AddSmallButton(label, () => { session.PopTo(depth); ResetPages(); Compose(); return true; }, B(cx, y, w), EnumButtonStyle.Small);
                cx += w + 4;
            }
        }

        private static double CrumbWidth(string label) => 16 + Math.Min(22, label.Length) * 7.5;

        // Save first and widest; rare actions behind "..."; spawn and look rows live in the preview column when it is shown
        private void AddFooter(GuiComposer compo, double x, double y)
        {
            if (session.Draft != null && page == Page.Editor)
            {
                const double gap = 6;
                const double moreW = 40;
                double saveW = 200;
                double bw = (PanelW - saveW - moreW - 3 * gap) / 2;
                bool dirty = session.IsDirty;
                compo.AddSmallButton((dirty ? "* " : "") + L("canrpgmobs:editor-save", "Save") + "  (Ctrl+S)", OnSave, B(x, y, saveW), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-revert", "Revert"), OnRevert, B(x + saveW + gap, y, bw), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-close-entry", "Close"), () => { ClearPending(); session.Close(); lootReturn = null; SetStatus("", false); Compose(); return true; },
                        B(x + saveW + bw + 2 * gap, y, bw), EnumButtonStyle.Small)
                    .AddSmallButton(footerMore ? "x" : "...", () => { footerMore = !footerMore; ClearPending(); Compose(); return true; },
                        B(x + PanelW - moreW, y, moreW), EnumButtonStyle.Small);

                double row = y + RowStep + 2;
                if (footerMore)
                {
                    double mw = (PanelW - 2 * gap) / 3;
                    string deleteText = confirmDelete ? L("canrpgmobs:editor-delete-confirm", "Really delete?") : L("canrpgmobs:editor-delete", "Delete");
                    compo.AddSmallButton(L("canrpgmobs:editor-validate", "Validate"), OnValidate, B(x, row, mw), EnumButtonStyle.Small)
                        .AddSmallButton(L("canrpgmobs:editor-duplicate", "Duplicate"), OnDuplicate, B(x + mw + gap, row, mw), EnumButtonStyle.Small)
                        .AddSmallButton(deleteText, OnDelete, B(x + 2 * (mw + gap), row, mw), EnumButtonStyle.Small);
                    row += RowStep;
                }
                if (!previewShown && session.Section is "mobs" or "races" or "roles")
                {
                    AddSpawnRow(compo, x, row, PanelW, true);
                    row += RowStep;
                    if (!footerMore) AddLookRow(compo, x, row, PanelW);
                }
            }

            if (status.Length > 0)
                compo.AddStaticText(status, EditorStyle.Status(statusIsProblem), B(x, y + RowStep * 3 + 6, PanelW, 44));
        }

        private bool OnRevert()
        {
            ClearPending();
            session.Revert();
            ResetPages();
            if (session.Draft == null) lootReturn = null;
            SetStatus("", false);
            Compose();
            return true;
        }

        // the key follows the open entry (SuggestPreviewKey), only level and tier are asked
        private void AddSpawnRow(GuiComposer compo, double x, double y, double width, bool showWhat)
        {
            string levelField = NextKey();
            double cx = x;
            if (showWhat)
            {
                compo.AddFitText(L("canrpgmobs:editor-preview-what", "In the world: {0}", MobKeyName(previewKey)), UiKit.LabelFont(), B(x, y + 4, 280));
                cx += 284;
            }
            compo.AddStaticText(L("canrpgmobs:editor-preview-level", "lvl"), EditorStyle.FieldLabel(), B(cx, y + 4, 30))
                .AddNumberInput(B(cx + 30, y, 44), v => previewLevel = int.TryParse(v, out int level) ? level : 0, CairoFont.WhiteDetailText(), levelField);
            TierDropDown(compo, previewTier, code => previewTier = code, L("canrpgmobs:editor-simple-normal-tier", "(normal)"), B(cx + 78, y, showWhat ? 100 : width - 78));
            fills.Add((levelField, previewLevel > 0 ? previewLevel.ToString(CultureInfo.InvariantCulture) : "", true));
            if (showWhat)
                compo.AddSmallButton(L("canrpgmobs:editor-preview", "Spawn preview"), OnPreview, B(cx + 184, y, width - (cx - x) - 184), EnumButtonStyle.Small);
        }

        // "" first, the default tier
        private void TierDropDown(GuiComposer compo, string current, Action<string> set, string emptyLabel, ElementBounds bounds)
        {
            var tiers = mod.Catalog.Tiers.Keys.OrderBy(k => k).Prepend("").ToArray();
            compo.AddDropDown(tiers, tiers.Select(t => t.Length == 0 ? emptyLabel : NameOf("tiers", t)).ToArray(),
                Math.Max(0, Array.IndexOf(tiers, current)), (code, _) => set(code), bounds, NextKey());
        }

        // the panel frame; top is where it starts below the panel's header
        private static GuiComposer PanelInset(GuiComposer compo, double x, double y, double top)
            => compo.AddInset(B(x - 6, y + top, PanelW + 12, BodyH - top), 3, 0.85f);

        private void RestorePages()
        {
            string key = PageKey();
            if (key == pageKey) return;
            if (pageKey.Length > 0) pageMemory[pageKey] = (formPage, childPage, problemPage, simplePage);
            pageKey = key;
            (formPage, childPage, problemPage, simplePage) = key.Length > 0 ? pageMemory.GetValueOrDefault(key) : default;
        }

        // after the first save a new entry gets a name but is still the same screen
        private void AdoptPages() => pageKey = PageKey();

        // keyed by the id the entry was opened under, so editing the id field doesn't count as a new screen
        private string PageKey()
        {
            if (page != Page.Editor || session.Draft == null) return "";
            string entry = session.Section + "|" + (session.OpenedId ?? NewEntryMark);
            return SimpleActive
                ? $"s|{entry}|{simpleTab}|{skillEdit}"
                : $"a|{entry}|{string.Join(">", session.Stack.Select(f => f.Label))}";
        }

        private void ResetPages()
        {
            // page numbers are restored via PageKey, not here
            tab = Tab.Fields;
            simpleTab = "main";
            simpleProblems = false;
            lootTestResult = null;
            skillEdit = null;
            spellBrowser = false;
            spellReplace = null;
            pick = null;
            itemPick = null;
        }

        private void SetStatus(string text, bool problem)
        {
            status = text;
            statusIsProblem = problem;
        }

        private string UniqueId(string baseId)
        {
            var taken = new HashSet<string>(EntryIds(session.Section), StringComparer.Ordinal);
            string id = baseId;
            for (int n = 2; taken.Contains(id); n++) id = baseId + n;
            return id;
        }

        private static bool Same(string a, string b) => SameId(a, b);

        private string NextKey() => "e" + keyCounter++;

        private static string Clip(string text, int max) => text.Length <= max ? text : text.Substring(0, Math.Max(1, max - 3)) + "...";
    }
}
