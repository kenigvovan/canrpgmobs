using System;
using System.Collections.Generic;

namespace canrpgmobs.Data
{
    // corners are block centers
    public static class ZoneGeometry
    {
        public static (int X, int Z)[]? Corners(List<int[]>? polygon)
        {
            if (polygon == null || polygon.Count < 3) return null;
            var corners = new (int, int)[polygon.Count];
            for (int i = 0; i < polygon.Count; i++)
            {
                if (polygon[i] is not { Length: 2 } p) return null;
                corners[i] = (p[0], p[1]);
            }
            return corners;
        }

        public static bool Contains((int X, int Z)[] corners, double x, double z)
        {
            double px = x - 0.5, pz = z - 0.5;
            bool inside = false;
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
            {
                var (xi, zi) = corners[i];
                var (xj, zj) = corners[j];
                if ((zi > pz) != (zj > pz) && px < (double)(xj - xi) * (pz - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        public static double Area((int X, int Z)[] corners)
        {
            long twice = 0;
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
                twice += (long)corners[j].X * corners[i].Z - (long)corners[i].X * corners[j].Z;
            return Math.Abs(twice) / 2.0;
        }

        public static double Perimeter((int X, int Z)[] corners)
        {
            double sum = 0;
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
                sum += Math.Sqrt(Sq(corners[i].X - corners[j].X) + Sq(corners[i].Z - corners[j].Z));
            return sum;
        }

        // edge i = corner i to i + 1
        public static bool SelfIntersects((int X, int Z)[] corners, out int edgeA, out int edgeB)
        {
            int n = corners.Length;
            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    bool neighbours = b == a + 1 || (a == 0 && b == n - 1);
                    var a1 = corners[a]; var a2 = corners[(a + 1) % n];
                    var b1 = corners[b]; var b2 = corners[(b + 1) % n];
                    if (neighbours ? Folds(a1, a2, b1, b2, b == a + 1) : SegmentsTouch(a1, a2, b1, b2))
                    {
                        edgeA = a;
                        edgeB = b;
                        return true;
                    }
                }
            }
            edgeA = edgeB = -1;
            return false;
        }

        // every edge that crosses or folds onto another, for drawing
        public static HashSet<int> BadEdges((int X, int Z)[] corners)
        {
            var bad = new HashSet<int>();
            int n = corners.Length;
            if (n < 3) return bad;
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    bool neighbours = b == a + 1 || (a == 0 && b == n - 1);
                    var a1 = corners[a]; var a2 = corners[(a + 1) % n];
                    var b1 = corners[b]; var b2 = corners[(b + 1) % n];
                    if (!(neighbours ? Folds(a1, a2, b1, b2, b == a + 1) : SegmentsTouch(a1, a2, b1, b2))) continue;
                    bad.Add(a);
                    bad.Add(b);
                }
            return bad;
        }

        // touching borders count
        public static bool Overlaps((int X, int Z)[] a, (int X, int Z)[] b)
        {
            var (ax1, az1, ax2, az2) = Bounds(a);
            var (bx1, bz1, bx2, bz2) = Bounds(b);
            if (ax2 < bx1 || bx2 < ax1 || az2 < bz1 || bz2 < az1) return false;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (SegmentsTouch(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length])) return true;
            // one inside the other
            return Contains(b, a[0].X + 0.5, a[0].Z + 0.5) || Contains(a, b[0].X + 0.5, b[0].Z + 0.5);
        }

        public static (int MinX, int MinZ, int MaxX, int MaxZ) Bounds((int X, int Z)[] corners)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var (x, z) in corners)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
            return (minX, minZ, maxX, maxZ);
        }

        // centroid, or the widest inside span through it for concave outlines
        public static (double X, double Z) LabelPoint((int X, int Z)[] corners)
        {
            double cx = 0, cz = 0, twice = 0;
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
            {
                double cross = (double)corners[j].X * corners[i].Z - (double)corners[i].X * corners[j].Z;
                twice += cross;
                cx += (corners[j].X + corners[i].X) * cross;
                cz += (corners[j].Z + corners[i].Z) * cross;
            }
            if (Math.Abs(twice) > 1e-9)
            {
                cx /= 3 * twice;
                cz /= 3 * twice;
            }
            else
            {
                cx = corners[0].X;
                cz = corners[0].Z;
            }
            if (Contains(corners, cx + 0.5, cz + 0.5)) return (cx + 0.5, cz + 0.5);

            var xs = new List<double>();
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
            {
                var (xi, zi) = corners[i];
                var (xj, zj) = corners[j];
                if ((zi > cz) != (zj > cz)) xs.Add((xj - xi) * (cz - zi) / (zj - zi) + xi);
            }
            xs.Sort();
            double best = -1, bestX = corners[0].X;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                if (xs[k + 1] - xs[k] <= best) continue;
                best = xs[k + 1] - xs[k];
                bestX = (xs[k] + xs[k + 1]) / 2;
            }
            return (bestX + 0.5, cz + 0.5);
        }

        private static double Sq(double v) => v * v;

        private static long Cross((int X, int Z) o, (int X, int Z) a, (int X, int Z) b)
            => (long)(a.X - o.X) * (b.Z - o.Z) - (long)(a.Z - o.Z) * (b.X - o.X);

        private static bool OnSegment((int X, int Z) p, (int X, int Z) a, (int X, int Z) b)
            => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Z, b.Z) <= p.Z && p.Z <= Math.Max(a.Z, b.Z);

        private static bool SegmentsTouch((int X, int Z) a1, (int X, int Z) a2, (int X, int Z) b1, (int X, int Z) b2)
        {
            long d1 = Cross(b1, b2, a1), d2 = Cross(b1, b2, a2), d3 = Cross(a1, a2, b1), d4 = Cross(a1, a2, b2);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return (d1 == 0 && OnSegment(a1, b1, b2)) || (d2 == 0 && OnSegment(a2, b1, b2))
                   || (d3 == 0 && OnSegment(b1, a1, a2)) || (d4 == 0 && OnSegment(b2, a1, a2));
        }

        // neighbour edges: zero length or folded back onto each other
        private static bool Folds((int X, int Z) a1, (int X, int Z) a2, (int X, int Z) b1, (int X, int Z) b2, bool aThenB)
        {
            if (a1 == a2 || b1 == b2) return true;
            var (s, p, q) = aThenB ? (a2, a1, b2) : (a1, a2, b1);
            if (Cross(s, p, q) != 0) return false;
            long dot = (long)(p.X - s.X) * (q.X - s.X) + (long)(p.Z - s.Z) * (q.Z - s.Z);
            return dot > 0;
        }
    }
}
