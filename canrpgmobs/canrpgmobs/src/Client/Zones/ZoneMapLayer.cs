using System;
using System.Collections.Generic;
using System.Text;
using Cairo;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Spawning;
using canrpgmobs.Runtime.Zones;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs.Client.Zones
{
    // Admins only. In markup mode the selected zone is edited with the mouse:
    // drag a corner, drag an edge's middle to add one, right click to add or remove.
    public class ZoneMapLayer : MapLayer
    {
        public const string Code = "canrpgmobs-zones";
        private const int MaxFillPixels = 512;
        private const float FillAlpha = 0.22f;
        private const double GrabPx = 9;
        private const int SnapGrid = 8;
        private const long DraftFillMs = 150;

        private sealed class Drawn
        {
            public ZoneShape Shape = null!;
            public Vec4f Color = new();
            public LoadedTexture? Fill;
            public LoadedTexture? Label;
            public Vec3d LabelAt = new();
        }

        private readonly ICoreClientAPI? capi;
        private readonly List<Drawn> drawn = new();
        private Drawn? draftDrawn;
        private int draftBuiltVersion = -1;
        private long draftBuiltMs;
        private MeshRef? quad;
        private int builtVersion = -1;
        private bool allowed;
        private long allowedCheckMs;

        private int hoverCorner = -1, hoverMid = -1, dragCorner = -1;
        private bool leftWasDown;
        private int lastX, lastY, pressX, pressY;
        private bool moved;

        private readonly Matrixf mv = new();
        private Vec2f a = new(), b = new();
        private readonly Vec3d world = new();
        private readonly Vec4f color = new();
        private static readonly Vec4f Bad = new(1f, 0.25f, 0.2f, 1f);
        private static readonly Vec4f White = new(1f, 1f, 1f, 1f);
        private static readonly Vec4f Grabbed = new(1f, 0.85f, 0.2f, 1f);

        public override string Title => "Mob zones";
        public override string LayerGroupCode => Code;
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
        public override bool RequireChunkLoaded => false;

        public ZoneMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
        }

        private canrpgmobsModSystem? Mod => capi == null ? null : canrpgmobsModSystem.For(capi);
        private ZoneDraft? Draft => Mod?.ZoneDraft;

        // under players and waypoints, and first to get clicks; can't move it while OnLoaded walks the list
        public override void OnLoaded()
        {
            if (capi == null || mapSink is not WorldMapManager manager) return;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                if (!manager.MapLayers.Remove(this)) return;
                manager.MapLayers.Insert(Math.Min(1, manager.MapLayers.Count), this);
            }, "canrpgmobs-zonelayer");
        }

        public override void Render(GuiElementMap map, float dt)
        {
            if (capi == null || !Active || Mod is not { } mod) return;
            bool admin = Allowed();
            if (!admin && !mod.ZonesOnMapForPlayers) return;
            if (builtVersion != mod.CatalogVersion) Rebuild(mod);
            var draft = admin ? mod.ZoneDraft : null;
            UpdateDraftDrawn(mod, draft);
            quad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

            int dim = capi.World.Player.Entity.Pos.Dimension;
            var prog = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
            capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
            prog.Uniform("applyColor", 0);
            prog.Uniform("extraGlow", 0);
            prog.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

            foreach (var d in drawn)
            {
                if (d.Shape.Model.dimension != dim || IsDraft(draft, d.Shape.Model.id) || (!admin && !d.Shape.Model.enabled)) continue;
                DrawZone(map, prog, d, d.Shape.Corners, null, false);
            }
            if (draft?.Draft != null)
            {
                var corners = draft.Corners.ToArray();
                if (draftDrawn != null) DrawZone(map, prog, draftDrawn, corners, draft.BadEdges(), draft.Active);
                else DrawOpen(map, prog, corners, draft.Active);
            }

            if (admin) DrawSpawners(map, prog, dim, mod.Catalog.Scaling.maxLevel);

            // labels last, over every fill
            foreach (var d in drawn)
                if (d.Shape.Model.dimension == dim && !IsDraft(draft, d.Shape.Model.id) && (admin || d.Shape.Model.enabled)) DrawLabel(map, d);
            if (draftDrawn != null) DrawLabel(map, draftDrawn);
        }

        private void DrawZone(GuiElementMap map, IShaderProgram prog, Drawn d, (int X, int Z)[] corners, HashSet<int>? bad, bool editing)
        {
            var s = d.Shape;
            ToView(map, s.MinX, s.MinZ, ref a);
            ToView(map, s.MaxX + 1, s.MaxZ + 1, ref b);
            if (b.X < 0 || b.Y < 0 || a.X > map.Bounds.InnerWidth || a.Y > map.Bounds.InnerHeight) return;

            float x0 = (float)map.Bounds.renderX, y0 = (float)map.Bounds.renderY;
            if (d.Fill != null)
            {
                prog.Uniform("noTexture", 0f);
                color.Set(d.Color.R, d.Color.G, d.Color.B, FillAlpha * d.Color.A * (editing ? 1.4f : 1f));
                prog.Uniform("rgbaIn", color);
                prog.BindTexture2D("tex2d", d.Fill.TextureId, 0);
                Quad(prog, x0 + (a.X + b.X) / 2, y0 + (a.Y + b.Y) / 2, 58f, (b.X - a.X) / 2, (b.Y - a.Y) / 2, 0f);
            }
            DrawEdges(map, prog, corners, d.Color, bad, editing, true);
        }

        // fewer than 3 corners: just the line so far
        private void DrawOpen(GuiElementMap map, IShaderProgram prog, (int X, int Z)[] corners, bool editing)
            => DrawEdges(map, prog, corners, Grabbed, null, editing, false);

        private void DrawEdges(GuiElementMap map, IShaderProgram prog, (int X, int Z)[] corners, Vec4f edgeColor, HashSet<int>? bad, bool editing, bool closed)
        {
            float x0 = (float)map.Bounds.renderX, y0 = (float)map.Bounds.renderY;
            prog.Uniform("noTexture", 1f);
            float thick = (float)GuiElement.scaled(editing ? 2.0 : 1.5);
            int edges = closed ? corners.Length : corners.Length - 1;
            for (int i = 0; i < edges; i++)
            {
                var c1 = corners[i];
                var c2 = corners[(i + 1) % corners.Length];
                ToView(map, c1.X + 0.5, c1.Z + 0.5, ref a);
                ToView(map, c2.X + 0.5, c2.Z + 0.5, ref b);
                float dx = b.X - a.X, dy = b.Y - a.Y;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.5f) continue;
                prog.Uniform("rgbaIn", bad != null && bad.Contains(i) ? Bad : edgeColor);
                Quad(prog, x0 + (a.X + b.X) / 2, y0 + (a.Y + b.Y) / 2, 59f, len / 2, thick, (float)Math.Atan2(dy, dx));
                // handle for adding a corner
                if (editing && len > 30)
                {
                    prog.Uniform("rgbaIn", hoverMid == i ? White : edgeColor);
                    float mid = (float)GuiElement.scaled(hoverMid == i ? 3.5 : 2.5);
                    Quad(prog, x0 + (a.X + b.X) / 2, y0 + (a.Y + b.Y) / 2, 60f, mid, mid, 0.785f);
                }
            }
            for (int i = 0; i < corners.Length; i++)
            {
                ToView(map, corners[i].X + 0.5, corners[i].Z + 0.5, ref a);
                float dot = (float)GuiElement.scaled(editing ? (i == hoverCorner || i == dragCorner ? 5.0 : 4.0) : 2.5);
                prog.Uniform("rgbaIn", !editing ? edgeColor : i == dragCorner ? Grabbed : i == hoverCorner ? White : edgeColor);
                Quad(prog, x0 + a.X, y0 + a.Y, 61f, dot, dot, 0f);
            }
        }

        // loaded spawner blocks as diamonds
        private void DrawSpawners(GuiElementMap map, IShaderProgram prog, int dim, int maxLevel)
        {
            prog.Uniform("noTexture", 1f);
            float size = (float)GuiElement.scaled(3.5);
            foreach (var spawner in BEMobSpawner.ClientLoaded)
            {
                if (spawner.Pos.dimension != dim) continue;
                ToView(map, spawner.Pos.X + 0.5, spawner.Pos.Z + 0.5, ref a);
                if (a.X < 0 || a.Y < 0 || a.X > map.Bounds.InnerWidth || a.Y > map.Bounds.InnerHeight) continue;
                int rgb = ZoneText.SpawnerRgb(capi!, spawner, maxLevel);
                var (r, g, bl) = ZoneText.Channels(rgb);
                color.Set(r / 255f, g / 255f, bl / 255f, 1f);
                prog.Uniform("rgbaIn", color);
                Quad(prog, (float)map.Bounds.renderX + a.X, (float)map.Bounds.renderY + a.Y, 61f, size, size, 0.785f);
            }
        }

        private void SpawnerHover(GuiElementMap map, MouseEvent args, StringBuilder hoverText)
        {
            double mx = args.X - map.Bounds.renderX, my = args.Y - map.Bounds.renderY;
            double grab = GuiElement.scaled(GrabPx);
            int dim = capi!.World.Player.Entity.Pos.Dimension;
            foreach (var spawner in BEMobSpawner.ClientLoaded)
            {
                if (spawner.Pos.dimension != dim) continue;
                ToView(map, spawner.Pos.X + 0.5, spawner.Pos.Z + 0.5, ref a);
                if ((a.X - mx) * (a.X - mx) + (a.Y - my) * (a.Y - my) > grab * grab) continue;
                var config = spawner.Config;
                string what = !config.IsSet ? Lang.Get("canrpgmobs:spawner-off")
                    : config.FromZone ? spawner.ZoneHere(capi)?.Model is { } zone ? Lang.Get("canrpgmobs:spawner-from-zone", ZoneText.Name(zone, Lang.CurrentLocale)) : Lang.Get("canrpgmobs:spawner-no-zone")
                    : ZoneText.ShortId(config.GroupId ?? config.MobKey ?? "");
                hoverText.AppendLine(Lang.Get("canrpgmobs:zones-map-spawner", what, spawner.AliveCount, config.MaxAlive) + (spawner.LastError != null ? " - " + spawner.LastError : ""));
                return;
            }
        }

        private void DrawLabel(GuiElementMap map, Drawn d)
        {
            if (d.Label == null) return;
            ToView(map, d.Shape.MinX, d.Shape.MinZ, ref a);
            ToView(map, d.Shape.MaxX + 1, d.Shape.MaxZ + 1, ref b);
            // too small on screen to name
            if (b.X - a.X < d.Label.Width * 0.6f) return;
            map.TranslateWorldPosToViewPos(d.LabelAt, ref a);
            capi!.Render.Render2DLoadedTexture(d.Label, (float)(map.Bounds.renderX + a.X - d.Label.Width / 2f),
                (float)(map.Bounds.renderY + a.Y - d.Label.Height / 2f), 62f);
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (capi == null || !Active || Mod is not { } mod) return;
            bool admin = Allowed();
            if (!admin && !mod.ZonesOnMapForPlayers) return;
            var at = MouseWorld(args, mapElem);
            var draft = admin ? mod.ZoneDraft : null;
            bool editing = draft is { Active: true, Draft: not null };
            bool leftDown = capi.Input.MouseButton.Left;
            if (leftDown && !leftWasDown)
            {
                pressX = lastX;
                pressY = lastY;
                moved = false;
            }
            if (leftDown && Math.Abs(args.X - pressX) + Math.Abs(args.Y - pressY) > 4) moved = true;
            lastX = args.X;
            lastY = args.Y;

            if (editing)
            {
                // press started on a handle: grab it instead of panning
                if (leftDown && !leftWasDown && dragCorner < 0 && (hoverCorner >= 0 || hoverMid >= 0))
                {
                    if (hoverCorner >= 0)
                    {
                        draft!.BeginDrag();
                        dragCorner = hoverCorner;
                    }
                    else
                    {
                        var (sx, sz) = Snap(mod, draft!, mapElem, at.X, at.Z, -1);
                        draft.InsertCorner(hoverMid + 1, sx, sz);
                        dragCorner = hoverMid + 1;
                    }
                    mapElem.CurrentBlockViewBounds.Translate(args.DeltaX / mapElem.ZoomLevel, 0, args.DeltaY / mapElem.ZoomLevel);
                }
                if (dragCorner >= 0)
                {
                    if (!leftDown) dragCorner = -1;
                    else
                    {
                        mapElem.IsDragingMap = false;
                        var (sx, sz) = Snap(mod, draft!, mapElem, at.X, at.Z, dragCorner);
                        draft!.MoveCorner(dragCorner, sx, sz);
                    }
                }
                if (dragCorner < 0) FindHandles(draft!, mapElem, args);
            }
            else hoverCorner = hoverMid = dragCorner = -1;
            leftWasDown = leftDown;

            int dim = capi.World.Player.Entity.Pos.Dimension;
            bool first = true;
            foreach (var shape in mod.Catalog.ZoneIndex.AllAt(dim, at.X, at.Y, at.Z))
            {
                if (IsDraft(draft, shape.Model.id) || (!admin && !shape.Model.enabled)) continue;
                var z = shape.Model;
                var line = new StringBuilder(Label(z));
                if (!admin)
                {
                    hoverText.AppendLine(line.ToString());
                    break;
                }
                if (!z.enabled) line.Append(' ').Append(Lang.Get("canrpgmobs:zone-off"));
                else if (!first) line.Append(' ').Append(Lang.Get("canrpgmobs:zone-covered"));
                if (z.priority != 0) line.Append(", ").Append(Lang.Get("canrpgmobs:zone-priority", z.priority));
                hoverText.AppendLine(line.ToString());
                if (z.enabled) first = false;
            }
            if (admin) SpawnerHover(mapElem, args, hoverText);
            if (draft?.Draft != null && draft.Shape is { } own && own.Contains(at.X, own.YMin == int.MinValue ? 0 : own.YMin, at.Z))
                hoverText.AppendLine(Label(own.Model) + (draft.IsDirty ? " *" : ""));
            if (draft is { Active: true })
                hoverText.AppendLine(Lang.Get(draft.Draft == null ? "canrpgmobs:zones-map-hint-none" : "canrpgmobs:zones-map-hint"));
        }

        public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
        {
            if (capi == null || !Active || !Allowed() || Mod?.ZoneDraft is not { Active: true } draft) return;
            var at = MouseWorld(args, mapElem);
            if (args.Button == EnumMouseButton.Left)
            {
                bool panned = moved;
                moved = false;
                leftWasDown = false;
                if (dragCorner >= 0)
                {
                    dragCorner = -1;
                    args.Handled = true;
                    return;
                }
                // a click, not a pan, picks the zone under the cursor
                if (panned) return;
                int dim = capi.World.Player.Entity.Pos.Dimension;
                var shape = Mod!.Catalog.ZoneIndex.At(dim, at.X, at.Y, at.Z, includeDisabled: true);
                if (shape == null || IsDraft(draft, shape.Model.id) || draft.WarnUnsaved()) return;
                draft.Open(shape.Model.id);
                return;
            }
            if (args.Button != EnumMouseButton.Right) return;
            args.Handled = true;
            if (draft.Draft == null)
            {
                draft.New((int)Math.Floor(at.X), (int)Math.Floor(at.Z));
                return;
            }
            if (hoverCorner >= 0)
            {
                draft.RemoveCornerIfAllowed(hoverCorner);
                hoverCorner = -1;
                return;
            }
            var (sx, sz) = Snap(Mod!, draft, mapElem, at.X, at.Z, -1);
            draft.AddCorner(sx, sz);
        }

        private void FindHandles(ZoneDraft draft, GuiElementMap map, MouseEvent args)
        {
            hoverCorner = hoverMid = -1;
            double mx = args.X - map.Bounds.renderX, my = args.Y - map.Bounds.renderY;
            double grab = GuiElement.scaled(GrabPx);
            double best = grab * grab;
            var corners = draft.Corners;
            for (int i = 0; i < corners.Count; i++)
            {
                ToView(map, corners[i].X + 0.5, corners[i].Z + 0.5, ref a);
                double sq = (a.X - mx) * (a.X - mx) + (a.Y - my) * (a.Y - my);
                if (sq > best) continue;
                best = sq;
                hoverCorner = i;
            }
            if (hoverCorner >= 0 || corners.Count < 3) return;
            for (int i = 0; i < corners.Count; i++)
            {
                var c1 = corners[i];
                var c2 = corners[(i + 1) % corners.Count];
                ToView(map, c1.X + 0.5, c1.Z + 0.5, ref a);
                ToView(map, c2.X + 0.5, c2.Z + 0.5, ref b);
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len <= 30) continue;
                double cx = (a.X + b.X) / 2 - mx, cy = (a.Y + b.Y) / 2 - my;
                double sq = cx * cx + cy * cy;
                if (sq > best) continue;
                best = sq;
                hoverMid = i;
            }
        }

        // Shift: to another zone's corner if one is close on screen, else to the grid
        private (int X, int Z) Snap(canrpgmobsModSystem mod, ZoneDraft draft, GuiElementMap map, double x, double z, int skip)
        {
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            if (!capi!.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftLeft] && !capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftRight]) return (bx, bz);
            double grab = GuiElement.scaled(GrabPx) / map.ZoomLevel;
            double best = grab * grab;
            (int X, int Z)? found = null;
            void Consider((int X, int Z) c)
            {
                double dx = c.X - bx, dz = c.Z - bz;
                if (dx * dx + dz * dz > best) return;
                best = dx * dx + dz * dz;
                found = c;
            }
            foreach (var shape in mod.Catalog.ZoneIndex.All)
            {
                if (IsDraft(draft, shape.Model.id)) continue;
                foreach (var c in shape.Corners) Consider(c);
            }
            var own = draft.Corners;
            for (int i = 0; i < own.Count; i++)
                if (i != skip) Consider(own[i]);
            if (found is { } hit) return hit;
            return ((int)Math.Round(x / SnapGrid) * SnapGrid, (int)Math.Round(z / SnapGrid) * SnapGrid);
        }

        private static bool IsDraft(ZoneDraft? draft, string id) => draft?.Draft != null && (draft.Id == id || draft.OpenedId == id);

        private Vec3d MouseWorld(MouseEvent args, GuiElementMap map)
        {
            var view = new Vec2f((float)(args.X - map.Bounds.renderX), (float)(args.Y - map.Bounds.renderY));
            var at = new Vec3d();
            map.TranslateViewPosToWorldPos(view, ref at);
            return at;
        }

        private void Rebuild(canrpgmobsModSystem mod)
        {
            DisposeDrawn();
            builtVersion = mod.CatalogVersion;
            foreach (var shape in mod.Catalog.ZoneIndex.All) drawn.Add(Make(shape, mod.Catalog.Scaling.maxLevel));
            draftBuiltVersion = -1;
        }

        // the fill is rasterized again at most every DraftFillMs while dragging
        private void UpdateDraftDrawn(canrpgmobsModSystem mod, ZoneDraft? draft)
        {
            if (draft?.Draft == null || draft.Shape == null)
            {
                DisposeDraft();
                return;
            }
            if (draftBuiltVersion == draft.Version) return;
            long now = capi!.World.ElapsedMilliseconds;
            if (draftDrawn != null && dragCorner >= 0 && now - draftBuiltMs < DraftFillMs)
            {
                draftDrawn.Shape = draft.Shape;
                return;
            }
            DisposeDraft();
            draftDrawn = Make(draft.Shape, mod.Catalog.Scaling.maxLevel);
            draftBuiltVersion = draft.Version;
            draftBuiltMs = now;
        }

        private Drawn Make(ZoneShape shape, int maxLevel)
        {
            var z = shape.Model;
            var (r, g, b) = ZoneText.Channels(z.enabled ? ZoneText.Rgb(z, maxLevel) : 0x9A9A9A);
            var d = new Drawn
            {
                Shape = shape,
                Color = new Vec4f(r / 255f, g / 255f, b / 255f, z.enabled ? 0.95f : 0.6f),
                Fill = Rasterize(shape)
            };
            var (lx, lz) = ZoneGeometry.LabelPoint(shape.Corners);
            d.LabelAt.Set(lx, 0, lz);
            d.Label = capi!.Gui.TextTexture.GenTextTexture(Label(z), CairoFont.WhiteSmallText().WithStroke(new double[] { 0, 0, 0, 0.9 }, 2.0));
            return d;
        }

        // "Dark Wood (lvl 10-15)"
        private static string Label(ZoneModel zone)
        {
            string levels = ZoneText.Levels(zone);
            string name = ZoneText.Name(zone, Lang.CurrentLocale);
            return levels.Length > 0 ? $"{name} ({Lang.Get("canrpgmobs:zone-level", levels)})" : name;
        }

        // white mask over the zone's bounds, tinted when drawn
        private LoadedTexture Rasterize(ZoneShape shape)
        {
            double w = shape.MaxX - shape.MinX + 1, h = shape.MaxZ - shape.MinZ + 1;
            double scale = Math.Min(1.0, MaxFillPixels / Math.Max(w, h));
            int pw = Math.Max(1, (int)Math.Ceiling(w * scale)), ph = Math.Max(1, (int)Math.Ceiling(h * scale));
            var texture = new LoadedTexture(capi);
            using (var surface = new ImageSurface(Format.Argb32, pw, ph))
            using (var ctx = new Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                var corners = shape.Corners;
                ctx.MoveTo((corners[0].X - shape.MinX + 0.5) * scale, (corners[0].Z - shape.MinZ + 0.5) * scale);
                for (int i = 1; i < corners.Length; i++) ctx.LineTo((corners[i].X - shape.MinX + 0.5) * scale, (corners[i].Z - shape.MinZ + 0.5) * scale);
                ctx.ClosePath();
                ctx.FillRule = FillRule.EvenOdd;
                ctx.Fill();
                capi!.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture);
            }
            return texture;
        }

        private void Quad(IShaderProgram prog, float cx, float cy, float z, float halfW, float halfH, float angle)
        {
            mv.Set(capi!.Render.CurrentModelviewMatrix).Translate(cx, cy, z);
            if (angle != 0f) mv.RotateZ(angle);
            mv.Scale(halfW, halfH, 0f);
            prog.UniformMatrix("modelViewMatrix", mv.Values);
            capi.Render.RenderMesh(quad!);
        }

        private void ToView(GuiElementMap map, double x, double z, ref Vec2f view)
        {
            world.Set(x, 0, z);
            map.TranslateWorldPosToViewPos(world, ref view);
        }

        private bool Allowed()
        {
            long now = capi!.World.ElapsedMilliseconds;
            if (now - allowedCheckMs < 1000 && now >= allowedCheckMs) return allowed;
            allowedCheckMs = now;
            return allowed = capi.World.Player?.HasPrivilege(Privilege.controlserver) == true;
        }

        private void DisposeDraft()
        {
            draftDrawn?.Fill?.Dispose();
            draftDrawn?.Label?.Dispose();
            draftDrawn = null;
            draftBuiltVersion = -1;
        }

        private void DisposeDrawn()
        {
            foreach (var d in drawn)
            {
                d.Fill?.Dispose();
                d.Label?.Dispose();
            }
            drawn.Clear();
        }

        public override void Dispose()
        {
            DisposeDrawn();
            DisposeDraft();
            quad?.Dispose();
            quad = null;
            base.Dispose();
        }
    }
}
