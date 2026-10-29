using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Appearance
{
    // Client, humanoid bodies. Adds worn items to the shape after PlayerModelLib builds it.
    // Must come after canrpgmobs:skin, the skin replaces the whole shape in its tesselation.
    public class EBMobGear : EntityBehavior
    {
        public const string Name = "canrpgmobs:gear";

        private ICoreClientAPI? capi;
        private string loaded = "";
        private ItemStack[] worn = Array.Empty<ItemStack>();
        // read from the renderer worker thread, so swap the whole dict, never mutate
        private volatile IReadOnlyDictionary<string, int> textures = new Dictionary<string, int>();
        private GearTextureSource? wrapper;
        private HashSet<string>? warnedItems;

        public EBMobGear(Entity entity) : base(entity) { }
        public override string PropertyName() => Name;

        public ItemSlot? RightHand { get; private set; }
        public ItemSlot? LeftHand { get; private set; }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            capi = entity.Api as ICoreClientAPI;
            Load();
            entity.WatchedAttributes.RegisterModifiedListener(MobKeys.Tree, OnIdentityChanged);
        }

        public override void OnTesselation(ref Shape entityShape, string shapePathForLogging, ref bool shapeIsCloned, ref string[] willDeleteElements)
        {
            base.OnTesselation(ref entityShape, shapePathForLogging, ref shapeIsCloned, ref willDeleteElements);
            var items = worn;
            if (capi == null || entityShape == null || items.Length == 0)
            {
                // stale gear textures would shadow the skin's
                if (textures.Count > 0) textures = new Dictionary<string, int>();
                return;
            }
            if (!shapeIsCloned)
            {
                entityShape = entityShape.Clone();
                shapeIsCloned = true;
            }
            willDeleteElements ??= Array.Empty<string>();

            // vanilla gear attach, textures go into the entity atlas
            var collected = new Dictionary<string, CompositeTexture>(StringComparer.Ordinal);
            foreach (var stack in items)
            {
                var attachable = IAttachableToEntity.FromCollectible(stack.Collectible);
                if (attachable == null) continue;
                try
                {
                    entityShape = EntityBehaviorContainer.addGearToShape(capi, entity, capi.EntityTextureAtlas, entityShape, stack,
                        attachable, "default", shapePathForLogging, ref willDeleteElements, collected);
                }
                catch (Exception e)
                {
                    // warn once per item
                    if ((warnedItems ??= new HashSet<string>()).Add(stack.Collectible.Code.ToString()))
                        capi.Logger.Warning("[canrpgmobs] could not put {0} on {1}: {2}", stack.Collectible.Code, entity.Code, e.Message);
                }
            }
            var next = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in collected)
                if (pair.Value.Baked != null) next[pair.Key] = pair.Value.Baked.TextureSubId;
            textures = next;
        }

        // gear textures take priority over the skin's
        public ITexPositionSource Wrap(ITexPositionSource skin)
        {
            var current = wrapper;
            if (current == null || !ReferenceEquals(current.Inner, skin)) wrapper = current = new GearTextureSource(this, skin);
            return current;
        }

        private TextureAtlasPosition? Position(string textureCode)
            => capi != null && textures.TryGetValue(textureCode, out int subId) ? capi.EntityTextureAtlas.Positions[subId] : null;

        private void OnIdentityChanged()
        {
            if (Load()) entity.MarkShapeModified();
        }

        // false if unchanged, avoids re-tesselating on unrelated identity updates
        private bool Load()
        {
            var identity = MobKeys.Identity(entity);
            string signature = MobGear.RawSignature(identity);
            if (signature == loaded) return false;
            loaded = signature;
            var (codes, right, left) = MobGear.Read(identity);

            worn = codes.Select(Stack).OfType<ItemStack>().ToArray();
            RightHand = Stack(right) is { } rightStack ? new DummySlot(rightStack) : null;
            LeftHand = Stack(left) is { } leftStack ? new DummySlot(leftStack) : null;
            return true;
        }

        // unknown codes are reported by the server catalog
        private ItemStack? Stack(string code)
            => string.IsNullOrEmpty(code) || MobGear.Find(entity.World, code) is not { } collectible ? null : new ItemStack(collectible);

        private sealed class GearTextureSource : ITexPositionSource
        {
            private readonly EBMobGear gear;

            public GearTextureSource(EBMobGear gear, ITexPositionSource inner)
            {
                this.gear = gear;
                Inner = inner;
            }

            public ITexPositionSource Inner { get; }
            public Size2i AtlasSize => Inner.AtlasSize;
            public TextureAtlasPosition this[string textureCode] => gear.Position(textureCode) ?? Inner[textureCode];
        }
    }
}
