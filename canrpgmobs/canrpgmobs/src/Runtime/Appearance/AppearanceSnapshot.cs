using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using canrpgmobs.Data;
using Vintagestory.API.Datastructures;

namespace canrpgmobs.Runtime.Appearance
{
    // Shape body look stored in the identity tree, so the client doesn't depend on its catalog.
    // PlayerModelLib bodies use PML's own attributes.
    public sealed class AppearanceSnapshot
    {
        public const string TreeKey = "app";

        private AppearanceSnapshot(string shape, float size, float hitboxWidth, float hitboxHeight, float eyeHeight,
            IReadOnlyDictionary<string, string> textures, int hash)
        {
            Shape = shape;
            Size = size;
            HitboxWidth = hitboxWidth;
            HitboxHeight = hitboxHeight;
            EyeHeight = eyeHeight;
            Textures = textures;
            Hash = hash;
        }

        public string Shape { get; }
        public float Size { get; }
        // 0 = keep entity type's hitbox
        public float HitboxWidth { get; }
        public float HitboxHeight { get; }
        // negative = keep entity type's eye height
        public float EyeHeight { get; }
        public IReadOnlyDictionary<string, string> Textures { get; }
        // FNV, same on both sides
        public int Hash { get; }

        public static void Write(ITreeAttribute identity, AppearanceModel look, AppearancePresetModel? preset, float sizeMul)
        {
            if (look.kind != AppearanceKinds.Shape || string.IsNullOrWhiteSpace(look.shape))
            {
                identity.RemoveAttribute(TreeKey);
                return;
            }

            var textures = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (look.textures != null) foreach (var pair in look.textures) textures[pair.Key] = pair.Value;
            if (preset?.textures != null) foreach (var pair in preset.textures) textures[pair.Key] = pair.Value;

            float size = (look.size > 0f ? look.size : 1f) * (sizeMul > 0f ? sizeMul : 1f);
            float width = look.hitbox is { Length: >= 2 } box ? box[0] : 0f;
            float height = look.hitbox is { Length: >= 2 } box2 ? box2[1] : 0f;
            float eye = look.eyeHeight ?? -1f;

            var tree = new TreeAttribute();
            tree.SetString("shape", look.shape!);
            tree.SetFloat("size", size);
            tree.SetFloat("hbw", width);
            tree.SetFloat("hbh", height);
            tree.SetFloat("eye", eye);
            var textureTree = new TreeAttribute();
            foreach (var pair in textures) textureTree.SetString(pair.Key, pair.Value);
            tree["tex"] = textureTree;
            tree.SetInt("ah", ComputeHash(look.shape!, size, width, height, eye, textures));
            identity[TreeKey] = tree;
        }

        public static AppearanceSnapshot? Read(ITreeAttribute? identity)
        {
            var tree = identity?.GetTreeAttribute(TreeKey);
            string? shape = tree?.GetString("shape");
            if (tree == null || string.IsNullOrWhiteSpace(shape)) return null;

            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            var textureTree = tree.GetTreeAttribute("tex");
            if (textureTree != null)
            {
                foreach (var pair in textureTree)
                {
                    string? path = textureTree.GetString(pair.Key);
                    if (!string.IsNullOrWhiteSpace(path)) textures[pair.Key] = path!;
                }
            }
            return new AppearanceSnapshot(shape!, tree.GetFloat("size", 1f), tree.GetFloat("hbw"), tree.GetFloat("hbh"),
                tree.GetFloat("eye", -1f), textures, tree.GetInt("ah"));
        }

        private static int ComputeHash(string shape, float size, float width, float height, float eye, SortedDictionary<string, string> textures)
        {
            var text = new StringBuilder(shape);
            text.Append('|').Append(size.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(width.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(height.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(eye.ToString("R", CultureInfo.InvariantCulture));
            foreach (var pair in textures) text.Append('|').Append(pair.Key).Append('=').Append(pair.Value);

            return unchecked((int)StableHash.Of(text.ToString()));
        }
    }
}
