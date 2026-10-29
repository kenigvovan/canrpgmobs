using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace canrpgmobs
{
    // WatchedAttributes keys
    public static class MobKeys
    {
        public const int CurrentFormatVersion = Data.FormatMigrator.CurrentVersion;

        // identity tree, written before spawn
        public const string Tree = "canrpgmobs";
        public const string FormatVersion = "fv";
        public const string Key = "key";
        public const string Level = "lvl";
        public const string Seed = "seed";
        public const string Preset = "preset";
        public const string FallbackName = "name";
        public const string Tier = "tier";
        public const string Preview = "preview";
        // follows the archetype unless FactionOverride is set
        public const string Faction = "fac";
        public const string FactionOverride = "facov";
        // leash anchor, internal coords (Y includes the dimension)
        public const string AnchorX = "ax";
        public const string AnchorY = "ay";
        public const string AnchorZ = "az";
        // boss phases entered this life
        public const string Phase = "phase";
        // summoner's entity id
        public const string Owner = "owner";
        public const string NoLoot = "noloot";
        // zone that spawned it
        public const string Zone = "zone";

        // PlayerModelLib keys, on the entity root
        public const string PlmModel = "skinModel";
        public const string PlmSize = "entitySize";
        public const string PlmSkinConfig = "skinConfig";
        public const string PlmAppliedParts = "appliedParts";

        // null for anything that isn't our mob
        public static ITreeAttribute? Identity(Entity entity) => entity.WatchedAttributes.GetTreeAttribute(Tree);
    }
}
