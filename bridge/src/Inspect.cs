using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ChemBridge
{
    public sealed class StructureRecord
    {
        public int Index;
        public XmlElement Fragment;
        public StructureInfo Info;
        public FragmentGraph Graph;
        public double[] BBox;
        public List<EzRec> Ez = new List<EzRec>();
        public double MedianBondLength;
        public int OutlierCount;
        public int Wedges;
        public string Label = "";
        public double DrawnMw = double.NaN;
        public string AnnotationText = "";
        public string ThumbPath = "";
        public List<string> Warnings = new List<string>();

        /// <summary>Captions paired with this structure (label and mass annotation) so edits can update or remove them.</summary>
        public List<XmlElement> OwnedCaptions = new List<XmlElement>();

        public double CenterX { get { return (BBox[0] + BBox[2]) / 2.0; } }
        public double CenterY { get { return (BBox[1] + BBox[3]) / 2.0; } }
    }

    /// <summary>Reads a .cdx/.cdxml document and produces the analysis payload.</summary>
    public sealed class Inspector
    {
        private readonly ChemPaths _paths;
        private readonly string _outDir;
        private readonly Engine _engine;
        private readonly List<string> _warnings = new List<string>();

        public Inspector(ChemPaths paths, string outDir, Engine engine)
        {
            _paths = paths;
            _outDir = outDir;
            _engine = engine;
            if (!Directory.Exists(_outDir)) Directory.CreateDirectory(_outDir);
        }

        public static string WrapFragment(XmlElement frag, double bondLength)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<CDXML BondLength=\"").Append(Cdx.Fmt(bondLength)).Append("\"><page>");
            sb.Append(frag.OuterXml);
            sb.Append("</page></CDXML>");
            return sb.ToString();
        }

        /// <summary>Convert .cdx to .cdxml (and render a page PNG) using ChemDraw, retrying cold starts.</summary>
        public static void ExportFromCdx(string inputPath, string cdxmlPath, string pngPath, int dpi)
        {
            RunWithRetry("convert", inputPath, delegate
            {
                using (ChemDraw app = ChemDraw.Acquire())
                {
                    app.Open(inputPath);
                    app.SaveAs(cdxmlPath, "text/xml");
                    if (!string.IsNullOrEmpty(pngPath))
                    {
                        try { app.SaveAs(pngPath, "image/png", dpi); }
                        catch { }
                    }
                }
            });
        }

        public static void RenderTo(string inputPath, string outputPath, int dpi)
        {
            RunWithRetry("render", inputPath, delegate
            {
                using (ChemDraw app = ChemDraw.Acquire())
                {
                    app.Open(inputPath);
                    string lower = outputPath.ToLowerInvariant();
                    if (lower.EndsWith(".svg")) app.SaveAs(outputPath, "image/svg+xml");
                    else app.SaveAs(outputPath, "image/png", dpi);
                }
            });
        }

        /// <summary>
        /// The first automation call after a cold start can fail while ChemDraw finishes booting;
        /// retry a few times with backoff instead of surfacing a phantom "cannot open" to the user.
        /// </summary>
        public static void RunWithRetry(string action, string path, Action operation)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    operation();
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) System.Threading.Thread.Sleep(400 * attempt);
                }
            }
            throw new InvalidOperationException("ChemDraw could not " + action + " " + path + " after 3 attempts: "
                + ChemDraw.Innermost(last), last);
        }

        public Dictionary<string, object> Run(string inputPath, bool makeThumbnails)
        {
            Stopwatch total = Stopwatch.StartNew();
            Dictionary<string, object> result = Json.Obj();

            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            string cdxmlPath = inputPath;
            string pngPath = Path.Combine(_outDir, "page.png");
            long convertMs = 0;

            if (ext == ".cdx")
            {
                Stopwatch sw = Stopwatch.StartNew();
                ExportFromCdx(inputPath, Path.Combine(_outDir, "document.cdxml"), pngPath, 300);
                convertMs = sw.ElapsedMilliseconds;
                cdxmlPath = Path.Combine(_outDir, "document.cdxml");
            }
            else if (ext == ".cdxml")
            {
                Stopwatch sw = Stopwatch.StartNew();
                RenderTo(inputPath, pngPath, 300);
                convertMs = sw.ElapsedMilliseconds;
            }
            else
            {
                throw new InvalidOperationException("Unsupported file type: " + ext + " (expected .cdx or .cdxml)");
            }

            if (!File.Exists(cdxmlPath)) throw new InvalidOperationException("Conversion produced no CDXML: " + inputPath);
            if (!File.Exists(pngPath)) pngPath = "";

            Cdx doc = Cdx.Load(cdxmlPath);
            List<XmlElement> frags = doc.Fragments();
            List<XmlElement> captions = doc.Captions();
            List<XmlElement> arrows = doc.Arrows();

            Stopwatch swAnalyze = Stopwatch.StartNew();
            List<StructureRecord> structures = new List<StructureRecord>();
            int idx = 0;
            foreach (XmlElement frag in frags)
            {
                idx++;
                StructureRecord rec = new StructureRecord();
                rec.Index = idx;
                rec.Fragment = frag;
                rec.BBox = doc.BBoxOf(frag);
                rec.Graph = FragmentGraph.From(frag);
                int dummy;
                List<BondRec> outliers;
                rec.MedianBondLength = rec.Graph.MedianBondLength(out outliers, out dummy);
                rec.OutlierCount = outliers.Count;
                rec.Wedges = rec.Graph.WedgeCount();
                rec.Ez = rec.Graph.StereoDoubleBonds();
                rec.Info = _engine.AnalyzeCdxml(WrapFragment(frag, doc.BondLength));
                if (!rec.Info.Ok)
                    rec.Warnings.Add("structure could not be interpreted by ChemScript: " + rec.Info.Error);
                structures.Add(rec);
            }
            long analyzeMs = swAnalyze.ElapsedMilliseconds;

            // ---- labels and property annotations ----
            List<Dictionary<string, object>> captionJson = new List<Dictionary<string, object>>();
            List<CaptionRec> caps = new List<CaptionRec>();
            foreach (XmlElement t in captions)
            {
                string text = Cdx.RawText(t).Trim();
                if (text.Length == 0) continue;
                double[] p = Cdx.ParsePoint(t.GetAttribute("p"));
                if (p == null) continue;
                CaptionRec c = new CaptionRec();
                c.Element = t;
                c.Text = text;
                c.X = p[0];
                c.Y = p[1];
                caps.Add(c);
                captionJson.Add(Json.Obj("x", Json.Round(c.X, 1), "y", Json.Round(c.Y, 1), "text", text));
            }
            AssignLabels(structures, caps, ConditionCaptions(doc, caps));

            // ---- reactions (arrows with structures on both sides) ----
            // (caption list reused below for arrow conditions)
            List<Dictionary<string, object>> reactionJson = new List<Dictionary<string, object>>();
            int arrowIdx = 0;
            foreach (XmlElement a in arrows)
            {
                arrowIdx++;
                double[] bb = Cdx.ParseBBox(a.GetAttribute("BoundingBox"));
                Dictionary<string, object> ar = Json.Obj();
                ar["arrow"] = arrowIdx;
                if (bb != null)
                {
                    ar["bbox"] = Json.Arr();
                    ((List<object>)ar["bbox"]).AddRange(new object[] { Json.Round(bb[0], 1), Json.Round(bb[1], 1), Json.Round(bb[2], 1), Json.Round(bb[3], 1) });

                    List<object> left = Json.Arr();
                    List<object> right = Json.Arr();
                    double arrowCenterY = (bb[1] + bb[3]) / 2.0;
                    // Only structures drawn immediately beside the arrow count: drawings are crowded,
                    // so a wide window would attach half the page to every arrow.
                    const double verticalWindow = 40;
                    const double horizontalGap = 40;
                    foreach (StructureRecord s in structures)
                    {
                        if (Math.Abs(s.CenterY - arrowCenterY) > verticalWindow) continue;
                        if (s.BBox[2] < bb[0] && bb[0] - s.BBox[2] <= horizontalGap) left.Add(s.Index);
                        else if (s.BBox[0] > bb[2] && s.BBox[0] - bb[2] <= horizontalGap) right.Add(s.Index);
                    }
                    ar["reactants"] = left;
                    ar["products"] = right;

                    List<object> conditions = Json.Arr();
                    foreach (CaptionRec c in caps)
                    {
                        if (IsPropertyCaption(c.Text)) continue;
                        if (c.X > bb[0] - 20 && c.X < bb[2] + 20 && c.Y > bb[1] - 25 && c.Y < bb[3] + 25)
                            conditions.Add(c.Text);
                    }
                    ar["conditions"] = conditions;
                }
                reactionJson.Add(ar);
            }

            // ---- thumbnails ----
            Dictionary<string, object> thumbs = Json.Obj();
            if (makeThumbnails && pngPath.Length > 0)
            {
                try
                {
                    CropThumbnails(doc, structures, pngPath, thumbs);
                }
                catch (Exception ex)
                {
                    _warnings.Add("thumbnail generation failed: " + ex.Message);
                }
            }

            // ---- validation ----
            Dictionary<string, object> validation = BuildValidation(structures, caps, Cdx.ParseBBox(doc.Page.GetAttribute("BoundingBox")));

            result["ok"] = true;
            result["file"] = inputPath;
            result["artifacts"] = Json.Obj(
                "cdxml", cdxmlPath,
                "png", pngPath,
                "thumbs", thumbs,
                "work_dir", _outDir);
            result["document"] = Json.Obj(
                "fragments", frags.Count,
                "captions", caps.Count,
                "arrows", arrows.Count,
                "bond_length", Json.Round(doc.BondLength, 2),
                "pages_wide", Math.Max(1, doc.Page.SelectNodes("/*/page").Count));
            result["structures"] = BuildStructureJson(structures);
            result["captions"] = captionJson;
            result["reactions"] = reactionJson;
            result["validation"] = validation;
            result["timing_ms"] = Json.Obj(
                "convert", convertMs,
                "analyze", analyzeMs,
                "total", total.ElapsedMilliseconds);
            return result;
        }

        public sealed class CaptionRec
        {
            public XmlElement Element;
            public string Text = "";
            public double X;
            public double Y;
        }

        // Written with \uFF1A instead of the literal full-width colon: the source stays pure ASCII,
        // so no encoding round-trip can ever corrupt these patterns again.
        private static readonly Regex PropertyRegex = new Regex(
            @"^\s*(Chemical\s+Formula|Molecular\s+Weight|Exact\s+Mass|m/z|Elemental\s+Analysis|Formula|MW|Log\s*P|tPSA|Mol\.?\s*Wt\.?)\s*[:\uFF1A]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LabelRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9\-_\.]{0,11}$", RegexOptions.Compiled);
        private static readonly Regex MwRegex = new Regex(@"Molecular\s+Weight\s*[:\uFF1A]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Label rule shared by the reader and the writers, so a written label is always readable.</summary>
        public static bool IsValidLabel(string label)
        {
            return !string.IsNullOrEmpty(label) && LabelRegex.IsMatch(label.Trim());
        }

        /// <summary>Label rule as user-facing text.</summary>
        public const string LabelRule = "1-12 characters, must start with a letter or digit, then letters, digits, '-' '_' or '.'";

        public static bool IsPropertyCaption(string text)
        {
            return PropertyRegex.IsMatch(text) || MwRegex.IsMatch(text);
        }

        /// <summary>Caption indices that sit in a reaction-condition position (above/below an arrow).</summary>
        private static HashSet<int> ConditionCaptions(Cdx doc, List<CaptionRec> caps)
        {
            HashSet<int> set = new HashSet<int>();
            foreach (XmlElement a in doc.Arrows())
            {
                double[] bb = Cdx.ParseBBox(a.GetAttribute("BoundingBox"));
                if (bb == null) continue;
                for (int i = 0; i < caps.Count; i++)
                {
                    CaptionRec c = caps[i];
                    // Only text sitting on the arrow line is a reaction condition; a label further
                    // above or below may still belong to a neighbouring structure.
                    if (c.X > bb[0] - 20 && c.X < bb[2] + 20 && c.Y > bb[1] - 25 && c.Y < bb[3] + 25) set.Add(i);
                }
            }
            return set;
        }

        public static void AssignLabels(Cdx doc, List<StructureRecord> structures)
        {
            List<CaptionRec> caps = new List<CaptionRec>();
            foreach (XmlElement t in doc.Captions())
            {
                string text = Cdx.RawText(t).Trim();
                if (text.Length == 0) continue;
                double[] p = Cdx.ParsePoint(t.GetAttribute("p"));
                if (p == null) continue;
                CaptionRec c = new CaptionRec();
                c.Element = t;
                c.Text = text;
                c.X = p[0];
                c.Y = p[1];
                caps.Add(c);
            }
            AssignLabels(structures, caps, ConditionCaptions(doc, caps));
        }

        private static readonly HashSet<string> ElementSymbols = new HashSet<string>(StringComparer.Ordinal)
        {
            "H","He","Li","Be","B","C","N","O","F","Ne","Na","Mg","Al","Si","P","S","Cl","Ar",
            "K","Ca","Sc","Ti","V","Cr","Mn","Fe","Co","Ni","Cu","Zn","Ga","Ge","As","Se","Br","Kr",
            "Rb","Sr","Y","Zr","Nb","Mo","Tc","Ru","Rh","Pd","Ag","Cd","In","Sn","Sb","Te","I","Xe",
            "Cs","Ba","La","Ce","Pr","Nd","Pm","Sm","Eu","Gd","Tb","Dy","Ho","Er","Tm","Yb","Lu",
            "Hf","Ta","W","Re","Os","Ir","Pt","Au","Hg","Tl","Pb","Bi","Po","At","Rn",
            "Fr","Ra","Ac","Th","Pa","U","Np","Pu","Am","Cm","Bk","Cf","Es","Fm","Md","No","Lr",
            "Rf","Db","Sg","Bh","Hs","Mt","Ds","Rg","Cn","Nh","Fl","Mc","Lv","Ts","Og"
        };

        /// <summary>False when a computed formula contains an impossible element symbol, which happens for unrecognised abbreviations.</summary>
        public static bool FormulaLooksValid(string formula)
        {
            if (string.IsNullOrEmpty(formula)) return false;
            MatchCollection parts = Regex.Matches(formula, @"([A-Z][a-z]?)(\d*)");
            int consumed = 0;
            foreach (Match m in parts)
            {
                consumed += m.Length;
                if (!ElementSymbols.Contains(m.Groups[1].Value)) return false;
            }
            return consumed == formula.Length;
        }

        private sealed class CaptionMatch
        {
            public double Dist;
            public int S;
            public int C;
            public bool IsMw;
            public double MwValue = double.NaN;
        }

        /// <summary>
        /// Caption geometry: a label or property block sits above, below or right of its structure.
        /// Anything further away belongs to a neighbour, not to this structure.
        /// </summary>
        private static bool SitsWith(StructureRecord s, CaptionRec c, double gap, double rightWidth)
        {
            const double xPad = 30;
            bool below = c.Y >= s.BBox[3] - 5 && c.Y <= s.BBox[3] + gap
                         && c.X >= s.BBox[0] - xPad && c.X <= s.BBox[2] + xPad;
            bool above = c.Y <= s.BBox[1] + 5 && c.Y >= s.BBox[1] - gap
                         && c.X >= s.BBox[0] - xPad && c.X <= s.BBox[2] + xPad;
            bool right = c.X >= s.BBox[2] - 5 && c.X <= s.BBox[2] + rightWidth
                         && c.Y >= s.BBox[1] - 20 && c.Y <= s.BBox[3] + 20;
            return below || above || right;
        }

        private static bool InsideBox(double[] b, double x, double y, double shrink)
        {
            return x >= b[0] + shrink && x <= b[2] - shrink && y >= b[1] + shrink && y <= b[3] - shrink;
        }

        /// <summary>Distance from a point to a rectangle; zero when the point is inside.</summary>
        public static double DistanceToBox(double[] b, double x, double y)
        {
            double dx = x < b[0] ? b[0] - x : (x > b[2] ? x - b[2] : 0);
            double dy = y < b[1] ? b[1] - y : (y > b[3] ? y - b[3] : 0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Pair captions with structures by position (below or right), one caption per structure slot.
        /// </summary>
        private static void AssignLabels(List<StructureRecord> structures, List<CaptionRec> caps, HashSet<int> excluded)
        {
            // Nearest-structure ownership: a caption belongs to the structure it is closest to,
            // so a neighbour drawn above it cannot steal a label. "Above" costs extra because
            // labels are normally placed under their structure.
            int[] nearestOf = new int[caps.Count];
            for (int ci = 0; ci < caps.Count; ci++)
            {
                nearestOf[ci] = -1;
                double best = double.MaxValue;
                for (int si = 0; si < structures.Count; si++)
                {
                    double score = DistanceToBox(structures[si].BBox, caps[ci].X, caps[ci].Y);
                    if (caps[ci].Y <= structures[si].BBox[1] + 5) score += 8;
                    if (score < best) { best = score; nearestOf[ci] = si; }
                }
            }

            List<CaptionMatch> candidates = new List<CaptionMatch>();
            for (int si = 0; si < structures.Count; si++)
            {
                StructureRecord s = structures[si];
                for (int ci = 0; ci < caps.Count; ci++)
                {
                    if (excluded.Contains(ci)) continue;
                    if (nearestOf[ci] >= 0 && nearestOf[ci] != si) continue;
                    CaptionRec c = caps[ci];
                    double dx = c.X - s.CenterX;
                    double dy = c.Y - s.CenterY;
                    double d = Math.Sqrt(dx * dx + dy * dy);

                    if (!IsPropertyCaption(c.Text))
                    {
                        string t = c.Text.Trim();
                        if (t.Length <= 12 && LabelRegex.IsMatch(t) && SitsWith(s, c, 50, 110))
                        {
                            CaptionMatch m = new CaptionMatch();
                            // Score by distance to the structure box, penalising text above it:
                            // a label under one structure is more likely its own than one above the next.
                            m.Dist = DistanceToBox(s.BBox, c.X, c.Y) + (c.Y <= s.BBox[1] + 5 ? 8 : 0);
                            m.S = si;
                            m.C = ci;
                            m.IsMw = false;
                            candidates.Add(m);
                        }
                    }

                    Match mw = MwRegex.Match(c.Text);
                    if (mw.Success)
                    {
                        double v;
                        if (double.TryParse(mw.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                            && SitsWith(s, c, 70, 130))
                        {
                            CaptionMatch m = new CaptionMatch();
                            m.Dist = DistanceToBox(s.BBox, c.X, c.Y) + 25;   // labels win ties
                            m.S = si;
                            m.C = ci;
                            m.IsMw = true;
                            m.MwValue = v;
                            candidates.Add(m);
                        }
                    }
                }
            }

            candidates.Sort(delegate(CaptionMatch a, CaptionMatch b) { return a.Dist.CompareTo(b.Dist); });

            bool[] captionUsed = new bool[caps.Count];
            bool[] labelDone = new bool[structures.Count];
            bool[] mwDone = new bool[structures.Count];

            foreach (CaptionMatch m in candidates)
            {
                if (captionUsed[m.C]) continue;
                if (m.IsMw)
                {
                    if (mwDone[m.S]) continue;
                    captionUsed[m.C] = true;
                    mwDone[m.S] = true;
                    structures[m.S].DrawnMw = m.MwValue;
                    structures[m.S].OwnedCaptions.Add(caps[m.C].Element);
                    if (string.IsNullOrEmpty(structures[m.S].AnnotationText))
                        structures[m.S].AnnotationText = caps[m.C].Text;
                }
                else
                {
                    if (labelDone[m.S]) continue;
                    captionUsed[m.C] = true;
                    labelDone[m.S] = true;
                    structures[m.S].Label = caps[m.C].Text.Trim();
                    structures[m.S].OwnedCaptions.Add(caps[m.C].Element);
                }
            }
        }

        public List<object> BuildStructureJson(List<StructureRecord> structures)
        {
            List<object> list = Json.Arr();
            foreach (StructureRecord s in structures)
            {
                Dictionary<string, object> o = Json.Obj();
                bool interpretable = s.Info.Ok && FormulaLooksValid(s.Info.Formula);
                o["id"] = s.Index;
                o["label"] = s.Label;
                o["smiles"] = s.Info.Smiles;
                o["inchi"] = s.Info.Inchi;
                // An unrecognised abbreviation group yields an impossible formula; report nothing rather than nonsense.
                o["formula"] = interpretable ? s.Info.Formula : null;
                o["mw"] = interpretable ? (object)Json.Round(s.Info.Mw, 2) : null;
                o["exact_mass"] = interpretable ? (object)Json.Round(s.Info.ExactMass, 4) : null;
                o["interpretable"] = interpretable;
                o["charge"] = s.Info.Charge;
                o["atoms"] = s.Info.Atoms;
                o["bonds"] = s.Info.Bonds;
                o["name"] = s.Info.Name;
                o["bbox"] = Json.Arr();
                ((List<object>)o["bbox"]).AddRange(new object[] {
                    Json.Round(s.BBox[0], 1), Json.Round(s.BBox[1], 1), Json.Round(s.BBox[2], 1), Json.Round(s.BBox[3], 1) });

                List<object> ez = Json.Arr();
                foreach (EzRec e in s.Ez)
                {
                    ez.Add(Json.Obj("nodes", e.A + "=" + e.B, "geometry_drawn", e.Geometry));
                }
                o["depiction"] = Json.Obj(
                    "bond_length", Json.Round(s.MedianBondLength, 2),
                    "bond_length_outliers", s.OutlierCount,
                    "wedge_bonds", s.Wedges,
                    "stereo_double_bonds", ez);

                o["annotation"] = Json.Obj(
                    "text", s.AnnotationText,
                    "mw_drawn", double.IsNaN(s.DrawnMw) ? (object)null : Json.Round(s.DrawnMw, 2),
                    "mw_matches", (double.IsNaN(s.DrawnMw) || !interpretable)
                        ? (object)null
                        : (object)(Math.Abs(s.DrawnMw - s.Info.Mw) < 0.06));

                if (!string.IsNullOrEmpty(s.ThumbPath)) o["thumbnail"] = s.ThumbPath;
                List<object> w = Json.Arr();
                foreach (string x in s.Warnings) w.Add(x);
                o["warnings"] = w;
                list.Add(o);
            }
            return list;
        }

        private Dictionary<string, object> BuildValidation(List<StructureRecord> structures, List<CaptionRec> caps, double[] pageBox)
        {
            int failed = 0, mwChecked = 0, mwMismatch = 0, ezLost = 0, outlierStructs = 0;
            List<object> warnings = Json.Arr();

            foreach (StructureRecord s in structures)
            {
                if (!s.Info.Ok)
                {
                    failed++;
                    warnings.Add(Json.Obj("code", "parse_failed", "structure", s.Index, "message", s.Info.Error));
                    continue;
                }

                if (!double.IsNaN(s.DrawnMw))
                {
                    mwChecked++;
                    if (Math.Abs(s.DrawnMw - s.Info.Mw) >= 0.06)
                    {
                        mwMismatch++;
                        warnings.Add(Json.Obj("code", "mw_mismatch", "structure", s.Index,
                            "message", "drawn " + s.DrawnMw.ToString("0.##", CultureInfo.InvariantCulture) +
                                       " vs computed " + s.Info.Mw.ToString("0.##", CultureInfo.InvariantCulture)));
                    }
                }

                // double-bond geometry exists in the drawing but the SMILES carries no stereo mark
                bool smilesHasStereo = s.Info.Smiles.IndexOf('/') >= 0 || s.Info.Smiles.IndexOf('\\') >= 0 || s.Info.Smiles.IndexOf('@') >= 0;
                if (s.Ez.Count > 0 && !smilesHasStereo)
                {
                    ezLost++;
                    warnings.Add(Json.Obj("code", "ez_only_in_drawing", "structure", s.Index,
                        "message", s.Ez.Count + " stereogenic double bond(s) visible in the drawing but absent from SMILES; use the CDXML coordinates"));
                }
                if (s.Wedges > 0 && s.Info.Smiles.IndexOf('@') < 0)
                {
                    ezLost++;
                    warnings.Add(Json.Obj("code", "wedge_lost", "structure", s.Index,
                        "message", s.Wedges + " wedge bond(s) drawn but SMILES carries no tetrahedral stereo"));
                }

                if (s.OutlierCount > 0)
                {
                    outlierStructs++;
                    warnings.Add(Json.Obj("code", "bond_length_outlier", "structure", s.Index,
                        "message", s.OutlierCount + " bond(s) deviate more than 30% from the median ("
                                   + s.MedianBondLength.ToString("0.##", CultureInfo.InvariantCulture) + ")"));
                }

                if (!FormulaLooksValid(s.Info.Formula))
                {
                    warnings.Add(Json.Obj("code", "formula_unexpected", "structure", s.Index,
                        "message", "computed formula '" + s.Info.Formula + "' contains an impossible element; "
                                   + "the drawing probably uses an abbreviation group ChemScript does not know"));
                }

                // Content outside the page still renders in the PNG but never prints; say so instead of
                // leaving the caller to wonder why a structure is "missing" from the sheet.
                if (pageBox != null && pageBox[2] > pageBox[0]
                    && (s.BBox[0] < pageBox[0] - 1 || s.BBox[1] < pageBox[1] - 1
                        || s.BBox[2] > pageBox[2] + 1 || s.BBox[3] > pageBox[3] + 1))
                {
                    warnings.Add(Json.Obj("code", "outside_page", "structure", s.Index,
                        "message", "structure lies outside the page bounds; it is visible in the rendered image but will not print"));
                }
            }

            if (structures.Count == 0)
                warnings.Add(Json.Obj("code", "no_structures", "message", "no top-level structure fragment was found"));

            // Duplicate labels make "--label" ambiguous for every write tool.
            Dictionary<string, List<int>> byLabel = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (StructureRecord s in structures)
            {
                if (string.IsNullOrEmpty(s.Label)) continue;
                List<int> ids;
                if (!byLabel.TryGetValue(s.Label, out ids)) { ids = new List<int>(); byLabel[s.Label] = ids; }
                ids.Add(s.Index);
            }
            foreach (KeyValuePair<string, List<int>> kv in byLabel)
            {
                if (kv.Value.Count < 2) continue;
                List<object> ids = Json.Arr();
                foreach (int id in kv.Value) ids.Add(id);
                warnings.Add(Json.Obj("code", "label_duplicate", "label", kv.Key,
                    "ids", ids,
                    "message", kv.Value.Count + " structures are labelled '" + kv.Key + "'; pass nth to pick one"));
            }

            Dictionary<string, object> v = Json.Obj();
            v["structures"] = structures.Count;
            v["parsed"] = structures.Count - failed;
            v["failed"] = failed;
            v["mw_checked"] = mwChecked;
            v["mw_mismatch"] = mwMismatch;
            v["stereo_warnings"] = ezLost;
            v["drawing_warnings"] = outlierStructs;
            v["ok"] = failed == 0 && mwMismatch == 0;
            v["warnings"] = warnings;
            return v;
        }

        // ---------------- thumbnails ----------------

        public sealed class Calibration
        {
            public double ScaleX, ScaleY, OffX, OffY;
            public int ImageWidth, ImageHeight;
        }

        /// <summary>Map document points onto the exported PNG using the ink bounding box of the drawing.</summary>
        public static Calibration Calibrate(string pngPath, double[] contentBox)
        {
            Calibration c = new Calibration();
            using (Bitmap bmp = new Bitmap(pngPath))
            {
                c.ImageWidth = bmp.Width;
                c.ImageHeight = bmp.Height;
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = data.Stride;
                byte[] bytes = new byte[stride * bmp.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                bmp.UnlockBits(data);

                int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
                bool any = false;
                for (int y = 0; y < bmp.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = row + x * 4;
                        if (bytes[i] < 245 || bytes[i + 1] < 245 || bytes[i + 2] < 245)
                        {
                            any = true;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                if (!any) { c.ScaleX = c.ScaleY = 1; return c; }

                double w = Math.Max(1e-6, contentBox[2] - contentBox[0]);
                double h = Math.Max(1e-6, contentBox[3] - contentBox[1]);
                c.ScaleX = (maxX - minX) / w;
                c.ScaleY = (maxY - minY) / h;
                // Offsets are the ink origin only: callers subtract contentBox[0] themselves.
                // Subtracting it here as well shifted every crop up/left by one content box.
                c.OffX = minX;
                c.OffY = minY;
            }
            return c;
        }

        public static double[] ContentBox(Cdx doc)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = -double.MaxValue, maxY = -double.MaxValue;
            bool any = false;
            XmlNodeList nodes = doc.Doc.SelectNodes("//fragment[@BoundingBox] | //page/t[@BoundingBox] | //page/arrow[@BoundingBox] | //page/graphic[@BoundingBox]");
            foreach (XmlNode n in nodes)
            {
                double[] b = Cdx.ParseBBox(((XmlElement)n).GetAttribute("BoundingBox"));
                if (b == null) continue;
                any = true;
                if (b[0] < minX) minX = b[0];
                if (b[1] < minY) minY = b[1];
                if (b[2] > maxX) maxX = b[2];
                if (b[3] > maxY) maxY = b[3];
            }
            if (!any) return new double[] { 0, 0, 1, 1 };
            return new double[] { minX, minY, maxX, maxY };
        }

        private void CropThumbnails(Cdx doc, List<StructureRecord> structures, string pngPath, Dictionary<string, object> thumbs)
        {
            double[] content = ContentBox(doc);
            Calibration cal = Calibrate(pngPath, content);
            using (Image src = Image.FromFile(pngPath))
            {
                double pad = 10;
                foreach (StructureRecord s in structures)
                {
                    double x0 = (s.BBox[0] - pad - content[0]) * cal.ScaleX + cal.OffX;
                    double y0 = (s.BBox[1] - pad - content[1]) * cal.ScaleY + cal.OffY;
                    double x1 = (s.BBox[2] + pad - content[0]) * cal.ScaleX + cal.OffX;
                    double y1 = (s.BBox[3] + pad - content[1]) * cal.ScaleY + cal.OffY;

                    int ix0 = Math.Max(0, (int)Math.Floor(x0));
                    int iy0 = Math.Max(0, (int)Math.Floor(y0));
                    int ix1 = Math.Min(src.Width, (int)Math.Ceiling(x1));
                    int iy1 = Math.Min(src.Height, (int)Math.Ceiling(y1));
                    int w = ix1 - ix0, h = iy1 - iy0;
                    if (w <= 2 || h <= 2)
                    {
                        // Never drop the thumbnail silently: keep a small blank marker.
                        _warnings.Add("structure " + s.Index + " lies outside the rendered page; its thumbnail is empty");
                        w = 8;
                        h = 8;
                        ix0 = Math.Max(0, Math.Min(src.Width - w, ix0));
                        iy0 = Math.Max(0, Math.Min(src.Height - h, iy0));
                    }

                    using (Bitmap crop = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(crop))
                        {
                            g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(ix0, iy0, w, h), GraphicsUnit.Pixel);
                        }
                        string path = Path.Combine(_outDir, "structure_" + s.Index.ToString("00") + ".png");
                        crop.Save(path, ImageFormat.Png);
                        s.ThumbPath = path;
                        thumbs["s" + s.Index] = path;
                    }
                }
            }
        }
    }
}
