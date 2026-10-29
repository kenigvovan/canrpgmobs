using System;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Runtime.Audio;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    public partial class MobEditorDialog
    {
        private const int PickRows = 12;

        private sealed class PickRequest
        {
            public PickRequest(string list, string title, string? context, Action<string> apply)
            {
                List = list;
                Title = title;
                Context = context;
                Apply = apply;
            }

            public string List { get; }
            public string Title { get; }
            public string? Context { get; }
            public Action<string> Apply { get; }
        }

        private PickRequest? pick;
        private string pickFilter = "";
        private string pickFilterDraft = "";
        private int pickPage;

        private bool OpenPick(string list, string title, string? context, Action<string> apply)
        {
            pick = new PickRequest(list, title, context, apply);
            pickFilter = pickFilterDraft = "";
            pickPage = 0;
            Compose();
            return true;
        }

        private void AddPickPage(GuiComposer compo, double x, double y)
        {
            var request = pick!;
            PanelInset(compo, x, y, 58)
                .AddStaticText(L("canrpgmobs:editor-pick-title", "Pick: {0}", request.Title), UiKit.SectionFont(), B(x, y + 2, PanelW, 24));

            SearchRow(compo, x, y + 30, PanelW - 190, pickFilterDraft, v => pickFilterDraft = v,
                    L("canrpgmobs:editor-filter", "Filter"), x + PanelW - 184, 90, () => { pickFilter = pickFilterDraft.Trim(); pickPage = 0; })
                .AddSmallButton(L("canrpgmobs:editor-cancel", "Cancel"), () => { pick = null; Compose(); return true; },
                    B(x + PanelW - 88, y + 30, 88), EnumButtonStyle.Small);

            var options = PickLists.Get(request.List, capi, mod, session, request.Context)
                .Where(o => pickFilter.Length == 0 || o.Label.IndexOf(pickFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            int pages = Math.Max(1, (options.Count + PickRows - 1) / PickRows);
            pickPage = Math.Clamp(pickPage, 0, pages - 1);

            double row = y + 66;
            if (options.Count == 0)
                compo.AddStaticText(L("canrpgmobs:editor-pick-empty", "Nothing to pick here; type the value into the field instead."), EditorStyle.Body(), B(x, row, PanelW, 40));
            foreach (var option in options.Skip(pickPage * PickRows).Take(PickRows))
            {
                string value = option.Value;
                compo.AddFitButton(option.Label, () =>
                {
                    request.Apply(value);
                    pick = null;
                    Compose();
                    return true;
                }, B(x, row, PanelW), EnumButtonStyle.Small);
                row += RowH + 4;
            }

            double py = y + 66 + PickRows * (RowH + 4) + 2;
            Pager(compo, x, py, pickPage, pages, p => pickPage = p, options.Count);
        }

        // trailing * picks a random variant, like the mob would
        private bool PlaySound(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            var location = MobAudio.Location(path);
            if (location.Path.EndsWith("*"))
            {
                var variants = capi.Assets.GetMany(location.Path.TrimEnd('*'), location.Domain, false);
                if (variants.Count == 0) return true;
                location = variants[capi.World.Rand.Next(variants.Count)].Location;
            }
            else location = location.WithPathAppendixOnce(".ogg");
            capi.Gui.PlaySound(location, false, 1f);
            return true;
        }
    }
}
