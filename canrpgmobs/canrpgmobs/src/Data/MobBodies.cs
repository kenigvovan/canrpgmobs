using System;
using System.Collections.Generic;

namespace canrpgmobs.Data
{
    // AppearanceKind: PlayerModelLib model or plain shape file
    public sealed record MobBody(string Id, string EntityCode, string AppearanceKind, string Help = "");

    // Other mods can register their own entity types from ModSystem.Start, before assets load.
    public static class MobBodies
    {
        public const string Humanoid = "humanoid";
        public const string Creature = "creature";

        private static readonly Dictionary<string, MobBody> items = new(StringComparer.Ordinal)
        {
            [Humanoid] = new(Humanoid, "canrpgmobs:rpgmob-humanoid", AppearanceKinds.Plm, "PlayerModelLib body: wears gear, holds items, plays seraph animations"),
            [Creature] = new(Creature, "canrpgmobs:rpgmob-creature", AppearanceKinds.Shape, "Plain shape body: any creature model, no gear")
        };

        public static IReadOnlyDictionary<string, MobBody> All => items;

        public static MobBody? Get(string? id) => id != null && items.TryGetValue(id, out var body) ? body : null;

        public static string? EntityCode(string? id) => Get(id)?.EntityCode;

        // unregistered -> PlayerModelLib, the common case
        public static string AppearanceKind(string? id) => Get(id)?.AppearanceKind ?? AppearanceKinds.Plm;

        // Call from ModSystem.Start, races are validated against this list when the catalog loads.
        // Replaces a body with the same id.
        public static void Register(MobBody body)
        {
            if (string.IsNullOrWhiteSpace(body.Id)) return;
            lock (items) items[body.Id] = body;
        }
    }
}
