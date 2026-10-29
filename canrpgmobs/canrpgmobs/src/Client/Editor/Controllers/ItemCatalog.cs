using System;
using System.Collections.Generic;
using canrpgmobs.Data;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace canrpgmobs.Client.Editor.Controllers
{
    public sealed class ItemEntry
    {
        public ItemEntry(string code, string name, ItemSlot slot, bool wearable, string? gearSlot, string? tool, bool weapon, bool arrow, float attackPower, bool isBlock)
        {
            Code = code;
            Name = name;
            Search = (name + " " + code).ToLowerInvariant();
            Slot = slot;
            Wearable = wearable;
            GearSlot = gearSlot;
            Tool = tool;
            Weapon = weapon;
            Arrow = arrow;
            AttackPower = attackPower;
            IsBlock = isBlock;
        }

        // with domain, game:blade-falx-iron
        public string Code { get; }
        public string Name { get; }
        public string Search { get; }
        // for icon rendering
        public ItemSlot Slot { get; }
        public bool Wearable { get; }
        // one of GearSlots
        public string? GearSlot { get; }
        public string? Tool { get; }
        // a tool or anything that hits harder than a fist
        public bool Weapon { get; }
        public bool Arrow { get; }
        public float AttackPower { get; }
        public bool IsBlock { get; }
    }

    // Built once per world. The block list is huge, so it's only built when a picker needs it.
    public static class ItemCatalog
    {
        public static readonly string[] GearSlots = { "head", "body", "legs", "feet", "shoulder", "hands", "waist" };

        private static List<ItemEntry>? items;
        private static Dictionary<string, ItemEntry>? itemsByCode;
        private static List<ItemEntry>? blocks;
        private static readonly Dictionary<string, ItemEntry?> blocksByCode = new(StringComparer.Ordinal);

        public static IReadOnlyList<ItemEntry> Items(ICoreClientAPI capi)
        {
            BuildItems(capi);
            return items!;
        }

        public static IReadOnlyList<ItemEntry> Blocks(ICoreClientAPI capi)
        {
            if (blocks is { Count: > 0 }) return blocks;
            var list = new List<ItemEntry>();
            foreach (var block in capi.World.Blocks)
            {
                // skip air and blocks not in creative tabs, naming all variants freezes the dialog.
                // hand-typed codes still resolve via FindBlock
                if (block?.Code == null || block.Id == 0 || block.CreativeInventoryTabs is not { Length: > 0 }) continue;
                if (Describe(block, true) is not { } entry) continue;
                list.Add(entry);
                blocksByCode[entry.Code] = entry;
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            blocks = list;
            return blocks;
        }

        // entries hold stacks from the current world, drop them on world change
        public static void Clear()
        {
            items = null;
            itemsByCode = null;
            blocks = null;
            blocksByCode.Clear();
        }

        // no domain = game, like in content files
        public static ItemEntry? Find(ICoreClientAPI capi, string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            BuildItems(capi);
            return itemsByCode!.TryGetValue(IdRules.Qualify(code!.Trim(), "game"), out var entry) ? entry : null;
        }

        // loot entries say which list the code is from
        public static ItemEntry? Find(ICoreClientAPI capi, string? code, bool block) => block ? FindBlock(capi, code) : Find(capi, code);

        // single lookup, doesn't build the whole block list
        public static ItemEntry? FindBlock(ICoreClientAPI capi, string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            string qualified = IdRules.Qualify(code!.Trim(), "game");
            if (blocksByCode.TryGetValue(qualified, out var known)) return known;
            var block = capi.World.GetBlock(new AssetLocation(qualified));
            var entry = block == null || block.Id == 0 ? null : Describe(block, true);
            blocksByCode[qualified] = entry;
            return entry;
        }

        private static void BuildItems(ICoreClientAPI capi)
        {
            if (items is { Count: > 0 }) return;
            var list = new List<ItemEntry>();
            var map = new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
            foreach (var item in capi.World.Items)
            {
                if (item?.Code == null || Describe(item, false) is not { } entry) continue;
                list.Add(entry);
                map[entry.Code] = entry;
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            items = list;
            itemsByCode = map;
        }

        // null if the item throws, it just won't show up in pickers
        private static ItemEntry? Describe(CollectibleObject collectible, bool isBlock)
        {
            try
            {
                var stack = new ItemStack(collectible);
                string path = collectible.Code.Path;
                string? category = null;
                if (!isBlock)
                {
                    var attachable = IAttachableToEntity.FromCollectible(collectible);
                    // some attachables throw without their attributes, treat as not wearable
                    try { category = attachable?.GetCategoryCode(stack); }
                    catch (Exception) { category = null; }
                }
                float power = collectible.GetAttackPower(stack);
                string? tool = collectible.Tool?.ToString().ToLowerInvariant() ?? (!isBlock && path.StartsWith("shield", StringComparison.Ordinal) ? "shield" : null);
                return new ItemEntry(collectible.Code.ToString(), stack.GetName(), new DummySlot(stack), category != null, category == null ? null : SlotFor(category),
                    tool, !isBlock && (tool != null || power > 0.5f), !isBlock && path.StartsWith("arrow-", StringComparison.Ordinal), power, isBlock);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // clothes categories / armor parts -> editor slots
        private static string SlotFor(string category)
        {
            string c = category.ToLowerInvariant();
            if (c.Contains("head") || c == "face") return "head";
            if (c.Contains("lowerbody") || c.Contains("leg")) return "legs";
            if (c.Contains("body")) return "body";
            if (c.Contains("foot") || c.Contains("feet")) return "feet";
            if (c.Contains("hand") || c == "arm") return "hands";
            if (c.Contains("waist")) return "waist";
            return "shoulder";
        }
    }
}
