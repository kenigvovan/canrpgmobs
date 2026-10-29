using System;
using System.Collections.Generic;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    public partial class MobEditorDialog
    {
        private const int ItemCols = 11;
        private const int ItemRows = 6;

        private sealed class ItemPickRequest
        {
            public ItemPickRequest(string title, string[] categories, Action<string?> apply, bool allowEmpty)
            {
                Title = title;
                Categories = categories;
                Apply = apply;
                AllowEmpty = allowEmpty;
            }

            public string Title { get; }
            public string[] Categories { get; }
            // null = remove the value
            public Action<string?> Apply { get; }
            public bool AllowEmpty { get; }
        }

        private ItemPickRequest? itemPick;
        private string itemFilter = "";
        private string itemFilterDraft = "";
        private string itemCategory = "all";
        private int itemPage;
        private List<ItemEntry>? itemList;
        private string itemListKey = "";

        private bool OpenItemPick(string title, string[] categories, Action<string?> apply, bool allowEmpty)
        {
            itemPick = new ItemPickRequest(title, categories, apply, allowEmpty);
            itemList = null;
            itemFilter = itemFilterDraft = "";
            itemCategory = categories.Length > 0 ? categories[0] : "all";
            itemPage = 0;
            Compose();
            return true;
        }

        // for items:* pick lists in advanced mode
        private static string[] ItemCategoriesFor(string pickList) => pickList switch
        {
            "items:wearable" => new[] { "wearable", "all" },
            "items:hand" => new[] { "hand", "shield", "all" },
            "items:arrow" => new[] { "arrow", "all" },
            _ => new[] { "all" }
        };

        private void AddItemPickPage(GuiComposer compo, double x, double y)
        {
            var request = itemPick!;
            PanelInset(compo, x, y, 58)
                .AddFitText(L("canrpgmobs:editor-itempick-title", "Pick an item: {0}", request.Title), UiKit.SectionFont(), B(x, y + 2, PanelW - 200, 24))
                .AddSmallButton(L("canrpgmobs:editor-cancel", "Cancel"), () => { itemPick = null; Compose(); return true; }, B(x + PanelW - 96, y, 96), EnumButtonStyle.Small);
            if (request.AllowEmpty)
                compo.AddSmallButton(L("canrpgmobs:editor-itempick-clear", "Remove"), () =>
                {
                    request.Apply(null);
                    itemPick = null;
                    Compose();
                    return true;
                }, B(x + PanelW - 198, y, 96), EnumButtonStyle.Small);

            double searchW = PanelW - 90 - 194 - 8;
            SearchRow(compo, x, y + 30, searchW, itemFilterDraft, v => itemFilterDraft = v,
                L("canrpgmobs:editor-itempick-search", "Search"), x + searchW + 4, 90,
                () => { itemFilter = itemFilterDraft.Trim().ToLowerInvariant(); itemPage = 0; });
            var categories = request.Categories;
            compo.AddDropDown(categories, categories.Select(CategoryName).ToArray(), Math.Max(0, Array.IndexOf(categories, itemCategory)),
                (code, _) => { itemCategory = code; itemPage = 0; Compose(); }, B(x + PanelW - 194, y + 30, 194), NextKey());

            // refilter only when category or search changes
            string listKey = itemCategory + "\n" + itemFilter;
            if (itemList == null || listKey != itemListKey)
            {
                var source = itemCategory == "block" ? ItemCatalog.Blocks(capi) : ItemCatalog.Items(capi);
                itemList = source.Where(e => MatchesCategory(e, itemCategory) && (itemFilter.Length == 0 || e.Search.Contains(itemFilter))).ToList();
                itemListKey = listKey;
            }
            var items = itemList;
            int perPage = ItemCols * ItemRows;
            int pages = Math.Max(1, (items.Count + perPage - 1) / perPage);
            itemPage = Math.Clamp(itemPage, 0, pages - 1);
            var pageItems = items.Skip(itemPage * perPage).Take(perPage).ToList();

            double cell = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
            double gridY = y + 66;
            if (pageItems.Count == 0)
                compo.AddStaticText(L("canrpgmobs:editor-itempick-empty", "Nothing found."), EditorStyle.Body(), B(x, gridY + 4, PanelW, 30));
            else
            {
                var skillItems = pageItems.Select(e => new SkillItem { Code = new AssetLocation(e.Code), Name = e.Name, RenderHandler = IconRenderer(e.Slot) }).ToList();
                compo.AddSkillItemGrid(skillItems, ItemCols, ItemRows, index =>
                {
                    if (index < 0 || index >= pageItems.Count) return;
                    string code = pageItems[index].Code;
                    // grid is still inside its mouse handler, recompose afterwards
                    capi.Event.EnqueueMainThreadTask(() =>
                    {
                        if (!IsOpened() || itemPick != request) return;
                        request.Apply(code);
                        itemPick = null;
                        Compose();
                    }, "canrpgmobs-itempick");
                }, B(x, gridY, ItemCols * cell, ItemRows * cell), "itemgrid");
            }

            double below = gridY + ItemRows * cell + 4;
            compo.AddDynamicText("", EditorStyle.Body(), B(x, below, PanelW, 22), "itemname");
            Pager(compo, x, below + 24, itemPage, pages, p => itemPage = p, items.Count);

            int last = -1;
            afterCompose.Add(() =>
            {
                var grid = SingleComposer.GetSkillItemGrid("itemgrid");
                if (grid == null) return;
                grid.OnSlotOver = index =>
                {
                    if (index == last || index < 0 || index >= pageItems.Count) return;
                    last = index;
                    SingleComposer.GetDynamicText("itemname")?.SetNewText($"{pageItems[index].Name}  ({pageItems[index].Code})");
                };
            });
        }

        private RenderSkillItemDelegate IconRenderer(ItemSlot slot) => (code, dt, posX, posY) =>
        {
            double size = GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize - 5.0);
            capi.Render.RenderItemstackToGui(slot, posX + size / 2, posY + size / 2, 100, (float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize), -1, true, false, false);
        };

        private static bool MatchesCategory(ItemEntry entry, string category)
        {
            if (category.StartsWith("slot:", StringComparison.Ordinal)) return entry.GearSlot == category.Substring(5);
            return category switch
            {
                "wearable" => entry.Wearable,
                "hand" => entry.Weapon,
                "shield" => entry.Tool == "shield",
                "arrow" => entry.Arrow,
                "block" => entry.IsBlock,
                _ => true
            };
        }

        private static string CategoryName(string category)
            => category.StartsWith("slot:", StringComparison.Ordinal)
                ? L("canrpgmobs:editor-itemcat-slot", "Slot: {0}", L("canrpgmobs:editor-slot-" + category.Substring(5), category.Substring(5)))
                : L("canrpgmobs:editor-itemcat-" + category, category);

        private void ItemChip(GuiComposer compo, double x, double y, string code, string hover, Action? remove)
        {
            var entry = ItemCatalog.Find(capi, code);
            compo.AddInset(B(x, y, 30, 30), 1, 0.7f);
            if (entry != null)
            {
                var slot = entry.Slot;
                compo.AddCustomRender(B(x, y, 30, 30), (dt, bounds) =>
                    capi.Render.RenderItemstackToGui(slot, bounds.renderX + bounds.InnerWidth / 2, bounds.renderY + bounds.InnerHeight / 2, 100,
                        (float)GuiElement.scaled(22), -1, true, false, false));
            }
            else compo.AddStaticText("?", EditorStyle.Status(true), B(x + 11, y + 6, 12));
            compo.AddHoverText(hover, CairoFont.WhiteDetailText(), 320, B(x, y, 30, 30));
            if (remove != null)
                compo.AddIcon(UiKit.IconCross, L("canrpgmobs:editor-tip-remove", "Remove"), () => { remove(); Compose(); return true; }, B(x + 32, y + 4, 20, 20));
        }

        private string ItemName(string code) => ItemCatalog.Find(capi, code)?.Name ?? code;
    }
}
