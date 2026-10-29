using System;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Preview;
using canrpgmobs.Data;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // 3D preview column in simple mode. A timer picks up typing so the model updates without recomposing.
    public partial class MobEditorDialog
    {
        private const double PreviewW = 260;
        private const long PreviewDelayMs = 350;

        private MobPreviewRenderer? preview;
        private ElementBounds? previewBounds;
        private bool previewShown;
        private float previewYaw = -1.2707963f;
        private float previewZoom = 1f;
        private bool previewRotating;
        private (ResolvedMob mob, AppearancePresetModel? preset, float size)? pendingPreview;
        private long pendingPreviewMs;
        private int lastPreviewVersion = -1;
        private string? previewError;
        private long previewTickId;

        private bool PreviewWanted => page == Page.Wizard || simpleMode && page == Page.Editor && session.Draft != null && session.Section is "mobs" or "races" or "roles";

        // the wizard edits its own state, not the session draft
        private int PreviewVersion => page == Page.Wizard ? wizardVersion : session.Version;

        // hidden on small screens
        private bool PreviewFits => GuiElement.scaled(ListW + PanelW + 70 + PreviewW + 16) <= capi.Render.FrameWidth;

        private void AddPreviewColumn(GuiComposer compo, double x, double y)
        {
            previewBounds = B(x, y, PreviewW, 296);
            compo.AddInset(previewBounds, 2, 0.85f);

            var eff = Effective(out _);
            double row = y + 300;
            var presets = eff?.Appearance.presets;
            var shownPreset = presets == null || presets.Count == 0 ? null : presets.FirstOrDefault(p => p.id == simplePreset) ?? presets[0];
            bool reroll = shownPreset?.parts?.Values.Any(AppearanceParts.IsRandom) == true;
            double rerollW = reroll ? 96 : 0;
            if (presets is { Count: > 1 })
            {
                var ids = presets.Select(p => p.id).ToArray();
                compo.AddDropDown(ids, ids, Math.Max(0, Array.IndexOf(ids, simplePreset)), (code, _) => { simplePreset = code; Compose(); },
                    B(x, row, PreviewW - (reroll ? rerollW + 4 : 0)), NextKey());
            }
            // random parts: show another roll
            if (reroll)
                compo.AddSmallButton(L("canrpgmobs:editor-preview-reroll", "Reroll"), () =>
                {
                    if (preview != null) preview.PreviewSeed = capi.World.Rand.NextInt64();
                    SchedulePreview();
                    return true;
                }, B(x + PreviewW - rerollW, row, rerollW), EnumButtonStyle.Small);

            row += RowStep;
            compo.AddStaticText(L("canrpgmobs:editor-preview-hands", "In hands"), EditorStyle.FieldLabel(), B(x, row + 8, 72));
            double cx = x + 76;
            // GUI rendering skips held items, so list them here
            foreach (var code in new[] { eff?.Appearance.rightHand, eff?.Appearance.leftHand })
            {
                if (string.IsNullOrEmpty(code)) continue;
                ItemChip(compo, cx, row, code!, ItemName(code!), null);
                cx += 36;
            }
            if (cx == x + 76) compo.AddStaticText("-", EditorStyle.Body(), B(cx, row + 8, 20));

            row += 34;
            compo.AddDynamicText("", HelpFont(), B(x, row, PreviewW, 36), "previewstate");
            row += 38;
            if (page == Page.Editor)
            {
                // the saved version, the server knows nothing of the draft
                compo.AddFitText(L("canrpgmobs:editor-preview-what", "In the world: {0}", MobKeyName(previewKey)), UiKit.LabelFont(), B(x, row + 4, PreviewW));
                row += RowStep;
                AddSpawnRow(compo, x, row, PreviewW, false);
                row += RowStep;
                compo.AddSmallButton(L("canrpgmobs:editor-preview-world", "Saved version in the world"), OnPreview, B(x, row, PreviewW), EnumButtonStyle.Small);
                row += RowStep;
                AddLookRow(compo, x, row, PreviewW);
            }
            afterCompose.Add(UpdatePreviewState);
        }

        // the tick shows it once the draft stops changing
        private void SchedulePreview()
        {
            if (!previewShown)
            {
                DisposePreview();
                return;
            }
            var eff = Effective(out string? error);
            previewError = eff == null ? error ?? "?" : null;
            if (eff == null)
            {
                // drop a stale pending look
                pendingPreview = null;
                return;
            }

            var presets = eff.Appearance.presets;
            var preset = presets == null || presets.Count == 0 ? null : presets.FirstOrDefault(p => p.id == simplePreset) ?? presets[0];
            pendingPreview = (eff, preset, mod.Catalog.Tier(eff.DefaultTier)?.size ?? 1f);
            // first model immediately, later ones debounced
            pendingPreviewMs = preview == null ? 0 : capi.World.ElapsedMilliseconds;
        }

        private void OnPreviewTick(float dt)
        {
            if (!IsOpened()) return;
            FollowTyping();
            if (previewShown && (session.Draft != null || page == Page.Wizard))
            {
                // inputs edit the draft without recomposing, so watch the version
                if (PreviewVersion != lastPreviewVersion)
                {
                    lastPreviewVersion = PreviewVersion;
                    InvalidateEffective();
                    SchedulePreview();
                }
                // retry once PlayerModelLib has loaded its models
                if (preview is { WaitingForModels: true } && pendingPreview == null && preview.ModelsReady) SchedulePreview();
            }
            if (pendingPreview is not { } next || capi.World.ElapsedMilliseconds - pendingPreviewMs < PreviewDelayMs) return;
            pendingPreview = null;
            (preview ??= new MobPreviewRenderer(capi)).Show(next.mob, next.preset, next.size);
            UpdatePreviewState();
        }

        private void UpdatePreviewState()
        {
            string text = previewError != null
                ? L("canrpgmobs:editor-preview-unresolved", "Can't show this mob: {0}", previewError)
                : preview?.Problem switch
                {
                    MobPreviewRenderer.NoteModelsLoading => L("canrpgmobs:editor-preview-models", "PlayerModelLib models are still loading."),
                    MobPreviewRenderer.NoteModelMissing => L("canrpgmobs:editor-preview-nomodel", "Model not found: shown as a seraph."),
                    MobPreviewRenderer.NoteFailed => L("canrpgmobs:editor-preview-failed", "The preview failed, see client-main.log."),
                    _ => L("canrpgmobs:editor-preview-hint", "Drag to turn, mouse wheel to zoom.")
                };
            SingleComposer?.GetDynamicText("previewstate")?.SetNewText(text);
        }

        private void DisposePreview()
        {
            preview?.Dispose();
            preview = null;
            pendingPreview = null;
            lastPreviewVersion = -1;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (previewShown && previewBounds != null) preview?.Render(deltaTime, previewBounds, previewYaw, previewZoom, focused);
        }

        public override void OnMouseDown(MouseEvent args)
        {
            base.OnMouseDown(args);
            previewRotating = previewShown && previewBounds != null && previewBounds.PointInside(args.X, args.Y);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            base.OnMouseUp(args);
            previewRotating = false;
        }

        public override void OnMouseMove(MouseEvent args)
        {
            base.OnMouseMove(args);
            if (previewRotating) previewYaw -= args.DeltaX / 100f;
        }

        public override void OnMouseWheel(MouseWheelEventArgs args)
        {
            base.OnMouseWheel(args);
            if (!previewShown || previewBounds == null || !previewBounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)) return;
            previewZoom = GameMath.Clamp(previewZoom + args.deltaPrecise / 5f, 0.5f, 2f);
            args.SetHandled(true);
        }

        // depth for the 3D model, same as the character dialog
        public override float ZSize => (float)GuiElement.scaled(280);

        public override void Dispose()
        {
            DisposePreview();
            base.Dispose();
        }
    }
}
