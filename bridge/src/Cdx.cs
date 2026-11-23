using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace ChemBridge
{
    /// <summary>CDXML document model: the editable representation of a .cdx file.</summary>
    public sealed class Cdx
    {
        public XmlDocument Doc;
        public XmlElement Root;
        public XmlElement Page;

        public static Cdx Load(string path)
        {
            XmlReaderSettings s = new XmlReaderSettings();
            s.DtdProcessing = DtdProcessing.Ignore;
            s.XmlResolver = null;
            s.IgnoreWhitespace = false;
            s.IgnoreComments = false;

            Cdx c = new Cdx();
            c.Doc = new XmlDocument();
            c.Doc.PreserveWhitespace = false;
            c.Doc.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(path, s))
            {
                c.Doc.Load(r);
            }
            c.Root = c.Doc.DocumentElement;
            if (c.Root == null || c.Root.Name != "CDXML")
                throw new InvalidOperationException("Not a CDXML document: " + path);
            c.Page = c.Root.SelectSingleNode("page") as XmlElement;
            if (c.Page == null)
            {
                c.Page = c.Doc.CreateElement("page");
                c.Root.AppendChild(c.Page);
            }
            return c;
        }

        public void Save(string path)
        {
            XmlWriterSettings s = new XmlWriterSettings();
            s.Encoding = new UTF8Encoding(false);
            s.Indent = false;
            s.NewLineHandling = NewLineHandling.None;
            using (XmlWriter w = XmlWriter.Create(path, s))
            {
                Doc.Save(w);
            }
        }

        public List<XmlElement> Fragments()
        {
            return NodesOf(Page.SelectNodes("fragment"));
        }

        public List<XmlElement> Captions()
        {
            return NodesOf(Page.SelectNodes("t"));
        }

        public List<XmlElement> Arrows()
        {
            return NodesOf(Page.SelectNodes("arrow"));
        }

        private static List<XmlElement> NodesOf(XmlNodeList list)
        {
            List<XmlElement> result = new List<XmlElement>();
            foreach (XmlNode n in list)
            {
                XmlElement e = n as XmlElement;
                if (e != null) result.Add(e);
            }
            return result;
        }

        public double BondLength
        {
            get
            {
                string v = Root.GetAttribute("BondLength");
                double d;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) return d;
                return 11.65;
            }
        }

        public static double[] ParseBBox(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return null;
            double[] b = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out b[i])) return null;
            }
            return b;
        }

        public static string FormatBBox(double[] b)
        {
            return Fmt(b[0]) + " " + Fmt(b[1]) + " " + Fmt(b[2]) + " " + Fmt(b[3]);
        }

        public static string Fmt(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Bounding box of a fragment: declared attribute when present, else computed from nodes.</summary>
        public double[] BBoxOf(XmlElement e)
        {
            double[] b = ParseBBox(e.GetAttribute("BoundingBox"));
            if (b != null) return b;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = -double.MaxValue, maxY = -double.MaxValue;
            bool any = false;
            foreach (XmlElement n in e.SelectNodes(".//n[@p]"))
            {
                double[] p = ParsePoint(n.GetAttribute("p"));
                if (p == null) continue;
                any = true;
                if (p[0] < minX) minX = p[0];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] < minY) minY = p[1];
                if (p[1] > maxY) maxY = p[1];
            }
            if (!any) return new double[] { 0, 0, 0, 0 };
            return new double[] { minX, minY, maxX, maxY };
        }

        public static double[] ParsePoint(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            double x, y;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return null;
            return new double[] { x, y };
        }

        /// <summary>Translate a fragment (and its geometry attributes) in place.</summary>
        public void Translate(XmlElement frag, double dx, double dy)
        {
            foreach (XmlElement n in frag.SelectNodes(".//n[@p]"))
            {
                double[] p = ParsePoint(n.GetAttribute("p"));
                if (p == null) continue;
                n.SetAttribute("p", Fmt(p[0] + dx) + " " + Fmt(p[1] + dy));
            }
            foreach (XmlElement sub in frag.SelectNodes(".//*[@BoundingBox]"))
            {
                double[] b = ParseBBox(sub.GetAttribute("BoundingBox"));
                if (b == null) continue;
                sub.SetAttribute("BoundingBox", FormatBBox(new double[] { b[0] + dx, b[1] + dy, b[2] + dx, b[3] + dy }));
            }
            double[] self = ParseBBox(frag.GetAttribute("BoundingBox"));
            if (self != null)
                frag.SetAttribute("BoundingBox", FormatBBox(new double[] { self[0] + dx, self[1] + dy, self[2] + dx, self[3] + dy }));
        }

        public int MaxId()
        {
            int max = 0;
            foreach (XmlElement e in Doc.SelectNodes("//*[@id]"))
            {
                int v;
                if (int.TryParse(e.GetAttribute("id"), out v) && v > max) max = v;
            }
            return max;
        }

        /// <summary>Shift every id inside the subtree by offset, and the B/E references of its bonds.</summary>
        public static void OffsetIds(XmlElement subtree, int offset)
        {
            foreach (XmlElement e in subtree.SelectNodes(".//*[@id]"))
            {
                int v;
                if (int.TryParse(e.GetAttribute("id"), out v)) e.SetAttribute("id", (v + offset).ToString(CultureInfo.InvariantCulture));
            }
            foreach (XmlElement b in subtree.SelectNodes(".//b"))
            {
                OffsetRef(b, "B", offset);
                OffsetRef(b, "E", offset);
            }
        }

        private static void OffsetRef(XmlElement b, string attr, int offset)
        {
            string v = b.GetAttribute(attr);
            int i;
            if (int.TryParse(v, out i)) b.SetAttribute(attr, (i + offset).ToString(CultureInfo.InvariantCulture));
        }

        public void Remove(XmlElement e)
        {
            if (e != null && e.ParentNode != null) e.ParentNode.RemoveChild(e);
        }

        /// <summary>Import a fragment from a standalone CDXML template, place its top-left corner at (x, y).</summary>
        public XmlElement AddFragmentFromCdxml(string templatePath, double x, double y)
        {
            Cdx t = Cdx.Load(templatePath);
            List<XmlElement> frags = t.Fragments();
            if (frags.Count == 0) throw new InvalidOperationException("Template contains no fragment");
            XmlElement src = frags[0];

            double[] srcBox = t.BBoxOf(src);
            double dx = x - srcBox[0];
            double dy = y - srcBox[1];

            XmlElement imported = (XmlElement)Doc.ImportNode(src, true);

            int minId = int.MaxValue;
            foreach (XmlElement e in imported.SelectNodes(".//*[@id]"))
            {
                int v;
                if (int.TryParse(e.GetAttribute("id"), out v) && v < minId) minId = v;
            }
            if (minId == int.MaxValue) minId = 0;
            int offset = MaxId() + 1 - minId;
            OffsetIds(imported, offset);

            Page.AppendChild(imported);
            Translate(imported, dx, dy);
            return imported;
        }

        public string CaptionText(XmlElement t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (XmlNode n in t.SelectNodes(".//s"))
            {
                if (n is XmlElement)
                {
                    XmlElement s = (XmlElement)n;
                    if (s.HasAttribute("font")) continue;
                    sb.Append(s.InnerText);
                }
            }
            if (sb.Length == 0) sb.Append(t.InnerText);
            return sb.ToString().Trim();
        }

        /// <summary>Caption text with bold/italic runs marked as-is (kept simple: plain concatenation of &lt;s&gt; runs).</summary>
        public static string RawText(XmlElement t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (XmlNode n in t.SelectNodes(".//s"))
            {
                if (n is XmlElement && ((XmlElement)n).HasAttribute("font")) continue;
                sb.Append(n.InnerText);
            }
            if (sb.Length == 0) sb.Append(t.InnerText);
            return sb.ToString();
        }

        public XmlElement AddCaption(string text, double x, double y)
        {
            XmlElement t = Doc.CreateElement("t");
            t.SetAttribute("id", (MaxId() + 1).ToString(CultureInfo.InvariantCulture));
            t.SetAttribute("p", Fmt(x) + " " + Fmt(y));
            double w = Math.Max(30, text.Length * 4.2);
            t.SetAttribute("BoundingBox", FormatBBox(new double[] { x - 2, y - 3, x + w, y + 5 }));
            // Without this ChemDraw reads "B1" or "A1" as a chemical formula and invents an atom.
            t.SetAttribute("InterpretChemically", "no");
            t.SetAttribute("LineHeight", "auto");
            XmlElement s = Doc.CreateElement("s");
            s.SetAttribute("font", "3");
            s.SetAttribute("size", "8.05");
            s.SetAttribute("face", "96");
            s.InnerText = text;
            t.AppendChild(s);
            Page.AppendChild(t);
            return t;
        }

        /// <summary>Move a caption to a new anchor point, keeping its size.</summary>
        public void MoveCaption(XmlElement t, double x, double y)
        {
            double[] b = ParseBBox(t.GetAttribute("BoundingBox"));
            double w = b != null ? (b[2] - b[0]) : Math.Max(30, Cdx.RawText(t).Length * 4.2);
            double h = b != null ? (b[3] - b[1]) : 8;
            t.SetAttribute("p", Fmt(x) + " " + Fmt(y));
            t.SetAttribute("BoundingBox", FormatBBox(new double[] { x - 2, y - h + 2, x - 2 + Math.Max(30, w), y + 2 }));
        }

        /// <summary>Append a reaction arrow from (x0,y0) to (x1,y1).</summary>
        public XmlElement AddArrow(double x0, double y0, double x1, double y1)
        {
            XmlElement a = Doc.CreateElement("arrow");
            a.SetAttribute("id", (MaxId() + 1).ToString(CultureInfo.InvariantCulture));
            double minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            double minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
            a.SetAttribute("BoundingBox", FormatBBox(new double[] { minX, minY, maxX, maxY }));
            a.SetAttribute("FillType", "None");
            a.SetAttribute("ArrowheadHead", "Full");
            a.SetAttribute("ArrowheadType", "Solid");
            a.SetAttribute("HeadSize", "1000");
            a.SetAttribute("ArrowheadCenterSize", "875");
            a.SetAttribute("ArrowheadWidth", "250");
            a.SetAttribute("Head3D", Fmt(x1) + " " + Fmt(y1) + " 0");
            a.SetAttribute("Tail3D", Fmt(x0) + " " + Fmt(y0) + " 0");
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            a.SetAttribute("Center3D", Fmt(cx) + " " + Fmt(cy) + " 0");
            a.SetAttribute("MajorAxisEnd3D", Fmt((minX + maxX) / 2) + " " + Fmt((minY + maxY) / 2) + " 0");
            a.SetAttribute("MinorAxisEnd3D", Fmt((minX + maxX) / 2) + " " + Fmt((minY + maxY) / 2) + " 0");
            Page.AppendChild(a);
            return a;
        }

        /// <summary>Page width in document points (falls back to A4-ish width).</summary>
        public double PageWidth
        {
            get
            {
                double[] b = ParseBBox(Page.GetAttribute("BoundingBox"));
                if (b != null && b[2] > b[0]) return b[2] - b[0];
                return 1150;
            }
        }

        public void SetCaptionText(XmlElement t, string text)
        {
            XmlElement run = null;
            foreach (XmlElement s in t.SelectNodes(".//s"))
            {
                if (s.HasAttribute("font")) continue;
                run = s;
                break;
            }
            if (run == null)
            {
                // Plain-text captions (no <s> runs) are dropped by ChemDraw on export, so materialise one.
                run = Doc.CreateElement("s");
                run.SetAttribute("font", "3");
                run.SetAttribute("size", "8.05");
                run.SetAttribute("face", "96");
                // Remove child nodes only: RemoveAll() would also drop p/BoundingBox/id/Z and
                // ChemDraw would then place the caption at the origin.
                while (t.HasChildNodes) t.RemoveChild(t.FirstChild);
                t.AppendChild(run);
            }
            if (!t.HasAttribute("InterpretChemically")) t.SetAttribute("InterpretChemically", "no");
            run.InnerText = text;

            double[] b = ParseBBox(t.GetAttribute("BoundingBox"));
            if (b != null)
            {
                double[] p = ParsePoint(t.GetAttribute("p"));
                double x = p != null ? p[0] : b[0];
                double y = p != null ? p[1] : b[1];
                t.SetAttribute("BoundingBox", FormatBBox(new double[] { x - 2, y - 3, x + Math.Max(30, text.Length * 4.2), y + 5 }));
            }
        }
    }
}
