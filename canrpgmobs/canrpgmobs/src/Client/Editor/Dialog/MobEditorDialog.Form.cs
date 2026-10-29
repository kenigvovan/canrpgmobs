using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    public partial class MobEditorDialog
    {
        private const double LabelW = 180;
        private const int FormRows = 11;

        private sealed class KeyHolder
        {
            public KeyHolder(string key) => Key = key;
            public string Key { get; set; }
        }

        // Card: this row heads a card, the rows after it up to the next card are framed with it
        private readonly record struct FormRow(int Rows, Action<GuiComposer, double> Draw, string? Card = null);

        // like the claims cards: gold heading, a thin rule under it
        private FormRow CardRow(double x, string title) => new(1, (c, y) =>
        {
            c.AddFitText(title, UiKit.SectionFont(), B(x + 4, y + 2, PanelW - 8, 20));
            c.AddInset(B(x + 4, y + RowStep - 5, PanelW - 8, 2));
        }, title);

        private void AddFieldsTab(GuiComposer compo, double x, double y, NavFrame frame)
        {
            var rows = new List<FormRow>();
            if (frame.MapKey != null) rows.Add(new FormRow(1, (c, ry) => DrawMapKey(c, x, ry, frame)));
            foreach (var field in frame.Node.FieldsFor(frame.Token))
            {
                var spec = field;
                rows.Add(new FormRow(1, (c, ry) => DrawField(c, x, ry, frame.Token, spec, frame.Node.Id)));
                // one row per map entry so long maps get paged
                if (spec.Kind != FieldKind.KeyValue || frame.Token[spec.Key] is not JObject map) continue;
                foreach (var property in map.Properties().ToList())
                {
                    string key = property.Name;
                    rows.Add(new FormRow(1, (c, ry) => DrawKeyValueEntry(c, x, ry, frame.Token, spec, map, key)));
                }
            }
            DrawPaged(compo, x, y, rows, FormRows, formPage, p => formPage = p);
        }

        private void DrawMapKey(GuiComposer compo, double x, double y, NavFrame frame)
        {
            string key = NextKey();
            compo.AddStaticText(L("canrpgmobs:editor-field-__key", "Key"), EditorStyle.FieldLabel(), B(x, y + 4, LabelW - 6))
                .AddTextInput(B(x + LabelW, y, ControlW), Guard(v => session.RenameMapKey(frame, v)), CairoFont.WhiteDetailText(), key);
            fills.Add((key, frame.MapKey ?? "", false));
        }

        private static double ControlW => PanelW - LabelW - 6;

        private void DrawField(GuiComposer compo, double x, double y, JObject token, FieldSpec field, string? nodeId = null)
        {
            string label = FieldLabel(field.Key);
            if (field.Kind == FieldKind.LocText) label = L("canrpgmobs:editor-loctext", "{0} (lang key | en | ru)", label);
            compo.AddFitText(label, UiKit.LabelFont(), B(x, y + 4, LabelW - 6));
            // help on hover keeps row height fixed
            string help = FieldHelp(nodeId, field.Key);
            if (help.Length > 0) compo.AddHoverText(help, CairoFont.WhiteDetailText(), 320, B(x, y, LabelW - 6));
            double cx = x + LabelW;

            switch (field.Kind)
            {
                case FieldKind.Id:
                case FieldKind.Text:
                    TextInput(compo, B(cx, y, ControlW), ReadString(token, field.Key), v => SetString(token, field.Key, v, field.Kind == FieldKind.Id));
                    break;

                case FieldKind.Number:
                case FieldKind.Int:
                    NumberInput(compo, B(cx, y, 140), ReadNumber(token[field.Key]), v => SetNumber(token, field.Key, v, field));
                    break;

                case FieldKind.Toggle:
                    YesNoToggle(compo, token, field.Key, field.Default, cx, y, 200, markDefault: true);
                    break;

                case FieldKind.TriState:
                {
                    bool? value = token[field.Key] is JValue { Type: JTokenType.Boolean } b ? (bool)b : null;
                    string text = value == null ? L("canrpgmobs:editor-auto", "auto") : value.Value ? L("canrpgmobs:editor-yes", "yes") : L("canrpgmobs:editor-no", "no");
                    compo.AddSmallButton(text, () =>
                    {
                        if (value == null) token[field.Key] = true;
                        else if (value.Value) token[field.Key] = false;
                        else token.Remove(field.Key);
                        Compose();
                        return true;
                    }, B(cx, y, 140), EnumButtonStyle.Small);
                    break;
                }

                case FieldKind.Choice:
                {
                    var choices = field.Choices ?? Array.Empty<string>();
                    var values = field.Required ? choices : choices.Prepend("").ToArray();
                    var names = field.Required ? choices : choices.Prepend(L("canrpgmobs:editor-default", "(default)")).ToArray();
                    int selected = Math.Max(0, Array.IndexOf(values, ReadString(token, field.Key)));
                    compo.AddDropDown(values, names, selected, (code, _) =>
                    {
                        if (code.Length == 0) token.Remove(field.Key);
                        else token[field.Key] = code;
                        Compose();
                    }, B(cx, y, 200), NextKey());
                    break;
                }

                case FieldKind.Pick:
                {
                    // mob fields: "..." lists the named mobs, the second button picks race then role
                    bool mobField = field.PickList == "mobKeys";
                    double inputW = ControlW - 44 - (field.IsSound ? 64 : 0) - (mobField ? 34 : 0);
                    TextInput(compo, B(cx, y, inputW), ReadString(token, field.Key), v => SetString(token, field.Key, v, false));
                    compo.AddSmallButton("...", () => field.PickList!.StartsWith("items:", StringComparison.Ordinal)
                        ? OpenItemPick(FieldLabel(field.Key), ItemCategoriesFor(field.PickList), v =>
                        {
                            if (v == null) token.Remove(field.Key);
                            else token[field.Key] = v;
                        }, true)
                        : OpenPick(mobField ? "mobsNamed" : field.PickList, FieldLabel(field.Key), null, v => token[field.Key] = v), B(cx + inputW + 4, y, 40), EnumButtonStyle.Small);
                    if (mobField)
                        compo.AddIcon(UiKit.ClassesIcon("dark-squad"), L("canrpgmobs:zones-spawn-add-pair", "+ Race and role"),
                            () => PickRaceRole(key => token[field.Key] = key), B(cx + inputW + 48, y, 30));
                    if (field.IsSound)
                        compo.AddSmallButton(L("canrpgmobs:editor-play", "Play"), () => PlaySound(ReadString(token, field.Key)), B(cx + inputW + 48, y, 60), EnumButtonStyle.Small);
                    break;
                }

                case FieldKind.LocText:
                {
                    // lang key and the English text; other languages come from lang files
                    double w = (ControlW - 4) / 2;
                    var text = token[field.Key] as JObject;
                    string Part(string k) => text?[k] is JValue value ? value.ToString() : "";
                    TextInput(compo, B(cx, y, w), Part("langKey"), v => SetLocText(token, field.Key, "langKey", v));
                    TextInput(compo, B(cx + w + 4, y, w), Part("en"), v => SetLocText(token, field.Key, "en", v));
                    break;
                }

                case FieldKind.RangePair:
                case FieldKind.IntRangePair:
                {
                    RangeInputs(compo, token, field.Key, field, cx, y, 110, 8);
                    break;
                }

                case FieldKind.StringList:
                {
                    double inputW = field.PickList != null ? ControlW - 44 : ControlW;
                    string joined = token[field.Key] is JArray list ? string.Join(", ", list.Select(t => t.ToString())) : "";
                    TextInput(compo, B(cx, y, inputW), joined, v => SetList(token, field.Key, v));
                    if (field.PickList != null)
                    {
                        void Add(string v)
                        {
                            var array = EnsureArray(token, field.Key);
                            if (!array.Any(t => t.ToString() == v)) array.Add(v);
                        }
                        compo.AddIcon("plus", L("canrpgmobs:editor-tip-add", "Add"), () => field.PickList.StartsWith("items:", StringComparison.Ordinal)
                            ? OpenItemPick(FieldLabel(field.Key), ItemCategoriesFor(field.PickList), v => { if (v != null) Add(v); }, false)
                            : OpenPick(field.PickList, FieldLabel(field.Key), null, Add), B(cx + inputW + 4, y, 30));
                    }
                    break;
                }

                case FieldKind.KeyValue:
                    DrawKeyValueAdd(compo, cx, y, token, field);
                    break;
            }
        }

        private void DrawKeyValueAdd(GuiComposer compo, double cx, double y, JObject token, FieldSpec field)
        {
            compo.AddSmallButton(L("canrpgmobs:editor-add", "Add"), () =>
            {
                var target = EnsureObject(token, field.Key);
                string key = "key";
                for (int n = 2; target.ContainsKey(key); n++) key = "key" + n;
                target[key] = field.ValueChoices?.FirstOrDefault() is { } first ? first : field.NumberValues ? 0 : "";
                Compose();
                return true;
            }, B(cx, y, 100), EnumButtonStyle.Small);
        }

        // holder tracks the key across in-place renames
        private void DrawKeyValueEntry(GuiComposer compo, double x, double y, JObject token, FieldSpec field, JObject map, string key)
        {
            if (map[key] is not { } value) return;
            var holder = new KeyHolder(key);
            double keyW = field.PickList != null ? 150 : 190;
            TextInput(compo, B(x + 16, y, keyW), key, v => RenameProperty(map, holder, v));
            if (field.PickList != null)
                compo.AddSmallButton("...", () => OpenPick(field.PickList, FieldLabel(field.Key), null, v => RenameProperty(map, holder, v)),
                    B(x + 16 + keyW + 4, y, 36), EnumButtonStyle.Small);

            double vx = x + 16 + 196;
            double valueW = PanelW - 216 - 50 - (field.ValuePickList != null ? 40 : 0);
            if (field.ValueChoices != null)
            {
                var choices = field.ValueChoices;
                int selected = Math.Max(0, Array.IndexOf(choices, value.ToString()));
                // recompose so the entry shows as unsaved
                compo.AddDropDown(choices, choices, selected, (code, _) => { map[holder.Key] = code; Compose(); },
                    B(vx, y, Math.Min(valueW, 160)), NextKey());
            }
            else if (field.NumberValues)
                NumberInput(compo, B(vx, y, 120), ReadNumber(value), v =>
                {
                    if (TryParse(v, out float number)) map[holder.Key] = number;
                });
            else
            {
                // skin parts get < > to cycle variants
                bool cycles = field.ValuePickList == "plmVariants";
                double inputW = cycles ? valueW - 68 : valueW;
                TextInput(compo, B(vx, y, inputW), value.ToString(), v => map[holder.Key] = v.Trim());
                if (field.ValuePickList != null)
                    compo.AddSmallButton("...", () => OpenPick(field.ValuePickList, FieldLabel(field.Key), holder.Key, v => map[holder.Key] = v),
                        B(vx + inputW + 4, y, 36), EnumButtonStyle.Small);
                if (cycles)
                    compo.AddIcon("left", L("canrpgmobs:editor-tip-prev-variant", "Previous variant"), () => CycleVariant(map, holder, -1), B(vx + inputW + 44, y, 30))
                        .AddIcon("right", L("canrpgmobs:editor-tip-next-variant", "Next variant"), () => CycleVariant(map, holder, 1), B(vx + inputW + 78, y, 30));
            }

            RemoveButton(compo, x, y, () => { map.Remove(holder.Key); RemoveIfEmpty(token, field.Key); });
        }

        private void AddListsTab(GuiComposer compo, double x, double y, NavFrame frame)
        {
            var rows = new List<FormRow>();
            foreach (var child in frame.Node.Children)
            {
                var spec = child;
                string label = L("canrpgmobs:editor-child-" + spec.Key, spec.Key);
                switch (spec.Mode)
                {
                    case ChildMode.Object:
                        rows.Add(new FormRow(1, (c, ry) =>
                        {
                            bool exists = frame.Token[spec.Key] is JObject;
                            c.AddStaticText(label, EditorStyle.FieldLabel(), B(x, ry + 4, LabelW - 6))
                                .AddSmallButton(exists ? L("canrpgmobs:editor-open", "Open") : L("canrpgmobs:editor-create", "Create"),
                                    () => { session.OpenObject(spec); ResetPages(); Compose(); return true; }, B(x + LabelW, ry, 140), EnumButtonStyle.Small);
                            if (exists)
                                c.AddSmallButton(L("canrpgmobs:editor-clear", "Clear"), () => { session.ClearObject(spec); Compose(); return true; },
                                    B(x + LabelW + 148, ry, 100), EnumButtonStyle.Small);
                        }));
                        break;

                    case ChildMode.Array:
                    {
                        var array = frame.Token[spec.Key] as JArray;
                        rows.Add(new FormRow(1, (c, ry) => Header(c, x, ry, label, array?.Count ?? 0, () =>
                        {
                            int index = session.AddItem(spec);
                            session.OpenItem(spec, index);
                            ResetPages();
                        })));
                        for (int i = 0; array != null && i < array.Count; i++)
                        {
                            if (array[i] is not JObject item) continue;
                            int index = i;
                            rows.Add(new FormRow(1, (c, ry) => ItemRow(c, x, ry, ItemText(spec, item, null),
                                () => { session.OpenItem(spec, index); ResetPages(); }, () => session.RemoveItem(spec, index))));
                        }
                        break;
                    }

                    case ChildMode.Map:
                    {
                        var map = frame.Token[spec.Key] as JObject;
                        rows.Add(new FormRow(1, (c, ry) => Header(c, x, ry, label, map?.Count ?? 0, () =>
                        {
                            string key = session.AddMapEntry(spec);
                            session.OpenMapEntry(spec, key);
                            ResetPages();
                        })));
                        foreach (var property in map?.Properties().ToList() ?? new List<JProperty>())
                        {
                            if (property.Value is not JObject item) continue;
                            string key = property.Name;
                            rows.Add(new FormRow(1, (c, ry) => ItemRow(c, x, ry, ItemText(spec, item, key),
                                () => { session.OpenMapEntry(spec, key); ResetPages(); }, () => session.RemoveMapEntry(spec, key))));
                        }
                        break;
                    }
                }
            }
            DrawPaged(compo, x, y, rows, FormRows, childPage, p => childPage = p);
        }

        private void Header(GuiComposer compo, double x, double y, string label, int count, Action add)
        {
            compo.AddStaticText($"{label} ({count})", UiKit.SectionFont(), B(x, y + 2, LabelW + 100))
                .AddSmallButton(L("canrpgmobs:editor-add", "Add"), () => { add(); Compose(); return true; }, B(x + PanelW - 110, y, 106), EnumButtonStyle.Small);
        }

        private void ItemRow(GuiComposer compo, double x, double y, string text, Action open, Action remove)
        {
            compo.AddFitButton(text, () => { open(); Compose(); return true; }, B(x + 16, y, PanelW - 64), EnumButtonStyle.Small);
            RemoveButton(compo, x, y, remove);
        }

        private string ItemText(ChildSpec spec, JObject item, string? key)
        {
            string text = EditorSchema.Describe(spec.NodeId, item, key);
            if (spec.NodeId == "skill") text = $"{PickLists.SpellTag(mod, item["spell"]?.ToString())} {text}";
            return text;
        }

        private void AddProblemsTab(GuiComposer compo, double x, double y)
        {
            if (session.Problems.Count == 0)
            {
                compo.AddStaticText(L("canrpgmobs:editor-no-problems", "No problems listed. Press Validate to check the draft."), EditorStyle.Body(), B(x, y, PanelW, 40));
                return;
            }
            var rows = session.Problems.Select(problem => new FormRow(1, (c, ry) =>
            {
                string path = problem.Path;
                string tail = Tail(path);
                string text = $"{problem.Severity.ToString().ToLowerInvariant()}: {(tail.Length > 0 ? tail + ": " : "")}{problem.Message}";
                c.AddFitButton(text,
                    () => { if (session.JumpTo(path)) tab = Tab.Fields; Compose(); return true; },
                    B(x, ry, PanelW), EnumButtonStyle.Small);
            })).ToList();
            DrawPaged(compo, x, y, rows, FormRows, problemPage, p => problemPage = p);
        }

        // path below the entry; match whole segments so "goblin" doesn't hit "goblins.json"
        private string Tail(string path)
        {
            string qualified = Q(session.DraftId);
            var segments = path.Split(new[] { " > " }, StringSplitOptions.None);
            int at = Array.FindIndex(segments, s => s.EndsWith(" " + qualified, StringComparison.Ordinal));
            return at < 0 ? path : string.Join(" > ", segments.Skip(at + 1));
        }

        private void DrawPaged(GuiComposer compo, double x, double y, List<FormRow> rows, int perPage, int page, Action<int> setPage)
        {
            var pages = new List<(int start, int end)>();
            int start = 0, used = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (used > 0 && used + rows[i].Rows > perPage)
                {
                    pages.Add((start, i));
                    start = i;
                    used = 0;
                }
                used += rows[i].Rows;
            }
            pages.Add((start, rows.Count));
            page = Math.Clamp(page, 0, pages.Count - 1);
            setPage(page);

            // frames first so the rows draw over them; a card cut by the page break keeps its frame
            if (rows.Any(r => r.Card != null))
            {
                bool inCard = rows.Take(pages[page].start).Any(r => r.Card != null);
                double top = y, at = y;
                for (int i = pages[page].start; i < pages[page].end; i++)
                {
                    if (rows[i].Card != null)
                    {
                        if (inCard && at > top) compo.AddInset(B(x - 3, top - 1, PanelW + 6, at - top - 2), 2);
                        inCard = true;
                        top = at;
                    }
                    at += rows[i].Rows * RowStep;
                }
                if (inCard && at > top) compo.AddInset(B(x - 3, top - 1, PanelW + 6, at - top - 2), 2);
            }

            double row = y;
            for (int i = pages[page].start; i < pages[page].end; i++)
            {
                rows[i].Draw(compo, row);
                row += rows[i].Rows * RowStep;
            }

            if (pages.Count <= 1) return;
            double py = y + perPage * RowStep + 2;
            Pager(compo, x, py, page, pages.Count, setPage);
        }

        private void TextInput(GuiComposer compo, ElementBounds bounds, string value, Action<string> onChanged)
        {
            string key = NextKey();
            compo.AddTextInput(bounds, Guard(onChanged), CairoFont.WhiteDetailText(), key);
            fills.Add((key, value, false));
        }

        private void NumberInput(GuiComposer compo, ElementBounds bounds, string value, Action<string> onChanged)
        {
            string key = NextKey();
            compo.AddNumberInput(bounds, Guard(onChanged), CairoFont.WhiteDetailText(), key);
            fills.Add((key, value, true));
        }

        private static string FieldLabel(string key) => L("canrpgmobs:editor-field-" + key, key);

        // editor-help-<node>-<key> overrides editor-help-<key>
        private static string FieldHelp(string? nodeId, string key)
        {
            if (nodeId != null && Lang.HasTranslation($"canrpgmobs:editor-help-{nodeId}-{key}", false, false)) return Lang.Get($"canrpgmobs:editor-help-{nodeId}-{key}");
            return Lang.HasTranslation("canrpgmobs:editor-help-" + key, false, false) ? Lang.Get("canrpgmobs:editor-help-" + key) : "";
        }

        private static void RenameProperty(JObject map, KeyHolder holder, string newKey)
        {
            newKey = newKey.Trim();
            if (newKey.Length == 0 || newKey == holder.Key || map.ContainsKey(newKey) || !map.ContainsKey(holder.Key)) return;
            var value = map[holder.Key];
            map.Remove(holder.Key);
            map[newKey] = value;
            holder.Key = newKey;
        }
    }
}
