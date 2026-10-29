using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canrpgmobs.Runtime.Appearance
{
    // Visual gear stored as item codes in the identity tree. No inventory, so no armor and no drops.
    public static class MobGear
    {
        public const string TreeKey = "gear";
        // clears inherited gear (omitting the field keeps it)
        public const string None = "none";
        private const string WornKey = "worn";
        private const string RightKey = "rh";
        private const string LeftKey = "lh";
        private const char Separator = ';';
        // default attack power of any collectible
        private const float BareHandPower = 0.5f;

        // server; preset lists override the look's
        public static void Write(ITreeAttribute identity, AppearanceModel look, AppearancePresetModel? preset)
        {
            // shape bodies can't show gear, and a hidden weapon must not affect damage
            if (look.kind != AppearanceKinds.Plm)
            {
                identity.RemoveAttribute(TreeKey);
                return;
            }
            string[] worn = Worn(look, preset);
            string right = Held(preset?.rightHand ?? look.rightHand);
            string left = Held(preset?.leftHand ?? look.leftHand);
            if (worn.Length == 0 && right.Length == 0 && left.Length == 0)
            {
                identity.RemoveAttribute(TreeKey);
                return;
            }

            var tree = new TreeAttribute();
            tree.SetString(WornKey, string.Join(Separator, worn));
            tree.SetString(RightKey, right);
            tree.SetString(LeftKey, left);
            identity[TreeKey] = tree;
        }

        internal static (string[] worn, string right, string left) Read(ITreeAttribute? identity)
        {
            var tree = identity?.GetTreeAttribute(TreeKey);
            string worn = tree?.GetString(WornKey) ?? "";
            return (worn.Split(Separator, StringSplitOptions.RemoveEmptyEntries), tree?.GetString(RightKey) ?? "", tree?.GetString(LeftKey) ?? "");
        }

        // no split, the AI reads it often
        internal static string ReadRight(ITreeAttribute? identity) => identity?.GetTreeAttribute(TreeKey)?.GetString(RightKey) ?? "";

        internal static string RawSignature(ITreeAttribute? identity)
        {
            var tree = identity?.GetTreeAttribute(TreeKey);
            return tree == null ? "" : tree.GetString(WornKey) + "|" + tree.GetString(RightKey) + "|" + tree.GetString(LeftKey);
        }

        // item first, then block
        internal static CollectibleObject? Find(IWorldAccessor world, string code)
        {
            var location = new AssetLocation(code);
            return (CollectibleObject?)world.GetItem(location) ?? world.GetBlock(location);
        }

        // null if no weapon in the right hand, then the mob's own meleeDamage/meleeTier apply
        public static WeaponStats? Weapon(IWorldAccessor world, ITreeAttribute? identity)
        {
            string code = ReadRight(identity);
            if (code.Length == 0 || Find(world, code) is not { } collectible) return null;
            var stack = new ItemStack(collectible);
            float power = collectible.GetAttackPower(stack);
            return power > BareHandPower ? new WeaponStats(code, power, collectible.ToolTier, collectible.GetAttackRange(stack)) : null;
        }

        public static bool IsNone(string? code) => string.Equals(code?.Trim(), None, StringComparison.OrdinalIgnoreCase);

        // "none" drops everything before it
        public static List<string> Effective(IEnumerable<string>? codes)
        {
            var kept = new List<string>();
            foreach (var code in codes ?? Enumerable.Empty<string>())
            {
                if (IsNone(code)) kept.Clear();
                else if (!string.IsNullOrWhiteSpace(code)) kept.Add(code);
            }
            return kept;
        }

        private static string Qualify(string? code) => string.IsNullOrWhiteSpace(code) ? "" : IdRules.Qualify(code!, "game");

        private static string Held(string? code) => IsNone(code) ? "" : Qualify(code);

        // preset gear replaces the look's unless gearMode is add
        private static string[] Worn(AppearanceModel look, AppearancePresetModel? preset)
        {
            var codes = preset?.gear == null
                ? look.gear
                : preset.gearMode == GearModes.Add
                    ? (look.gear ?? new List<string>()).Concat(preset.gear)
                    : preset.gear;
            return Effective(codes).Select(Qualify).Where(c => c.Length > 0).ToArray();
        }
    }
}
