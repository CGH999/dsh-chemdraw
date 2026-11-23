using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace ChemBridge
{
    public sealed class BondRec
    {
        public string Id = "";
        public string A = "";
        public string B = "";
        public double Order = 1;
        public string Display = "";
    }

    public sealed class EzRec
    {
        public string A = "";
        public string B = "";
        public string Geometry = "";
        public double Side1;
        public double Side2;
    }

    /// <summary>Heavy-atom graph of one drawn structure, plus geometry derived from the drawing.</summary>
    public sealed class FragmentGraph
    {
        public Dictionary<string, double[]> P = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public Dictionary<string, int> El = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> Charges = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<BondRec> Bonds = new List<BondRec>();
        public Dictionary<string, List<string>> Adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public static FragmentGraph From(XmlElement frag)
        {
            FragmentGraph g = new FragmentGraph();
            foreach (XmlElement n in frag.SelectNodes(".//n[@p]"))
            {
                string id = n.GetAttribute("id");
                if (string.IsNullOrEmpty(id)) continue;
                double[] p = Cdx.ParsePoint(n.GetAttribute("p"));
                if (p == null) continue;
                g.P[id] = p;

                int z = 6;
                string el = n.GetAttribute("Element");
                if (!string.IsNullOrEmpty(el)) int.TryParse(el, out z);
                if (z <= 0) z = 6;
                g.El[id] = z;

                int ch = 0;
                string cs = n.GetAttribute("Charge");
                if (!string.IsNullOrEmpty(cs)) int.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out ch);
                g.Charges[id] = ch;

                if (!g.Adj.ContainsKey(id)) g.Adj[id] = new List<string>();
            }

            foreach (XmlElement b in frag.SelectNodes(".//b"))
            {
                BondRec r = new BondRec();
                r.Id = b.GetAttribute("id");
                r.A = b.GetAttribute("B");
                r.B = b.GetAttribute("E");
                string ord = b.GetAttribute("Order");
                double o;
                if (ord == "1.5") r.Order = 1.5;
                else if (double.TryParse(ord, NumberStyles.Float, CultureInfo.InvariantCulture, out o)) r.Order = o;
                else r.Order = 1;
                r.Display = b.GetAttribute("Display");
                if (string.IsNullOrEmpty(r.Display)) r.Display = b.GetAttribute("Display2");
                if (!g.P.ContainsKey(r.A) || !g.P.ContainsKey(r.B)) continue;
                g.Bonds.Add(r);
                g.Adj[r.A].Add(r.B);
                g.Adj[r.B].Add(r.A);
            }
            return g;
        }

        public int Degree(string id)
        {
            List<string> l;
            if (Adj.TryGetValue(id, out l)) return l.Count;
            return 0;
        }

        public int WedgeCount()
        {
            int n = 0;
            foreach (BondRec b in Bonds)
                if (b.Display.StartsWith("Wedge", StringComparison.OrdinalIgnoreCase) ||
                    b.Display.StartsWith("Hash", StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        public int ChargeSum()
        {
            int s = 0;
            foreach (KeyValuePair<string, int> kv in Charges) s += kv.Value;
            return s;
        }

        /// <summary>True when the a-b bond participates in a ring (alternate path exists).</summary>
        public bool InRing(string a, string b)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
            q.Enqueue(new KeyValuePair<string, int>(a, 0));
            seen.Add(a);
            while (q.Count > 0)
            {
                KeyValuePair<string, int> cur = q.Dequeue();
                if (cur.Value > 12) continue;
                List<string> nb;
                if (!Adj.TryGetValue(cur.Key, out nb)) continue;
                foreach (string next in nb)
                {
                    if (cur.Key == a && next == b) continue;   // skip the bond itself
                    if (next == b) return true;
                    if (seen.Contains(next)) continue;
                    seen.Add(next);
                    q.Enqueue(new KeyValuePair<string, int>(next, cur.Value + 1));
                }
            }
            return false;
        }

        /// <summary>Double bonds that can carry E/Z, with the geometry as actually drawn.</summary>
        public List<EzRec> StereoDoubleBonds()
        {
            List<EzRec> result = new List<EzRec>();
            foreach (BondRec b in Bonds)
            {
                if (Math.Abs(b.Order - 2.0) > 0.001) continue;
                if (Degree(b.A) < 2 || Degree(b.B) < 2) continue;   // terminal (e.g. C=O) carries no E/Z
                if (InRing(b.A, b.B)) continue;                      // ring double bonds are fixed by topology

                string na = Other(b.A, b.B);
                string nb = Other(b.B, b.A);
                if (na == null || nb == null) continue;

                double[] p1 = P[b.A], p2 = P[b.B], q1 = P[na], q2 = P[nb];
                double ax = p2[0] - p1[0], ay = p2[1] - p1[1];
                double s1 = (q1[0] - p1[0]) * ay - (q1[1] - p1[1]) * ax;
                double s2 = (q2[0] - p1[0]) * ay - (q2[1] - p1[1]) * ax;

                EzRec r = new EzRec();
                r.A = b.A;
                r.B = b.B;
                r.Side1 = s1;
                r.Side2 = s2;
                r.Geometry = (s1 * s2 > 0) ? "Z" : "E";
                result.Add(r);
            }
            return result;
        }

        private string Other(string center, string exclude)
        {
            List<string> nb;
            if (!Adj.TryGetValue(center, out nb)) return null;
            foreach (string s in nb) if (s != exclude) return s;
            return null;
        }

        public double MedianBondLength(out List<BondRec> outliers, out int bondCount)
        {
            List<double> lens = new List<double>();
            List<BondRec> recs = new List<BondRec>();
            foreach (BondRec b in Bonds)
            {
                double[] p1 = P[b.A], p2 = P[b.B];
                double d = Math.Sqrt((p2[0] - p1[0]) * (p2[0] - p1[0]) + (p2[1] - p1[1]) * (p2[1] - p1[1]));
                lens.Add(d);
                recs.Add(b);
            }
            outliers = new List<BondRec>();
            bondCount = lens.Count;
            if (lens.Count == 0) return 0;
            double[] sorted = lens.ToArray();
            Array.Sort(sorted);
            double median = sorted[sorted.Length / 2];
            if (median <= 0) return 0;
            for (int i = 0; i < lens.Count; i++)
            {
                if (Math.Abs(lens[i] - median) > 0.30 * median) outliers.Add(recs[i]);
            }
            return median;
        }
    }

    public static class Geo3D
    {
        /// <summary>RMSD of all points from their best-fit plane.</summary>
        public static double PlaneRmsd(List<double[]> pts)
        {
            if (pts.Count < 4) return 0;
            double cx = 0, cy = 0, cz = 0;
            foreach (double[] p in pts) { cx += p[0]; cy += p[1]; cz += p[2]; }
            cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;

            double[,] m = new double[3, 3];
            foreach (double[] p in pts)
            {
                double dx = p[0] - cx, dy = p[1] - cy, dz = p[2] - cz;
                m[0, 0] += dx * dx; m[0, 1] += dx * dy; m[0, 2] += dx * dz;
                m[1, 1] += dy * dy; m[1, 2] += dy * dz; m[2, 2] += dz * dz;
            }
            m[1, 0] = m[0, 1]; m[2, 0] = m[0, 2]; m[2, 1] = m[1, 2];

            double[] evals;
            double[,] evecs;
            Jacobi(m, out evals, out evecs);
            int minIdx = 0;
            for (int i = 1; i < 3; i++) if (evals[i] < evals[minIdx]) minIdx = i;

            double nx = evecs[0, minIdx], ny = evecs[1, minIdx], nz = evecs[2, minIdx];
            double sum = 0;
            foreach (double[] p in pts)
            {
                double d = (p[0] - cx) * nx + (p[1] - cy) * ny + (p[2] - cz) * nz;
                sum += d * d;
            }
            return Math.Sqrt(sum / pts.Count);
        }

        public static double MaxPairwise(List<double[]> pts)
        {
            double best = 0;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double d = Dist(pts[i], pts[j]);
                    if (d > best) best = d;
                }
            return best;
        }

        /// <summary>Largest |dihedral| over all bonded quadruples: a proxy for how far the skeleton twists.</summary>
        public static double MaxTorsion(List<double[]> pts, List<int[]> bonds)
        {
            Dictionary<int, List<int>> adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < pts.Count; i++) adj[i] = new List<int>();
            foreach (int[] b in bonds)
            {
                if (b[0] < 0 || b[1] < 0 || b[0] >= pts.Count || b[1] >= pts.Count) continue;
                adj[b[0]].Add(b[1]);
                adj[b[1]].Add(b[0]);
            }
            double best = 0;
            foreach (int[] b in bonds)
            {
                if (b[0] < 0 || b[1] < 0 || b[0] >= pts.Count || b[1] >= pts.Count) continue;
                foreach (int c in adj[b[0]])
                {
                    if (c == b[1]) continue;
                    foreach (int d in adj[b[1]])
                    {
                        if (d == b[0]) continue;
                        double ang = Math.Abs(Dihedral(pts[c], pts[b[0]], pts[b[1]], pts[d]));
                        if (ang > best) best = ang;
                    }
                }
            }
            return best;
        }

        public static double Dihedral(double[] p0, double[] p1, double[] p2, double[] p3)
        {
            double[] b0 = Sub(p0, p1);
            double[] b1 = Sub(p2, p1);
            double[] b2 = Sub(p3, p2);
            double[] n1 = Cross(b0, b1);
            double[] n2 = Cross(b1, b2);
            double n1n = Norm(n1), n2n = Norm(n2);
            if (n1n == 0 || n2n == 0) return 0;
            double cos = Dot(n1, n2) / (n1n * n2n);
            if (cos > 1) cos = 1;
            if (cos < -1) cos = -1;
            return Math.Acos(cos) * 180.0 / Math.PI;
        }

        private static double[] Sub(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double[] Cross(double[] a, double[] b) { return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
        private static double Dist(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2])); }

        private static void Jacobi(double[,] a, out double[] evals, out double[,] evecs)
        {
            int n = 3;
            double[,] v = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) v[i, j] = (i == j) ? 1 : 0;

            double[,] m = (double[,])a.Clone();
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = 0;
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++) off += Math.Abs(m[i, j]);
                if (off < 1e-12) break;

                for (int p = 0; p < n; p++)
                {
                    for (int q = p + 1; q < n; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-15) continue;
                        double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1);
                        double s = t * c;

                        for (int k = 0; k < n; k++)
                        {
                            double mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq;
                            m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk;
                            m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            evals = new double[n];
            for (int i = 0; i < n; i++) evals[i] = m[i, i];
            evecs = v;
        }
    }
}
