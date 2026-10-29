using System.Collections.Concurrent;
using canrpgmobs.Runtime.Audio;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Appearance
{
    // Both sides. Anim/sound sets, plus shape/textures/hitbox for shape bodies.
    // Must come before canrpgmobs:skin (PlayerModelLib replaces the shape after) and canrpgmobs:mob (AI reads the anim list).
    public class EBMobAppearance : EntityBehavior
    {
        public const string Name = "canrpgmobs:appearance";

        // shared per side, tesselation clones them
        private static readonly ConcurrentDictionary<string, Shape?> ServerShapes = new();
        private static readonly ConcurrentDictionary<string, Shape?> ClientShapes = new();

        private AppearanceSnapshot? snapshot;

        public EBMobAppearance(Entity entity) : base(entity) { }
        public override string PropertyName() => Name;

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);

            ReapplySets();

            snapshot = AppearanceSnapshot.Read(MobKeys.Identity(entity));
            if (snapshot != null) ApplyBody(snapshot);

            if (entity.Api.Side == EnumAppSide.Client)
                entity.WatchedAttributes.RegisterModifiedListener(MobKeys.Tree, OnIdentityChanged);
        }

        // also called on catalog change
        public void ReapplySets()
        {
            string? key = MobKeys.Identity(entity)?.GetString(MobKeys.Key);
            var mob = key == null ? null : canrpgmobsModSystem.For(entity.Api)?.Catalog.Resolve(key);
            if (mob == null) return;
            MobAnimations.Apply(entity, mob.Anims);
            MobAudio.ApplyEntitySounds(entity, mob);
        }

        public override void AfterInitialized(bool onFirstSpawn)
        {
            base.AfterInitialized(onFirstSpawn);
            UseShape();
        }

        public override void OnTesselation(ref Shape entityShape, string shapePathForLogging, ref bool shapeIsCloned, ref string[] willDeleteElements)
        {
            base.OnTesselation(ref entityShape, shapePathForLogging, ref shapeIsCloned, ref willDeleteElements);
            if (snapshot == null) return;
            var shape = UseShape();
            if (shape == null) return;
            // cloned flag forces a fresh animator instead of the type's cached one
            entityShape = shape.Clone();
            shapeIsCloned = true;
        }

        private void OnIdentityChanged()
        {
            var fresh = AppearanceSnapshot.Read(MobKeys.Identity(entity));
            if (fresh?.Hash == snapshot?.Hash) return;
            snapshot = fresh;
            if (snapshot != null) ApplyBody(snapshot);
            entity.MarkShapeModified();
        }

        private void ApplyBody(AppearanceSnapshot look)
        {
            var properties = entity.Properties;
            float size = look.Size > 0f ? look.Size : 1f;

            if (look.HitboxWidth > 0f && look.HitboxHeight > 0f)
            {
                properties.CollisionBoxSize = new Vec2f(look.HitboxWidth * size, look.HitboxHeight * size);
                properties.SelectionBoxSize = new Vec2f(look.HitboxWidth * size, look.HitboxHeight * size);
                entity.SetCollisionBox(properties.CollisionBoxSize.X, properties.CollisionBoxSize.Y);
                entity.SetSelectionBox(properties.SelectionBoxSize.X, properties.SelectionBoxSize.Y);
            }
            if (look.EyeHeight >= 0f)
            {
                properties.EyeHeight = look.EyeHeight * size;
                entity.LocalEyePos.Y = properties.EyeHeight;
            }
            if (properties.Client != null) properties.Client.Size = size;
            if (entity.Api is ICoreClientAPI capi) ApplyTextures(capi, look);
        }

        // bake and insert into the atlas ourselves, like vanilla shape overlays
        private void ApplyTextures(ICoreClientAPI capi, AppearanceSnapshot look)
        {
            var textures = entity.Properties.Client?.Textures;
            if (textures == null) return;
            foreach (var pair in look.Textures)
            {
                var texture = new CompositeTexture(AssetLocation.Create(pair.Value, "game"));
                texture.Bake(capi.Assets);
                capi.EntityTextureAtlas.GetOrInsertTexture(texture.Baked.TextureFilenames[0], out int subId, out _, null, 0f);
                texture.Baked.TextureSubId = subId;
                textures[pair.Key] = texture;
            }
        }

        // both sides need it: the client resolves synced anims through it, the server animator runs them
        private Shape? UseShape()
        {
            if (snapshot == null || entity.Properties.Client == null) return null;
            var shape = LoadShape(snapshot.Shape);
            if (shape == null || ReferenceEquals(entity.Properties.Client.LoadedShapeForEntity, shape)) return shape;

            entity.Properties.Client.LoadedShapeForEntity = shape;
            if (entity.Api.Side == EnumAppSide.Server)
                entity.AnimManager.LoadAnimator(entity.Api, entity, shape, null, false, null, "head");
            return shape;
        }

        private Shape? LoadShape(string path)
        {
            var cache = entity.Api.Side == EnumAppSide.Server ? ServerShapes : ClientShapes;
            return cache.GetOrAdd(path, p =>
            {
                var location = AssetLocation.Create(p, "game").WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                var shape = Shape.TryGet(entity.Api, location);
                if (shape == null)
                {
                    entity.Api.Logger.Warning("[canrpgmobs] shape '{0}' not found, mob stays with its entity type's shape", p);
                    return null;
                }
                shape.ResolveReferences(entity.Api.Logger, location.ToString());
                shape.InitForAnimations(entity.Api.Logger, location.ToString(), "head");
                return shape;
            });
        }
    }
}
