using System;
using System.Collections.Generic;
using System.Linq;

namespace canrpgmobs.Data
{
    public sealed class ZoneShape
    {
        internal ZoneShape(ZoneModel model, (int X, int Z)[] corners)
        {
            Model = model;
            Corners = corners;
            (MinX, MinZ, MaxX, MaxZ) = ZoneGeometry.Bounds(corners);
            YMin = model.y is { Length: 2 } y ? y[0] : int.MinValue;
            YMax = model.y is { Length: 2 } y2 ? y2[1] : int.MaxValue;
        }

        public static ZoneShape? Of(ZoneModel model) => ZoneGeometry.Corners(model.polygon) is { } c ? new ZoneShape(model, c) : null;

        public ZoneModel Model { get; }
        public (int X, int Z)[] Corners { get; }
        public int MinX { get; }
        public int MinZ { get; }
        public int MaxX { get; }
        public int MaxZ { get; }
        public int YMin { get; }
        public int YMax { get; }

        public bool Contains(double x, double y, double z)
            => x >= MinX && x <= MaxX + 1 && z >= MinZ && z <= MaxZ + 1 && y >= YMin && y < YMax + 1.0
               && ZoneGeometry.Contains(Corners, x, z);
    }

    // built once per catalog
    public sealed class ZoneIndex
    {
        // bigger zones skip the per-column table
        private const long MaxColumns = 65536;

        private readonly Dictionary<long, ZoneShape[]> columns = new();
        private readonly ZoneShape[] large;
        private readonly Dictionary<string, ZoneShape> byId = new();

        public IReadOnlyList<ZoneShape> All { get; }

        public ZoneIndex(IEnumerable<ZoneModel> zones)
        {
            // priority first, id keeps equal ones stable
            All = zones.Select(ZoneShape.Of).Where(s => s != null).Select(s => s!)
                .OrderByDescending(s => s.Model.priority).ThenBy(s => s.Model.id, StringComparer.Ordinal).ToArray();
            foreach (var shape in All) byId.TryAdd(shape.Model.id, shape);

            var bigOnes = new List<ZoneShape>();
            var lists = new Dictionary<long, List<ZoneShape>>();
            foreach (var shape in All)
            {
                int cx1 = shape.MinX >> 5, cz1 = shape.MinZ >> 5, cx2 = shape.MaxX >> 5, cz2 = shape.MaxZ >> 5;
                if ((long)(cx2 - cx1 + 1) * (cz2 - cz1 + 1) > MaxColumns)
                {
                    bigOnes.Add(shape);
                    continue;
                }
                for (int cx = cx1; cx <= cx2; cx++)
                    for (int cz = cz1; cz <= cz2; cz++)
                    {
                        long key = Key(shape.Model.dimension, cx, cz);
                        if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<ZoneShape>(1);
                        list.Add(shape);
                    }
            }
            foreach (var (key, list) in lists) columns[key] = list.ToArray();
            large = bigOnes.ToArray();
        }

        public ZoneShape? Get(string id) => byId.GetValueOrDefault(id);

        // the winning zone
        public ZoneShape? At(int dimension, double x, double y, double z, bool includeDisabled = false)
        {
            ZoneShape? best = null;
            if (columns.TryGetValue(Key(dimension, (int)Math.Floor(x) >> 5, (int)Math.Floor(z) >> 5), out var shapes))
                foreach (var shape in shapes)
                    if ((includeDisabled || shape.Model.enabled) && shape.Contains(x, y, z))
                    {
                        best = shape;
                        break;
                    }
            foreach (var shape in large)
                if (shape.Model.dimension == dimension && (includeDisabled || shape.Model.enabled) && shape.Contains(x, y, z)
                    && (best == null || Before(shape, best)))
                {
                    best = shape;
                    break;
                }
            return best;
        }

        // every zone here, winner first
        public List<ZoneShape> AllAt(int dimension, double x, double y, double z)
        {
            var found = new List<ZoneShape>();
            foreach (var shape in All)
                if (shape.Model.dimension == dimension && shape.Contains(x, y, z)) found.Add(shape);
            return found;
        }

        private static bool Before(ZoneShape a, ZoneShape b)
            => a.Model.priority != b.Model.priority ? a.Model.priority > b.Model.priority : string.CompareOrdinal(a.Model.id, b.Model.id) < 0;

        private static long Key(int dimension, int cx, int cz)
            => ((long)dimension << 48) ^ ((long)(cx & 0xFFFFFF) << 24) ^ (cz & 0xFFFFFF);
    }
}
