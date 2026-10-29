using System;
using System.Globalization;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Appearance;
using canrpgmobs.Runtime.Spawning;
using Newtonsoft.Json;
using PlayerModelLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canrpgmobs.Client.Editor.Preview
{
    // Client-only entity that is never spawned, dressed via MobSpawner.WriteAppearance and drawn like the character screen.
    // Each change builds a new entity; the old one keeps drawing until the new mesh is ready to avoid flicker.
    public sealed class MobPreviewRenderer : IDisposable
    {
        public const string NoteModelsLoading = "models";
        public const string NoteModelMissing = "nomodel";
        public const string NoteFailed = "failed";

        private sealed class PreviewShapeRenderer : EntityShapeRenderer
        {
            private const long TesselateIntervalMs = 250;

            private bool disposed;
            private long lastTesselateMs = -TesselateIntervalMs;

            public PreviewShapeRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
            {
                DoRenderHeldItem = false;
                // vanilla polls climate every 5s for frost, which the GUI shader never uses anyway
                // (frostAlpha is set in DoRender3DOpaqueBatched, not called for GUI models)
                frostable = false;
                frostAlpha = targetFrostAlpha = 0f;
                getFrostAlpha = () => 0f;
            }

            public bool HasMesh => meshRefOpaque != null;

            // RenderToGui calls this every frame until the async mesh arrives, throttle it
            public override void TesselateShape()
            {
                long now = capi.World.ElapsedMilliseconds;
                if (disposed || now - lastTesselateMs < TesselateIntervalMs) return;
                lastTesselateMs = now;
                base.TesselateShape();
            }

            // late mesh after dispose would leak
            protected override void onMeshReady(MeshData meshData)
            {
                if (disposed) return;
                base.onMeshReady(meshData);
            }

            public override void Dispose()
            {
                disposed = true;
                base.Dispose();
            }
        }

        private readonly ICoreClientAPI capi;
        private readonly Matrixf lightMatrix = new();
        private readonly Vec4f lightDirection = new Vec4f(-1f, -1f, 0f, 0f).NormalizeXYZ();

        private EntityAgent? entity;
        private PreviewShapeRenderer? renderer;
        private EntityAgent? previous;
        private PreviewShapeRenderer? previousRenderer;
        private string signature = "";

        public MobPreviewRenderer(ICoreClientAPI capi) => this.capi = capi;

        // one of the Note* constants
        public string? Problem { get; private set; }

        public bool WaitingForModels => Problem == NoteModelsLoading;

        public bool ModelsReady => capi.ModLoader.GetModSystem<CustomModelsSystem>()?.ModelsLoaded != false;

        // picks the random parts; the dialog rerolls it
        public long PreviewSeed { get; set; }

        public void Show(ResolvedMob mob, AppearancePresetModel? preset, float sizeMul)
        {
            string next = string.Join("|", mob.Key, mob.Race.body, JsonConvert.SerializeObject(mob.Appearance),
                preset == null ? "" : JsonConvert.SerializeObject(preset), sizeMul.ToString(CultureInfo.InvariantCulture), PreviewSeed);
            if (next == signature) return;

            Problem = null;
            var models = capi.ModLoader.GetModSystem<CustomModelsSystem>();
            if (mob.Appearance.kind == AppearanceKinds.Plm && models != null)
            {
                if (!models.ModelsLoaded)
                {
                    // building a skin now would subscribe to the models event forever; the dialog retries
                    Problem = NoteModelsLoading;
                    return;
                }
                if (mob.Appearance.model != AppearanceModel.Seraph && !models.CustomModels.ContainsKey(mob.Appearance.model)) Problem = NoteModelMissing;
            }

            signature = next;
            try
            {
                Build(mob, preset, sizeMul);
            }
            catch (Exception e)
            {
                Problem = NoteFailed;
                capi.Logger.Warning("[canrpgmobs] editor preview of {0} failed: {1}", mob.Key, e);
            }
        }

        private void Build(ResolvedMob mob, AppearancePresetModel? preset, float sizeMul)
        {
            string? bodyCode = MobBodies.EntityCode(mob.Race.body);
            var type = bodyCode == null ? null : capi.World.GetEntityType(new AssetLocation(bodyCode));
            if (type == null || capi.World.ClassRegistry.CreateEntity(type) is not EntityAgent agent)
            {
                Problem = NoteFailed;
                return;
            }

            // appearance behaviors read this, must be set before Initialize
            var identity = new TreeAttribute();
            identity.SetInt(MobKeys.FormatVersion, MobKeys.CurrentFormatVersion);
            identity.SetString(MobKeys.Key, mob.Key);
            identity.SetInt(MobKeys.Level, 1);
            identity.SetBool(MobKeys.Preview, true);
            identity.SetString(MobKeys.Preset, preset?.id ?? "");
            agent.WatchedAttributes[MobKeys.Tree] = identity;
            identity.SetLong(MobKeys.Seed, PreviewSeed);
            MobSpawner.WriteAppearance(capi, agent, mob.Appearance, preset, sizeMul);

            // climate/light lookups need a loaded position
            agent.Pos.SetPos(capi.World.Player.Entity.Pos.XYZ);
            PreviewShapeRenderer? shapeRenderer = null;
            try
            {
                // cloned, the appearance behavior modifies textures/size on the properties
                agent.Initialize(type.Clone(), capi, 0);
                agent.AfterInitialized(false);

                shapeRenderer = new PreviewShapeRenderer(agent, capi);
                agent.Properties.Client.Renderer = shapeRenderer;
                agent.OnEntityLoaded();
                shapeRenderer.TesselateShape();
            }
            catch
            {
                // half-built entity still holds tick listeners and subscriptions
                Destroy(agent, shapeRenderer);
                throw;
            }

            Destroy(previous, previousRenderer);
            previous = entity;
            previousRenderer = renderer;
            entity = agent;
            renderer = shapeRenderer;
        }

        // zoom 1 = fits the height
        public void Render(float dt, ElementBounds bounds, float yaw, float zoom, bool focused)
        {
            if (renderer?.HasMesh == true && previous != null)
            {
                Destroy(previous, previousRenderer);
                previous = null;
                previousRenderer = null;
            }
            var drawn = renderer?.HasMesh == true || previous == null ? entity : previous;
            if (drawn == null) return;

            var render = capi.Render;
            render.GlPushMatrix();
            if (focused) render.GlTranslate(0f, 0f, 150f);
            render.GlRotate(-14f, 1f, 0f, 0f);
            lightMatrix.Identity();
            lightMatrix.RotateXDeg(-14f);
            var light = lightMatrix.TransformVector(lightDirection);
            render.CurrentActiveShader.Uniform("lightPosition", light.X, light.Y, light.Z);
            render.PushScissor(bounds, false);

            // GUI model matrix puts the feet at (posX + size, posY + 2*size), size = pixels per block
            float height = Math.Max(0.6f, drawn.SelectionBox.Y2);
            double size = bounds.InnerHeight * 0.8 * zoom / height;
            double posX = bounds.renderX + bounds.InnerWidth / 2 - size;
            double posY = bounds.renderY + bounds.InnerHeight * 0.92 - 2 * size;
            render.RenderEntityToGui(dt, drawn, posX, posY, GuiElement.scaled(230), yaw, (float)size, -1);

            render.PopScissor();
            render.CurrentActiveShader.Uniform("lightPosition", 0.7071068f, -0.7071068f, 0f);
            render.GlPopMatrix();
        }

        public void Dispose()
        {
            Destroy(previous, previousRenderer);
            Destroy(entity, renderer);
            previous = entity = null;
            previousRenderer = renderer = null;
            signature = "";
        }

        private static void Destroy(EntityAgent? agent, EntityShapeRenderer? shapeRenderer)
        {
            try { shapeRenderer?.Dispose(); }
            catch (Exception) { /* GL context already gone */ }
            if (agent == null) return;
            try
            {
                agent.Properties.Client.Renderer = null;
                agent.OnEntityDespawn(new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
            catch (Exception) { /* never spawned, ignore */ }
        }
    }
}
