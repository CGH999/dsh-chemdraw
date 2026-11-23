using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace ChemBridge
{
    public sealed class WriteOptions
    {
        public bool DryRun;
        public bool NoBackup;

        public static WriteOptions FromArgs(Dict args)
        {
            WriteOptions o = new WriteOptions();
            o.DryRun = args.Has("dry-run");
            o.NoBackup = args.Has("no-backup");
            return o;
        }
    }

    /// <summary>
    /// One editing session: load the document, apply operations on the CDXML model,
    /// write back through ChemDraw, and verify the result by re-reading it.
    /// </summary>
    public sealed class WriteSession : IDisposable
    {
        public string InputPath;
        public string WorkDir;
        public Cdx Doc;
        public Engine Engine;
        public string SourceCdxml;
        public List<StructureRecord> Current = new List<StructureRecord>();
        public List<string> ExpectedKeys = new List<string>();
        public List<Dictionary<string, object>> ChangeLog = new List<Dictionary<string, object>>();
        private int _maxIndex;
        /// <summary>True when this session created the target file, so there is nothing to back up.</summary>
        public bool CreatedNew;

        public static WriteSession Begin(string input, Engine engine, string workDir, bool createIfMissing)
        {
            string ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext != ".cdx" && ext != ".cdxml")
                throw new InvalidOperationException("Only .cdx and .cdxml can be edited");

            bool created = false;
            if (!File.Exists(input))
            {
                if (!createIfMissing)
                    throw new FileNotFoundException("file not found: " + input);
                string parent = Path.GetDirectoryName(Path.GetFullPath(input));
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    throw new DirectoryNotFoundException("folder not found: " + parent);
                CreateEmptyDocument(input, ext);
                created = true;
            }

            WriteSession s = new WriteSession();
            s.CreatedNew = created;
            s.InputPath = input;
            s.WorkDir = workDir;
            s.Engine = engine;
            if (!Directory.Exists(workDir)) Directory.CreateDirectory(workDir);

            if (ext == ".cdx")
            {
                string cdxml = Path.Combine(workDir, "orig.cdxml");
                Inspector.ExportFromCdx(input, cdxml, Path.Combine(workDir, "orig.png"), 300);
                s.SourceCdxml = cdxml;
            }
            else
            {
                s.SourceCdxml = input;
            }

            s.Doc = Cdx.Load(s.SourceCdxml);
            List<XmlElement> frags = s.Doc.Fragments();
            int i = 0;
            foreach (XmlElement f in frags)
            {
                i++;
                StructureRecord rec = new StructureRecord();
                rec.Index = i;
                rec.Fragment = f;
                rec.BBox = s.Doc.BBoxOf(f);
                rec.Graph = FragmentGraph.From(f);
                int dummy;
                List<BondRec> outliers;
                rec.MedianBondLength = rec.Graph.MedianBondLength(out outliers, out dummy);
                rec.OutlierCount = outliers.Count;
                rec.Ez = rec.Graph.StereoDoubleBonds();
                rec.Info = engine.AnalyzeCdxml(Inspector.WrapFragment(f, s.Doc.BondLength));
                s.Current.Add(rec);
                s.ExpectedKeys.Add(rec.Info.Key);
                s._maxIndex = i;
            }
            Inspector.AssignLabels(s.Doc, s.Current);
            return s;
        }

        /// <summary>Create a blank ChemDraw document at the given path (used when adding to a new file).</summary>
        private static void CreateEmptyDocument(string path, string ext)
        {
            using (ChemDraw app = ChemDraw.Acquire())
            {
                app.AddDocument();
                app.SaveAs(path, ext == ".cdx" ? "chemical/x-cdx" : "text/xml");
            }
            if (!File.Exists(path))
                throw new InvalidOperationException("ChemDraw did not create " + path);
        }

        // ---------------- operations ----------------

        public StructureRecord Find(int id)
        {
            foreach (StructureRecord r in Current) if (r.Index == id) return r;
            return null;
        }

        public StructureRecord FindByLabel(string label)
        {
            return FindByLabel(label, 1);
        }

        /// <summary>Nth (1-based) structure carrying this label; throws when the label is ambiguous or nth is out of range.</summary>
        public StructureRecord FindByLabel(string label, int nth)
        {
            List<StructureRecord> matches = new List<StructureRecord>();
            foreach (StructureRecord r in Current)
                if (string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)) matches.Add(r);
            if (matches.Count == 0) return null;
            if (nth < 1 || nth > matches.Count)
                throw new InvalidOperationException("label '" + label + "' matches " + matches.Count
                    + " structure(s) (ids " + IdList(matches) + "); nth=" + nth + " is out of range");
            return matches[nth - 1];
        }

        public int CountByLabel(string label)
        {
            int n = 0;
            foreach (StructureRecord r in Current)
                if (string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private static string IdList(List<StructureRecord> records)
        {
            List<string> ids = new List<string>();
            foreach (StructureRecord r in records) ids.Add("#" + r.Index);
            return string.Join(", ", ids.ToArray());
        }

        /// <summary>Reject labels that the reader could never pair back to a structure.</summary>
        public static string CheckLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            string trimmed = label.Trim();
            if (!Inspector.IsValidLabel(trimmed))
                throw new InvalidOperationException("invalid label '" + trimmed + "': " + Inspector.LabelRule);
            return trimmed;
        }

        public Dictionary<string, object> Delete(int id)
        {
            StructureRecord r = Find(id);
            if (r == null) throw new InvalidOperationException("structure " + id + " not found");
            Current.Remove(r);
            RemoveExpected(r.Info.Key);

            int captionsRemoved = 0;
            foreach (XmlElement cap in r.OwnedCaptions)
            {
                if (cap.ParentNode != null) { Doc.Remove(cap); captionsRemoved++; }
            }
            Doc.Remove(r.Fragment);

            // Property blocks ("Chemical Formula: ...") are often not paired to any single structure,
            // so remove the ones that clearly belonged to the structure that just disappeared.
            captionsRemoved += RemoveOrphansNear(r.BBox);

            Dictionary<string, object> change = Json.Obj(
                "op", "delete", "id", id, "label", r.Label,
                "inchi", r.Info.Inchi, "formula", r.Info.Formula, "mw", Json.Round(r.Info.Mw, 2),
                "captions_removed", captionsRemoved);
            ChangeLog.Add(change);
            return change;
        }

        public Dictionary<string, object> Add(string smiles, string label, double? atX, double? atY, bool withProps)
        {
            label = CheckLabel(label);
            StructureInfo info;
            string template = Path.Combine(WorkDir, "new_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cdxml");
            if (!Engine.GenerateCdxml(smiles, template, out info))
                throw new InvalidOperationException("could not build a structure from SMILES: " + smiles);

            XmlElement frag = Doc.AddFragmentFromCdxml(template, 100, 100);
            double[] b = Doc.BBoxOf(frag);
            double[] target;
            if (atX.HasValue && atY.HasValue) target = new double[] { atX.Value, atY.Value };
            else target = PlaceFree(Doc, b[2] - b[0], b[3] - b[1], frag);
            Doc.Translate(frag, target[0] - b[0], target[1] - b[1]);
            b = Doc.BBoxOf(frag);

            StructureRecord rec = new StructureRecord();
            rec.Index = ++_maxIndex;
            rec.Fragment = frag;
            rec.BBox = b;
            rec.Label = label ?? "";
            rec.Info = info;
            rec.Graph = FragmentGraph.From(frag);
            int dummy;
            List<BondRec> outliers;
            rec.MedianBondLength = rec.Graph.MedianBondLength(out outliers, out dummy);
            rec.Ez = rec.Graph.StereoDoubleBonds();
            Current.Add(rec);
            ExpectedKeys.Add(info.Key);

            if (!string.IsNullOrEmpty(rec.Label))
                rec.OwnedCaptions.Add(Doc.AddCaption(rec.Label, b[2] + 6, b[1] + (b[3] - b[1]) / 2));

            if (withProps)
            {
                rec.OwnedCaptions.Add(Doc.AddCaption("Chemical Formula: " + info.Formula, b[0], b[3] + 12));
                rec.OwnedCaptions.Add(Doc.AddCaption("Molecular Weight: " + info.Mw.ToString("0.##", CultureInfo.InvariantCulture), b[0], b[3] + 21));
            }

            Dictionary<string, object> change = Json.Obj(
                "op", "add", "id", rec.Index, "label", rec.Label, "smiles", smiles,
                "inchi", info.Inchi, "key", info.Key, "formula", info.Formula, "mw", Json.Round(info.Mw, 2),
                "placed_at", Json.Arr(), "bbox", BoxJson(b));
            ((List<object>)change["placed_at"]).AddRange(new object[] { Json.Round(b[0], 1), Json.Round(b[1], 1) });
            ChangeLog.Add(change);
            return change;
        }

        public Dictionary<string, object> Replace(int id, string smiles, string label, bool withProps)
        {
            label = CheckLabel(label);
            StructureRecord old = Find(id);
            if (old == null) throw new InvalidOperationException("structure " + id + " not found");

            StructureInfo info;
            string template = Path.Combine(WorkDir, "rep_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cdxml");
            if (!Engine.GenerateCdxml(smiles, template, out info))
                throw new InvalidOperationException("could not build a structure from SMILES: " + smiles);

            double[] oldBox = old.BBox;
            string oldInchi = old.Info.Inchi;
            string oldFormula = old.Info.Formula;

            Doc.Remove(old.Fragment);
            XmlElement frag = Doc.AddFragmentFromCdxml(template, oldBox[0], oldBox[1]);
            double[] b = Doc.BBoxOf(frag);

            StructureRecord rec = new StructureRecord();
            rec.Index = id;
            rec.Fragment = frag;
            rec.BBox = b;
            rec.Label = string.IsNullOrEmpty(label) ? old.Label : label;
            rec.Info = info;
            rec.Graph = FragmentGraph.From(frag);
            int dummy;
            List<BondRec> outliers;
            rec.MedianBondLength = rec.Graph.MedianBondLength(out outliers, out dummy);
            rec.Ez = rec.Graph.StereoDoubleBonds();

            Current[Current.IndexOf(old)] = rec;
            RemoveExpected(old.Info.Key);
            ExpectedKeys.Add(info.Key);

            rec.OwnedCaptions.AddRange(old.OwnedCaptions);
            int refreshed = RefreshPropertyCaptions(rec);
            refreshed += RefreshUnclaimedNearby(rec, oldBox);

            // Keep the label outside the new (possibly larger) structure.
            if (!string.IsNullOrEmpty(rec.Label))
            {
                double lx = b[2] + 6, ly = b[1] + (b[3] - b[1]) / 2;
                bool moved = false;
                foreach (XmlElement cap in rec.OwnedCaptions)
                {
                    if (cap.ParentNode == null) continue;
                    if (Cdx.RawText(cap).Trim() != rec.Label) continue;
                    Doc.MoveCaption(cap, lx, ly);
                    moved = true;
                    break;
                }
                if (!moved) rec.OwnedCaptions.Add(Doc.AddCaption(rec.Label, lx, ly));
            }

            Dictionary<string, object> change = Json.Obj(
                "op", "replace", "id", id, "label", rec.Label,
                "old_inchi", oldInchi, "old_formula", oldFormula,
                "inchi", info.Inchi, "key", info.Key, "formula", info.Formula, "mw", Json.Round(info.Mw, 2),
                "captions_refreshed", refreshed, "bbox", BoxJson(b));
            ChangeLog.Add(change);
            return change;
        }

        public Dictionary<string, object> Annotate(int id, string text, double? x, double? y)
        {
            Dictionary<string, object> change = Json.Obj("op", "annotate", "text", text);
            double px, py;
            if (id > 0)
            {
                StructureRecord r = Find(id);
                if (r == null) throw new InvalidOperationException("structure " + id + " not found");
                px = x ?? r.BBox[0];
                py = y ?? (r.BBox[3] + 12);
                change["id"] = id;
            }
            else
            {
                px = x ?? 40;
                py = y ?? 40;
            }
            Doc.AddCaption(text, px, py);
            change["at"] = Json.Arr();
            ((List<object>)change["at"]).AddRange(new object[] { Json.Round(px, 1), Json.Round(py, 1) });
            ChangeLog.Add(change);
            return change;
        }

        /// <summary>
        /// Remove property captions that are clearly closer to the deleted structure than to any
        /// surviving one, so deleting a structure does not leave a floating "Molecular Weight:".
        /// </summary>
        private int RemoveOrphansNear(double[] box)
        {
            int removed = 0;
            List<XmlElement> candidates = new List<XmlElement>(Doc.Captions());
            foreach (XmlElement cap in candidates)
            {
                if (cap.ParentNode == null) continue;
                string text = Cdx.RawText(cap);
                if (!Inspector.IsPropertyCaption(text)) continue;
                double[] p = Cdx.ParsePoint(cap.GetAttribute("p"));
                if (p == null) continue;

                bool claimed = false;
                foreach (StructureRecord other in Current)
                {
                    foreach (XmlElement owned in other.OwnedCaptions)
                        if (ReferenceEquals(owned, cap)) { claimed = true; break; }
                    if (claimed) break;
                }
                if (claimed) continue;

                double deletedDistance = Inspector.DistanceToBox(box, p[0], p[1]);
                double survivingDistance = double.MaxValue;
                foreach (StructureRecord other in Current)
                {
                    double d = Inspector.DistanceToBox(other.BBox, p[0], p[1]);
                    if (d < survivingDistance) survivingDistance = d;
                }

                if (deletedDistance + 5 < survivingDistance)
                {
                    Doc.Remove(cap);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>Move a structure (and the captions it owns) by a delta, or to an absolute top-left corner.</summary>
        public Dictionary<string, object> Move(int id, double? dx, double? dy, double? toX, double? toY)
        {
            StructureRecord r = Find(id);
            if (r == null) throw new InvalidOperationException("structure " + id + " not found");

            double[] b = Doc.BBoxOf(r.Fragment);
            double nx = toX ?? (b[0] + (dx ?? 0));
            double ny = toY ?? (b[1] + (dy ?? 0));
            double shiftX = nx - b[0];
            double shiftY = ny - b[1];

            Doc.Translate(r.Fragment, shiftX, shiftY);
            foreach (XmlElement cap in r.OwnedCaptions)
            {
                if (cap.ParentNode == null) continue;
                double[] p = Cdx.ParsePoint(cap.GetAttribute("p"));
                if (p == null) continue;
                Doc.MoveCaption(cap, p[0] + shiftX, p[1] + shiftY);
            }

            Dictionary<string, object> change = Json.Obj(
                "op", "move", "id", id, "label", r.Label,
                "dx", Json.Round(shiftX, 1), "dy", Json.Round(shiftY, 1),
                "bbox", Json.Arr());
            ((List<object>)change["bbox"]).AddRange(new object[] {
                Json.Round(nx, 1), Json.Round(ny, 1),
                Json.Round(nx + (b[2] - b[0]), 1), Json.Round(ny + (b[3] - b[1]), 1) });
            ChangeLog.Add(change);
            return change;
        }

        /// <summary>Add a reaction arrow.</summary>
        public Dictionary<string, object> Arrow(double x0, double y0, double x1, double y1)
        {
            Doc.AddArrow(x0, y0, x1, y1);
            Dictionary<string, object> change = Json.Obj(
                "op", "arrow",
                "from", Json.Arr(), "to", Json.Arr());
            ((List<object>)change["from"]).AddRange(new object[] { Json.Round(x0, 1), Json.Round(y0, 1) });
            ((List<object>)change["to"]).AddRange(new object[] { Json.Round(x1, 1), Json.Round(y1, 1) });
            ChangeLog.Add(change);
            return change;
        }

        /// <summary>Regex.Replace that never takes the whole edit down with it.</summary>
        private static string SafeReplace(string input, string pattern, string replacement)
        {
            try
            {
                return Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return input;
            }
        }

        /// <summary>Rewrite the "Chemical Formula" / "Molecular Weight" captions that belong to a structure.</summary>
        public int RefreshPropertyCaptions(StructureRecord r)
        {
            int n = 0;
            foreach (XmlElement t in r.OwnedCaptions)
            {
                if (t.ParentNode == null) continue;
                string text = Cdx.RawText(t);
                if (!Inspector.IsPropertyCaption(text)) continue;

                string updated = text;
                if (r.Info.Ok)
                {
                    updated = SafeReplace(updated, @"(Chemical\s+Formula\s*[:\uFF1A]\s*)\S+", "${1}" + r.Info.Formula);
                    updated = SafeReplace(updated, @"(Molecular\s+Weight\s*[:\uFF1A]\s*)[0-9]+(?:\.[0-9]+)?",
                        "${1}" + r.Info.Mw.ToString("0.##", CultureInfo.InvariantCulture));
                }
                if (updated != text)
                {
                    Doc.SetCaptionText(t, updated);
                    n++;
                }
            }
            return n;
        }

        /// <summary>Rewrite property captions that sit near a replaced structure and belong to no other structure.</summary>
        private int RefreshUnclaimedNearby(StructureRecord r, double[] box)
        {
            HashSet<XmlElement> claimed = new HashSet<XmlElement>();
            foreach (StructureRecord o in Current)
                foreach (XmlElement c in o.OwnedCaptions) claimed.Add(c);

            int n = 0;
            double cx = (box[0] + box[2]) / 2, cy = (box[1] + box[3]) / 2;
            foreach (XmlElement t in Doc.Captions())
            {
                if (claimed.Contains(t)) continue;
                double[] p = Cdx.ParsePoint(t.GetAttribute("p"));
                if (p == null) continue;
                double d = Math.Sqrt((p[0] - cx) * (p[0] - cx) + (p[1] - cy) * (p[1] - cy));
                if (d > 150) continue;
                string text = Cdx.RawText(t);
                if (!Inspector.IsPropertyCaption(text) || !r.Info.Ok) continue;

                string updated = SafeReplace(text, @"(Chemical\s+Formula\s*[:\uFF1A]\s*)\S+", "${1}" + r.Info.Formula);
                updated = SafeReplace(updated, @"(Molecular\s+Weight\s*[:\uFF1A]\s*)[0-9]+(?:\.[0-9]+)?",
                    "${1}" + r.Info.Mw.ToString("0.##", CultureInfo.InvariantCulture));
                if (updated != text)
                {
                    Doc.SetCaptionText(t, updated);
                    r.OwnedCaptions.Add(t);
                    n++;
                }
            }
            return n;
        }

        private void RemoveExpected(string key)
        {
            // An empty key used to return early, so deleting a structure without an InChI left a
            // phantom entry behind and every edit failed verification.
            for (int i = 0; i < ExpectedKeys.Count; i++)
            {
                if (string.Equals(ExpectedKeys[i], key, StringComparison.Ordinal))
                {
                    ExpectedKeys.RemoveAt(i);
                    return;
                }
            }
        }

        // ---------------- placement ----------------

        /// <summary>Find a free spot for a w x h box, avoiding every existing object.</summary>
        public static double[] PlaceFree(Cdx doc, double w, double h, XmlElement exclude)
        {
            List<double[]> occupied = new List<double[]>();
            XmlNodeList nodes = doc.Doc.SelectNodes("//fragment[@BoundingBox] | //page/t[@BoundingBox] | //page/arrow[@BoundingBox] | //page/graphic[@BoundingBox]");
            foreach (XmlNode n in nodes)
            {
                XmlElement e = n as XmlElement;
                if (e == null || ReferenceEquals(e, exclude)) continue;
                if (exclude != null && IsInside(e, exclude)) continue;
                double[] b = Cdx.ParseBBox(e.GetAttribute("BoundingBox"));
                if (b != null) occupied.Add(b);
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = -double.MaxValue, maxY = -double.MaxValue;
            foreach (double[] b in occupied)
            {
                if (b[0] < minX) minX = b[0];
                if (b[1] < minY) minY = b[1];
                if (b[2] > maxX) maxX = b[2];
                if (b[3] > maxY) maxY = b[3];
            }
            if (occupied.Count == 0) return new double[] { 100, 100 };

            List<double[]> candidates = new List<double[]>();
            candidates.Add(new double[] { maxX + 60, minY });
            candidates.Add(new double[] { minX, maxY + 70 });
            candidates.Add(new double[] { maxX + 60, maxY });
            for (double y = minY; y < maxY + 600; y += 40)
                for (double x = minX; x < Math.Max(maxX + 400, 1200); x += 40)
                    candidates.Add(new double[] { x, y });

            foreach (double[] c in candidates)
            {
                double pageWidth = doc.PageWidth;
                if (c[0] + w > pageWidth - 10 && c[0] > minX) continue;   // keep it on the page
                bool clash = false;
                foreach (double[] b in occupied)
                {
                    double ox = Math.Min(c[0] + w, b[2]) - Math.Max(c[0], b[0]);
                    double oy = Math.Min(c[1] + h, b[3]) - Math.Max(c[1], b[1]);
                    if (ox > 0 && oy > 0 && ox * oy > 60) { clash = true; break; }
                }
                if (!clash) return c;
            }
            return new double[] { maxX + 60, maxY + 70 };
        }

        /// <summary>True when e is the ancestor element itself or a descendant of it.</summary>
        private static bool IsInside(XmlElement e, XmlElement ancestor)
        {
            XmlNode cur = e;
            while (cur != null)
            {
                if (ReferenceEquals(cur, ancestor)) return true;
                cur = cur.ParentNode;
            }
            return false;
        }

        // ---------------- commit ----------------
        public Dictionary<string, object> Commit(WriteOptions options)
        {
            string editedCdxml = Path.Combine(WorkDir, "edited.cdxml");
            Doc.Save(editedCdxml);

            bool isCdx = Path.GetExtension(InputPath).ToLowerInvariant() == ".cdx";
            string produced;
            string inspectTarget;

            if (isCdx)
            {
                produced = Path.Combine(WorkDir, "edited.cdx");
                if (File.Exists(produced)) File.Delete(produced);
                using (ChemDraw app = ChemDraw.Acquire())
                {
                    app.Open(editedCdxml);
                    app.SaveAs(produced, "chemical/x-cdx");
                }
                inspectTarget = produced;
            }
            else
            {
                produced = editedCdxml;
                inspectTarget = editedCdxml;
            }

            // Re-read what was actually produced, using the same path the reader uses.
            string verifyDir = Path.Combine(WorkDir, "verify");
            if (!Directory.Exists(verifyDir)) Directory.CreateDirectory(verifyDir);
            string verifyCdxml;
            if (isCdx) Inspector.ExportFromCdx(produced, Path.Combine(verifyDir, "verify.cdxml"), Path.Combine(verifyDir, "verify.png"), 200);
            else verifyCdxml = produced;
            verifyCdxml = isCdx ? Path.Combine(verifyDir, "verify.cdxml") : produced;

            Cdx check = Cdx.Load(verifyCdxml);
            List<string> actual = new List<string>();
            List<KeyValuePair<int, string>> actualById = new List<KeyValuePair<int, string>>();
            Dictionary<int, double[]> producedBox = new Dictionary<int, double[]>();
            int fragmentIndex = 0;
            foreach (XmlElement f in check.Fragments())
            {
                fragmentIndex++;
                StructureInfo si = Engine.AnalyzeCdxml(Inspector.WrapFragment(f, check.BondLength));
                if (si.Ok)
                {
                    actual.Add(si.Key);
                    actualById.Add(new KeyValuePair<int, string>(fragmentIndex, si.Key));
                    producedBox[fragmentIndex] = check.BBoxOf(f);
                }
            }

            List<string> missing, unexpected;
            string mode = "structure key";
            MultisetDiff(ExpectedKeys, actual, out missing, out unexpected);
            if (missing.Count > 0 || unexpected.Count > 0)
            {
                List<string> expSkel = new List<string>();
                foreach (string s in ExpectedKeys) expSkel.Add(Skeleton(s));
                List<string> actSkel = new List<string>();
                foreach (string s in actual) actSkel.Add(Skeleton(s));
                List<string> m2, u2;
                MultisetDiff(expSkel, actSkel, out m2, out u2);
                if (m2.Count == 0 && u2.Count == 0) { mode = "structure key (skeleton)"; missing.Clear(); unexpected.Clear(); }
            }

            bool verified = missing.Count == 0 && unexpected.Count == 0;

            // ChemDraw re-orders fragments when it saves, so an in-memory id is not the saved id.
            // Resolve each change to the produced id by position first (drawings keep their coordinates),
            // and fall back to InChI only when the change carries no box.
            Dictionary<string, List<int>> idsByInchi = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, string> kv in actualById)
            {
                List<int> ids;
                if (!idsByInchi.TryGetValue(kv.Value, out ids)) { ids = new List<int>(); idsByInchi[kv.Value] = ids; }
                ids.Add(kv.Key);
            }

            foreach (Dictionary<string, object> change in ChangeLog)
            {
                string key = null;
                object rawKey, rawInchi;
                if (change.TryGetValue("key", out rawKey)) key = rawKey as string;
                if (string.IsNullOrEmpty(key) && change.TryGetValue("inchi", out rawInchi)) key = rawInchi as string;
                if (string.IsNullOrEmpty(key)) continue;

                List<int> ids;
                if (!idsByInchi.TryGetValue(key, out ids) || ids.Count == 0) continue;

                List<object> box = change.ContainsKey("bbox") ? change["bbox"] as List<object> : null;
                int chosen = -1;
                double chosenDistance = double.MaxValue;
                if (box != null && box.Count == 4)
                {
                    double bx = (Convert.ToDouble(box[0], CultureInfo.InvariantCulture) + Convert.ToDouble(box[2], CultureInfo.InvariantCulture)) / 2;
                    double by = (Convert.ToDouble(box[1], CultureInfo.InvariantCulture) + Convert.ToDouble(box[3], CultureInfo.InvariantCulture)) / 2;
                    foreach (int id in ids)
                    {
                        double[] fb;
                        if (!producedBox.TryGetValue(id, out fb) || fb == null) continue;
                        double fx = (fb[0] + fb[2]) / 2, fy = (fb[1] + fb[3]) / 2;
                        double d = Math.Sqrt((fx - bx) * (fx - bx) + (fy - by) * (fy - by));
                        if (d < chosenDistance) { chosenDistance = d; chosen = id; }
                    }
                }

                if (chosen > 0 && chosenDistance <= 40)
                {
                    change["id_after_save"] = chosen;
                    change["id_match"] = "position";
                }
                else
                {
                    change["id_after_save"] = ids[ids.Count - 1];
                    change["id_match"] = ids.Count > 1 ? "inchi_ambiguous" : "inchi";
                }

                if (ids.Count > 1)
                {
                    List<object> all = Json.Arr();
                    foreach (int id in ids) all.Add(id);
                    change["same_inchi_ids"] = all;
                }
            }
            Dictionary<string, object> verification = Json.Obj(
                "expected", ExpectedKeys.Count,
                "actual", actual.Count,
                "verified", verified,
                "compared_by", mode,
                "missing", ToJsonArray(missing),
                "unexpected", ToJsonArray(unexpected));

            Dictionary<string, object> result = Json.Obj();
            result["ok"] = verified;
            result["dry_run"] = options.DryRun;
            result["applied"] = false;
            result["file"] = InputPath;
            result["changes"] = ChangeLog;
            result["verification"] = verification;
            result["artifacts"] = Json.Obj(
                "edited_cdx", produced,
                "edited_cdxml", editedCdxml,
                "preview_png", Path.Combine(verifyDir, "verify.png"));

            if (!verified)
            {
                result["error"] = "verification failed; the file was left untouched. missing=" + missing.Count + " unexpected=" + unexpected.Count;
                return result;
            }

            if (!options.DryRun)
            {
                string backup = "";
                try
                {
                    if (!options.NoBackup && !CreatedNew)
                    {
                        backup = InputPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                        File.Copy(InputPath, backup, true);
                    }
                    string dir = Path.GetDirectoryName(Path.GetFullPath(InputPath));
                    string tmp = Path.Combine(dir, "." + Path.GetFileName(InputPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp");
                    File.Copy(produced, tmp, true);
                    try
                    {
                        File.Replace(tmp, InputPath, null);
                    }
                    catch
                    {
                        File.Copy(tmp, InputPath, true);
                        try { File.Delete(tmp); } catch { }
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result["ok"] = false;
                    result["error"] = "the target file could not be written (" + ex.Message + "). "
                        + "This session is restricted to its workspace: copy the .cdx into the workspace, "
                        + "or run the session with full file access. The original file was not modified.";
                    try { if (backup.Length > 0 && File.Exists(backup)) File.Delete(backup); } catch { }
                    return result;
                }
                catch (IOException ex)
                {
                    result["ok"] = false;
                    result["error"] = "the target file could not be written (" + ex.Message + "). "
                        + "It may be open in ChemDraw. The original file was not modified.";
                    return result;
                }
                result["applied"] = true;
                result["backup"] = backup;
                TrimBackups(InputPath, 3);
            }

            return result;
        }

        private static List<object> BoxJson(double[] b)
        {
            List<object> l = Json.Arr();
            l.AddRange(new object[] { Json.Round(b[0], 1), Json.Round(b[1], 1), Json.Round(b[2], 1), Json.Round(b[3], 1) });
            return l;
        }

        /// <summary>Keep only the newest few backups so repeated edits do not litter the folder.</summary>
        private static void TrimBackups(string input, int keep)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(input));
                if (string.IsNullOrEmpty(dir)) return;
                string[] files = Directory.GetFiles(dir, Path.GetFileName(input) + ".bak.*");
                if (files.Length <= keep) return;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length - keep; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static string Skeleton(string inchi)
        {
            if (string.IsNullOrEmpty(inchi)) return "";
            int slash = inchi.IndexOf('/');
            return slash < 0 ? inchi : inchi.Substring(0, slash);
        }

        private static List<object> ToJsonArray(List<string> items)
        {
            List<object> a = Json.Arr();
            foreach (string s in items) a.Add(s);
            return a;
        }

        private static void MultisetDiff(List<string> expected, List<string> actual, out List<string> missing, out List<string> unexpected)
        {
            List<string> pool = new List<string>(actual);
            missing = new List<string>();
            foreach (string e in expected)
            {
                int idx = pool.IndexOf(e);
                if (idx >= 0) pool.RemoveAt(idx);
                else missing.Add(e);
            }
            unexpected = pool;
        }

        public void Dispose()
        {
        }
    }
}
