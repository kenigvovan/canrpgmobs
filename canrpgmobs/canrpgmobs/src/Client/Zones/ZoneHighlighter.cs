using System;
using System.Collections.Generic;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Spawning;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Client.Zones
{
    // zone outlines on the ground around the player; the zone being marked up is shown as drafted
    public sealed class ZoneHighlighter : IDisposable
    {
        private const int Slot = 7301;
        private const int Radius = 96;
        private const int PillarHeight = 6;
        private const int MaxBlocks = 6000;
        private const long MinRefreshMs = 200;

        private readonly ICoreClientAPI capi;
        private readonly canrpgmobsModSystem mod;
        private long tickId;
        private int lastHash;
        private long lastRefreshMs;
        private bool wanted;

        private readonly List<BlockPos> blocks = new();
        private readonly List<int> colors = new();

        public ZoneHighlighter(ICoreClientAPI capi, canrpgmobsModSystem mod)
        {
            this.capi = capi;
            this.mod = mod;
        }

        public bool Enabled => tickId != 0;

        // markup mode keeps it on even when switched off by the command
        public void SetEnabled(bool on)
        {
            wanted = on;
            Apply();
        }

        public void Apply()
        {
            bool on = wanted || mod.ZoneDraft?.Active == true;
            if (on == Enabled)
            {
                if (on) Refresh(false);
                return;
            }
            if (on)
            {
                tickId = capi.Event.RegisterGameTickListener(_ => Refresh(true), 1000);
                Refresh(true);
                return;
            }
            capi.Event.UnregisterGameTickListener(tickId);
            tickId = 0;
            Clear();
        }

        private void Refresh(bool force)
        {
            long now = capi.World.ElapsedMilliseconds;
            if (!force && now - lastRefreshMs < MinRefreshMs) return;
            lastRefreshMs = now;
            var player = capi.World.Player;
            if (player?.Entity == null) return;
            var pos = player.Entity.Pos;
            int px = (int)Math.Floor(pos.X), pz = (int)Math.Floor(pos.Z), dim = pos.Dimension;
            blocks.Clear();
            colors.Clear();
            int maxLevel = mod.Catalog.Scaling.maxLevel;
            var draft = mod.ZoneDraft;
            string? draftId = draft?.Draft != null ? draft.Id : null;
            string? openedId = draft?.Draft != null ? draft.OpenedId : null;

            foreach (var shape in mod.Catalog.ZoneIndex.All)
            {
                var z = shape.Model;
                if (z.dimension != dim || z.id == draftId || z.id == openedId) continue;
                if (shape.MaxX < px - Radius || shape.MinX > px + Radius || shape.MaxZ < pz - Radius || shape.MinZ > pz + Radius) continue;
                int rgb = z.enabled ? ZoneText.Rgb(z, maxLevel) : 0x808080;
                Outline(shape.Corners, true, rgb, px, pz, dim, -1);
            }
            if (draft?.Active == true)
                foreach (var spawner in BEMobSpawner.ClientLoaded)
                {
                    var at = spawner.Pos;
                    if (at.dimension != dim || !Near(at.X, at.Z, px, pz) || blocks.Count >= MaxBlocks) continue;
                    int rgb = ZoneText.SpawnerRgb(capi, spawner, maxLevel);
                    var (r, g, b) = ZoneText.Channels(rgb);
                    blocks.Add(at.Copy());
                    colors.Add(ColorUtil.ColorFromRgba(r, g, b, 150));
                }
            if (draft?.Draft != null && ((int?)draft.Draft["dimension"] ?? 0) == dim)
            {
                var corners = draft.Corners.ToArray();
                Outline(corners, corners.Length >= 3, 0xFFD030, px, pz, dim, corners.Length - 1);
                if (mod.ShowZoneRing) Ring(draft.Draft["spawnDistance"] is JArray { Count: 2 } d ? new[] { (int)d[0], (int)d[1] } : ZoneRules.DefaultSpawnDistance, px, pz, dim);
                if (mod.ShowZoneAttempts && mod.LastZoneStats is { } stats && stats.ZoneId == draft.OpenedId) Attempts(stats, dim);
            }

            int hash = blocks.Count;
            for (int i = 0; i < blocks.Count; i++) hash = hash * 31 + blocks[i].GetHashCode() ^ colors[i];
            if (hash == lastHash) return;
            lastHash = hash;
            capi.World.HighlightBlocks(player, Slot, blocks, colors);
        }

        // last = the corner added last, drawn taller
        private void Outline((int X, int Z)[] corners, bool closed, int rgb, int px, int pz, int dim, int last)
        {
            var ba = capi.World.BlockAccessor;
            var (r, g, b) = ZoneText.Channels(rgb);
            int edge = ColorUtil.ColorFromRgba(r, g, b, 110);
            int corner = ColorUtil.ColorFromRgba(Math.Min(255, r + 60), Math.Min(255, g + 60), Math.Min(255, b + 60), 170);
            int edges = closed ? corners.Length : corners.Length - 1;

            for (int i = 0; i < corners.Length && blocks.Count < MaxBlocks; i++)
            {
                var (x1, z1) = corners[i];
                if (i < edges)
                {
                    var (x2, z2) = corners[(i + 1) % corners.Length];
                    int steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(z2 - z1));
                    // corners go in as pillars
                    for (int s = 1; s < steps && blocks.Count < MaxBlocks; s++)
                    {
                        int x = x1 + (int)Math.Round((x2 - x1) * (double)s / steps);
                        int z = z1 + (int)Math.Round((z2 - z1) * (double)s / steps);
                        if (!Near(x, z, px, pz)) continue;
                        int y = ba.GetRainMapHeightAt(x, z);
                        if (y <= 0) continue;
                        blocks.Add(new BlockPos(x, y, z, dim));
                        colors.Add(edge);
                    }
                }
                if (!Near(x1, z1, px, pz)) continue;
                int ground = ba.GetRainMapHeightAt(x1, z1);
                if (ground <= 0) continue;
                int height = i == last ? PillarHeight + 4 : PillarHeight;
                for (int h = 0; h <= height; h++)
                {
                    blocks.Add(new BlockPos(x1, ground + h, z1, dim));
                    colors.Add(corner);
                }
            }
        }

        // where the zone may spawn around the player: between the two circles
        private void Ring(int[] range, int px, int pz, int dim)
        {
            var ba = capi.World.BlockAccessor;
            int inner = ColorUtil.ColorFromRgba(90, 140, 255, 120), outer = ColorUtil.ColorFromRgba(90, 220, 255, 120);
            foreach (var (radius, color) in new[] { (range[0], inner), (range[1], outer) })
            {
                if (radius <= 0) continue;
                int steps = Math.Clamp(radius * 4, 24, 400);
                for (int s = 0; s < steps && blocks.Count < MaxBlocks; s++)
                {
                    double angle = s * GameMath.TWOPI / steps;
                    int x = px + (int)Math.Round(Math.Cos(angle) * radius), z = pz + (int)Math.Round(Math.Sin(angle) * radius);
                    int y = ba.GetRainMapHeightAt(x, z);
                    if (y <= 0) continue;
                    blocks.Add(new BlockPos(x, y, z, dim));
                    colors.Add(color);
                }
            }
        }

        // last spawn attempts from the server, green worked, red refused
        private void Attempts(ZoneStatsPacket stats, int dim)
        {
            int good = ColorUtil.ColorFromRgba(60, 230, 90, 160), bad = ColorUtil.ColorFromRgba(240, 60, 50, 160);
            for (int i = 0; i < stats.Ok.Length && i * 3 + 2 < stats.Points.Length && blocks.Count < MaxBlocks; i++)
            {
                var pos = new BlockPos((int)Math.Floor(stats.Points[i * 3]), (int)Math.Floor(stats.Points[i * 3 + 1]), (int)Math.Floor(stats.Points[i * 3 + 2]), dim);
                blocks.Add(pos);
                colors.Add(stats.Ok[i] ? good : bad);
                blocks.Add(pos.UpCopy());
                colors.Add(stats.Ok[i] ? good : bad);
            }
        }

        private static bool Near(int x, int z, int px, int pz)
        {
            long dx = x - px, dz = z - pz;
            return dx * dx + dz * dz <= (long)Radius * Radius;
        }

        private void Clear()
        {
            lastHash = 0;
            if (capi.World?.Player is { } player) capi.World.HighlightBlocks(player, Slot, new List<BlockPos>(), new List<int>());
        }

        public void Dispose()
        {
            if (tickId != 0) capi.Event.UnregisterGameTickListener(tickId);
            tickId = 0;
        }
    }
}
