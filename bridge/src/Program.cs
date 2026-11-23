using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using CambridgeSoft.ChemScript;

namespace ChemBridge
{
    /// <summary>Simple "--key value" / "--flag" argument container.</summary>
    public sealed class Dict : Dictionary<string, string>
    {
        public Dict() : base(StringComparer.OrdinalIgnoreCase) { }

        public bool Has(string key)
        {
            return ContainsKey(key);
        }

        public string Get(string key, string fallback)
        {
            string v;
            return TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            string v = Get(key, null);
            int i;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            return fallback;
        }

        public double GetDouble(string key, double fallback)
        {
            string v = Get(key, null);
            double d;
            if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }
    }

    public static class Program
    {
        /// <summary>
        /// Real stdout. The ChemScript engine prints a banner on first use, so Console.Out is muted.
        /// Initialised to TextWriter.Null: when the host runs us without console handles, touching
        /// Console.Out in a static initialiser would throw before Main can catch it.
        /// </summary>
        private static TextWriter _stdout = TextWriter.Null;

        /// <summary>When set, the JSON payload is written here instead of stdout (native engine chatter is unavoidable).</summary>
        private static string _jsonOut;

        [STAThread]
        public static int Main(string[] args)
        {
            // A windowless host (Electron) has no console: setting the encoding there throws.
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }
            try
            {
                _stdout = Console.Out;
                Console.SetOut(TextWriter.Null);
            }
            catch { _stdout = TextWriter.Null; }

            string command = args.Length > 0 ? args[0] : "help";

            try
            {
                Dict options;
                string input;
                ParseArgs(args, out options, out input);
                _jsonOut = options.Get("json-out", null);

                if (command == "help" || command == "--help" || command == "-h")
                {
                    Emit(BuildHelp());
                    return 0;
                }

                ChemPaths paths = ChemPaths.Resolve();
                paths.HookAssemblyResolver();
                if (command != "doctor" && string.IsNullOrEmpty(input))
                    throw new InvalidOperationException("missing input file");

                if (command == "doctor")
                {
                    Emit(Doctor(paths));
                    return 0;
                }

                if (!File.Exists(input) && command != "add" && command != "edit")
                    throw new FileNotFoundException("file not found: " + input);

                string workDir = options.Get("out", DefaultWorkDir(input));
                if (!IsAscii(workDir))
                {
                    // ChemDraw's COM SaveAs cannot write into a non-ASCII directory, so fall back.
                    string fallback = Path.Combine(WorkRoot(), "DSH-ChemDraw",
                        AsciiSlug(Path.GetFileNameWithoutExtension(input)) + "_" +
                        Fnv(Path.GetFullPath(input).ToLowerInvariant()).ToString("x8"));
                    Console.Error.WriteLine("note: output directory contains non-ASCII characters; using " + fallback);
                    workDir = fallback;
                }
                Engine engine = new Engine(workDir);

                switch (command)
                {
                    case "inspect":
                        {
                            Inspector inspector = new Inspector(paths, workDir, engine);
                            bool thumbs = !options.Has("no-thumbs");
                            Emit(inspector.Run(input, thumbs));
                            return 0;
                        }
                    case "render":
                        {
                            List<string> ids = ParseIds(options.Get("ids", ""));
                            Inspector inspector = new Inspector(paths, workDir, engine);
                            Dictionary<string, object> data = inspector.Run(input, true);
                            Dictionary<string, object> result = Json.Obj();
                            result["ok"] = true;
                            result["file"] = input;
                            result["artifacts"] = data["artifacts"];
                            List<object> picked = Json.Arr();
                            List<object> all = (List<object>)data["structures"];
                            foreach (object o in all)
                            {
                                Dictionary<string, object> s = (Dictionary<string, object>)o;
                                if (ids.Count > 0 && !ids.Contains(Convert.ToString(s["id"]))) continue;
                                picked.Add(Json.Obj("id", s["id"], "label", s["label"],
                                    "thumbnail", s.ContainsKey("thumbnail") ? s["thumbnail"] : null));
                            }
                            result["structures"] = picked;
                            if (ids.Count > 0 && picked.Count == 0)
                            {
                                result["ok"] = false;
                                result["error"] = "no structure has id " + string.Join(",", ids.ToArray())
                                    + "; this document has " + all.Count + " structure(s), ids 1.." + all.Count;
                                result["structures"] = Json.Arr();
                            }
                            Emit(result);
                            return result["ok"] is bool && (bool)result["ok"] ? 0 : 2;
                        }
                    case "threed":
                        {
                            Emit(ThreeD(paths, engine, input, workDir, ParseIds(options.Get("ids", ""))));
                            return 0;
                        }
                    case "find":
                        {
                            Emit(Find(paths, engine, input, workDir, options));
                            return 0;
                        }
                    case "add":
                    case "delete":
                    case "replace":
                    case "annotate":
                    case "move":
                    case "arrow":
                    case "edit":
                        {
                            WriteOptions wo = WriteOptions.FromArgs(options);
                            Dictionary<string, object> result = RunWrite(command, input, engine, workDir, options, wo);
                            Emit(result);
                            return Convert.ToBoolean(result["ok"]) ? 0 : 2;
                        }
                    default:
                        Emit(Json.Obj("ok", false, "error", "unknown command: " + command));
                        return 2;
                }
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Dictionary<string, object> error = Json.Obj();
                error["ok"] = false;
                error["error"] = Describe(ex);
                error["command"] = command;
                error["type"] = ex.GetType().Name;
                Emit(error);
                return 1;
            }
        }

        /// <summary>Exception text that survives assembly-load and localization failures.</summary>
        private static string Describe(Exception ex)
        {
            try { return ex.Message; }
            catch { return ex.GetType().FullName ?? "unknown exception"; }
        }

        /// <summary>Append a crash record next to the temp directory; never throws.</summary>
        private static void LogCrash(Exception ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("---- " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                for (Exception e = ex; e != null; e = e.InnerException)
                {
                    string type = "?";
                    string message = "?";
                    try { type = e.GetType().FullName; } catch { }
                    try { message = e.Message; } catch { }
                    sb.AppendLine(type + ": " + message);
                    try { sb.AppendLine(e.StackTrace); } catch { }
                }
                File.AppendAllText(Path.Combine(WorkRoot(), "ChemBridge-crash.log"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void Emit(Dictionary<string, object> payload)
        {
            string json = Json.Stringify(payload);
            if (!string.IsNullOrEmpty(_jsonOut))
            {
                File.WriteAllText(_jsonOut, json, new UTF8Encoding(false));
            }
            else
            {
                _stdout.Write(json);
            }
            _stdout.Flush();
        }

        private static readonly HashSet<string> ValueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "out", "json-out", "ids", "smiles", "label", "index", "at", "text", "ops", "dpi", "home",
            "dx", "dy", "from", "to", "nth", "formula"
        };

        /// <summary>Parse "--key value" flags anywhere after the command; the first bare token is the input file.</summary>
        private static void ParseArgs(string[] args, out Dict options, out string input)
        {
            options = new Dict();
            input = null;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    string key = a.Substring(2);
                    string value = "true";
                    if (ValueFlags.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        value = args[i + 1];
                        i++;
                    }
                    options[key] = value;
                }
                else if (input == null)
                {
                    input = a;
                }
            }
        }

        private static List<string> ParseIds(string spec)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrEmpty(spec)) return ids;
            foreach (string part in spec.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                ids.Add(part.Trim());
            return ids;
        }

        private static uint Fnv(string s)
        {
            uint hash = 2166136261;
            foreach (char c in s)
            {
                hash ^= (byte)(c & 0xff);
                hash *= 16777619;
                hash ^= (byte)(c >> 8);
                hash *= 16777619;
            }
            return hash;
        }

        private static string Sanitize(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = true;
                foreach (char b in bad) if (c == b) { ok = false; break; }
                sb.Append(ok && c != ' ' ? c : '_');
            }
            string s = sb.ToString();
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

        public static string DefaultWorkDir(string input)
        {
            string root = System.Environment.GetEnvironmentVariable("CHEMDRAW_WORK_ROOT");
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            string full;
            try { full = Path.GetFullPath(input); }
            catch { full = input; }
            string name = AsciiSlug(Path.GetFileNameWithoutExtension(full));
            return Path.Combine(root, "DSH-ChemDraw", name + "_" + Fnv(full.ToLowerInvariant()).ToString("x8"));
        }

        /// <summary>Root for scratch data; the host may restrict the system temp directory.</summary>
        public static string WorkRoot()
        {
            string root = System.Environment.GetEnvironmentVariable("CHEMDRAW_WORK_ROOT");
            return string.IsNullOrEmpty(root) ? Path.GetTempPath() : root;
        }

        public static bool IsAscii(string s)
        {
            foreach (char c in s) if (c > 127) return false;
            return true;
        }

        private static string AsciiSlug(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = true;
                foreach (char b in bad) if (c == b) { ok = false; break; }
                if (c > 127) ok = false;
                sb.Append(ok && c != ' ' ? c : '_');
            }
            string s = sb.ToString().Trim('_');
            if (s.Length == 0) s = "doc";
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

        private static Dictionary<string, object> Doctor(ChemPaths paths)
        {
            Dictionary<string, object> d = Json.Obj();
            d["ok"] = true;
            d["chemdraw_home"] = paths.Home;
            d["chemdraw_exe"] = Path.Combine(paths.ChemDrawExeDir, "ChemDraw.exe");

            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(Path.Combine(paths.ChemDrawExeDir, "ChemDraw.exe"));
                d["chemdraw_version"] = vi.FileVersion;
            }
            catch { d["chemdraw_version"] = "unknown"; }

            bool running = false;
            try
            {
                object existing = System.Runtime.InteropServices.Marshal.GetActiveObject(ChemDraw.ProgId);
                running = existing != null;
            }
            catch { running = false; }
            d["preexisting_instance"] = running;

            d["chemscript_activated"] = CambridgeSoft.ChemScript.Environment.IsActivated();            string version = CambridgeSoft.ChemScript.Environment.Version();
            if (version != null) version = version.Split('\n')[0].Trim();
            d["chemscript_version"] = version;

            try
            {
                StructureData sd = new StructureData();
                sd.ReadData("CCO", "chemical/x-smiles");
                d["self_test_smiles"] = sd.Smiles;
                d["self_test_mw"] = Json.Round(sd.Weight, 2);
            }
            catch (Exception ex)
            {
                d["self_test_error"] = ex.Message;
            }

            // Guard against the encoding regression that once broke caption rewriting:
            // these patterns must recognise both ':' and the full-width '\uFF1A'.
            d["caption_regex_ok"] = Inspector.IsPropertyCaption("Molecular Weight\uFF1A1.00")
                                    && Inspector.IsPropertyCaption("Chemical Formula: C2H6O");
            d["label_rule"] = Inspector.LabelRule;
            return d;
        }

        /// <summary>List structures matching a label / id / formula / SMILES fragment.</summary>
        private static Dictionary<string, object> Find(ChemPaths paths, Engine engine, string input, string workDir, Dict options)
        {
            string label = options.Get("label", null);
            string formula = options.Get("formula", null);
            string smiles = options.Get("smiles", null);
            int index = options.GetInt("index", 0);

            Inspector inspector = new Inspector(paths, workDir, engine);
            Dictionary<string, object> data = inspector.Run(input, true);

            List<object> matches = Json.Arr();
            foreach (object entry in (List<object>)data["structures"])
            {
                Dictionary<string, object> s = (Dictionary<string, object>)entry;
                if (index > 0 && Convert.ToInt32(s["id"], CultureInfo.InvariantCulture) != index) continue;
                if (label != null && !string.Equals(Convert.ToString(s["label"], CultureInfo.InvariantCulture), label, StringComparison.OrdinalIgnoreCase)) continue;
                if (formula != null && Convert.ToString(s["formula"] ?? "", CultureInfo.InvariantCulture).IndexOf(formula, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (smiles != null && Convert.ToString(s["smiles"] ?? "", CultureInfo.InvariantCulture).IndexOf(smiles, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(s);
            }

            Dictionary<string, object> result = Json.Obj();
            result["ok"] = true;
            result["file"] = input;
            result["filter"] = Json.Obj(
                "label", label,
                "index", index > 0 ? (object)index : null,
                "formula", formula,
                "smiles", smiles);
            result["match_count"] = matches.Count;
            result["matches"] = matches;
            result["artifacts"] = data["artifacts"];
            return result;
        }

        private static Dictionary<string, object> ThreeD(ChemPaths paths, Engine engine, string input, string workDir, List<string> ids)
        {
            List<object> results = Json.Arr();
            string cdxml;
            if (Path.GetExtension(input).ToLowerInvariant() == ".cdx")
            {
                cdxml = Path.Combine(workDir, "document.cdxml");
                Inspector.ExportFromCdx(input, cdxml, "", 300);
            }
            else cdxml = input;

            Cdx doc = Cdx.Load(cdxml);
            int i = 0;
            foreach (XmlElement frag in doc.Fragments())
            {
                i++;
                if (ids.Count > 0 && !ids.Contains(i.ToString(CultureInfo.InvariantCulture))) continue;
                StructureInfo info = engine.AnalyzeCdxml(Inspector.WrapFragment(frag, doc.BondLength));
                Dictionary<string, object> item = Json.Obj();
                item["id"] = i;
                item["smiles"] = info.Smiles;
                item["formula"] = info.Formula;
                if (!info.Ok)
                {
                    item["ok"] = false;
                    item["error"] = info.Error;
                    results.Add(item);
                    continue;
                }

                List<double[]> atoms;
                List<int[]> bonds;
                double energy;
                string error;
                Stopwatch sw = Stopwatch.StartNew();
                bool ok = engine.Generate3D(info.Smiles, out atoms, out bonds, out energy, out error);
                item["ok"] = ok;
                if (ok)
                {
                    item["conformer"] = "mm2";
                    item["energy_kcal"] = Json.Round(energy, 2);
                    item["atoms"] = atoms.Count;
                    item["planarity_rmsd"] = Json.Round(Geo3D.PlaneRmsd(atoms), 3);
                    item["max_torsion_deg"] = Json.Round(Geo3D.MaxTorsion(atoms, bonds), 1);
                    item["max_atom_distance"] = Json.Round(Geo3D.MaxPairwise(atoms), 2);
                }
                else
                {
                    item["error"] = error;
                }
                item["ms"] = sw.ElapsedMilliseconds;
                results.Add(item);
            }

            Dictionary<string, object> result = Json.Obj();
            if (ids.Count > 0 && results.Count == 0)
            {
                result["ok"] = false;
                result["file"] = input;
                result["error"] = "no structure has id " + string.Join(",", ids.ToArray())
                    + "; this document has " + i + " structure(s), ids 1.." + i;
                return result;
            }
            result["ok"] = true;
            result["file"] = input;
            result["structures"] = results;
            return result;
        }

        private static Dictionary<string, object> RunWrite(string command, string input, Engine engine, string workDir, Dict options, WriteOptions wo)
        {
            string editDir = Path.Combine(workDir, "edit");

            // An empty batch is a no-op: do not touch (or back up) the file at all.
            if (command == "edit")
            {
                string opsFile = options.Get("ops", null);
                if (opsFile == null || !File.Exists(opsFile)) throw new InvalidOperationException("--ops <json file> is required");
                object[] ops = ParseOps(File.ReadAllText(opsFile));
                if (ops.Length == 0)
                {
                    Dictionary<string, object> empty = Json.Obj();
                    empty["ok"] = true;
                    empty["applied"] = false;
                    empty["dry_run"] = wo.DryRun;
                    empty["file"] = input;
                    empty["changes"] = Json.Arr();
                    empty["note"] = "no operations were requested; the file was not touched";
                    empty["command"] = command;
                    return empty;
                }
            }

            bool createIfMissing = command == "add" || command == "edit";
            using (WriteSession session = WriteSession.Begin(input, engine, editDir, createIfMissing))
            {
                if (command == "add")
                {
                    string smiles = options.Get("smiles", null);
                    if (smiles == null) throw new InvalidOperationException("--smiles is required");
                    double? x = null, y = null;
                    double px, py;
                    if (PointArg(options.Get("at", null), out px, out py)) { x = px; y = py; }
                    session.Add(smiles, options.Get("label", ""), x, y, options.Has("props"));
                }
                else if (command == "move")
                {
                    int id = options.GetInt("index", 0);
                    if (id <= 0) throw new InvalidOperationException("--index is required");
                    double? toX = null, toY = null;
                    double px, py;
                    if (PointArg(options.Get("at", null), out px, out py)) { toX = px; toY = py; }
                    session.Move(id, options.GetDouble("dx", 0), options.GetDouble("dy", 0), toX, toY);
                }
                else if (command == "arrow")
                {
                    double ax, ay, bx, by;
                    if (!PointArg(options.Get("from", null), out ax, out ay))
                        throw new InvalidOperationException("--from x,y is required");
                    if (!PointArg(options.Get("to", null), out bx, out by))
                        throw new InvalidOperationException("--to x,y is required");
                    session.Arrow(ax, ay, bx, by);
                }
                else if (command == "delete")
                {
                    int id = options.GetInt("index", 0);
                    string label = options.Get("label", null);
                    if (id <= 0 && label == null) throw new InvalidOperationException("--index or --label is required");
                    int nth = options.GetInt("nth", 1);
                    int matches = 0;
                    if (id <= 0)
                    {
                        StructureRecord r = session.FindByLabel(label, nth);
                        if (r == null) throw new InvalidOperationException("no structure labelled '" + label + "'");
                        matches = session.CountByLabel(label);
                        id = r.Index;
                    }
                    Dictionary<string, object> change = session.Delete(id);
                    if (matches > 0) change["label_match_count"] = matches;
                }
                else if (command == "replace")
                {
                    int id = options.GetInt("index", 0);
                    string smiles = options.Get("smiles", null);
                    if (id <= 0) throw new InvalidOperationException("--index is required");
                    if (smiles == null) throw new InvalidOperationException("--smiles is required");
                    session.Replace(id, smiles, options.Get("label", null), options.Has("props"));
                }
                else if (command == "annotate")
                {
                    string text = options.Get("text", null);
                    if (text == null) throw new InvalidOperationException("--text is required");
                    double? x = null, y = null;
                    double px, py;
                    if (PointArg(options.Get("at", null), out px, out py)) { x = px; y = py; }
                    session.Annotate(options.GetInt("index", 0), text, x, y);
                }
                else if (command == "edit")
                {
                    string opsFile = options.Get("ops", null);
                    if (opsFile == null || !File.Exists(opsFile)) throw new InvalidOperationException("--ops <json file> is required");
                    ApplyBatch(session, File.ReadAllText(opsFile));
                }

                Dictionary<string, object> result = session.Commit(wo);
                result["command"] = command;
                return result;
            }
        }

        /// <summary>Parse {"ops":[...]} (or a bare array) into the operation list.</summary>
        private static object[] ParseOps(string json)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            object parsed = ser.DeserializeObject(json);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            object[] ops = null;
            if (root != null && root.ContainsKey("ops")) ops = root["ops"] as object[];
            else ops = parsed as object[];
            if (ops == null) throw new InvalidOperationException("ops JSON must be {\"ops\":[...]} or an array");
            return ops;
        }

        private static void ApplyBatch(WriteSession session, string json)
        {
            object[] ops = ParseOps(json);
            foreach (object entry in ops)
            {
                Dictionary<string, object> op = entry as Dictionary<string, object>;
                if (op == null) throw new InvalidOperationException("each op must be an object");
                string kind = Str(op, "op");
                if (kind == null) throw new InvalidOperationException("op is missing 'op'");
                kind = kind.ToLowerInvariant();

                if (kind == "add")
                {
                    double? x = null, y = null;
                    double[] at = Pair(op, "at");
                    if (at != null) { x = at[0]; y = at[1]; }
                    session.Add(Str(op, "smiles"), Str(op, "label") ?? "", x, y, Bool(op, "props"));
                }
                else if (kind == "delete")
                {
                    int id = Int(op, "id", 0);
                    if (id <= 0)
                    {
                        string label = Str(op, "label");
                        if (label == null) throw new InvalidOperationException("delete needs id or label");
                        StructureRecord r = session.FindByLabel(label, Int(op, "nth", 1));
                        if (r == null) throw new InvalidOperationException("no structure labelled '" + label + "'");
                        id = r.Index;
                    }
                    session.Delete(id);
                }
                else if (kind == "replace")
                {
                    session.Replace(Int(op, "id", 0), Str(op, "smiles"), Str(op, "label"), Bool(op, "props"));
                }
                else if (kind == "annotate")
                {
                    double? x = null, y = null;
                    double[] at = Pair(op, "at");
                    if (at != null) { x = at[0]; y = at[1]; }
                    session.Annotate(Int(op, "id", 0), Str(op, "text"), x, y);
                }
                else if (kind == "move")
                {
                    double[] at = Pair(op, "at");
                    session.Move(Int(op, "id", 0), Dbl(op, "dx", 0), Dbl(op, "dy", 0),
                        at != null ? (double?)at[0] : null, at != null ? (double?)at[1] : null);
                }
                else if (kind == "arrow")
                {
                    double[] from = Pair(op, "from");
                    double[] to = Pair(op, "to");
                    if (from == null || to == null) throw new InvalidOperationException("arrow needs from and to, e.g. [x,y] or \"x,y\"");
                    session.Arrow(from[0], from[1], to[0], to[1]);
                }
                else
                {
                    throw new InvalidOperationException("unknown op: " + kind);
                }
            }
        }

        private static double Dbl(Dictionary<string, object> d, string key, double fallback)
        {
            string s = Str(d, key);
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            string s = Str(d, key);
            int i;
            return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : fallback;
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            bool b;
            if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b)) return b;
            return false;
        }

        /// <summary>Parse "x,y" (also "x y") into a point.</summary>
        private static bool PointArg(string s, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private static double[] Pair(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;

            string s = v as string;
            if (s != null)
            {
                double x, y;
                return PointArg(s, out x, out y) ? new double[] { x, y } : null;
            }

            object[] arr = v as object[];
            if (arr == null || arr.Length < 2) return null;
            double px, py;
            if (!double.TryParse(Convert.ToString(arr[0], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out px)) return null;
            if (!double.TryParse(Convert.ToString(arr[1], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out py)) return null;
            return new double[] { px, py };
        }

        private static Dictionary<string, object> BuildHelp()
        {
            Dictionary<string, object> h = Json.Obj();
            h["ok"] = true;
            h["usage"] = new string[]
            {
                "ChemBridge doctor",
                "ChemBridge inspect <file.cdx|.cdxml> [--out <dir>] [--no-thumbs]",
                "ChemBridge render <file> [--out <dir>] [--ids 1,2]",
                "ChemBridge threed <file> [--ids 1,2] [--out <dir>]",
                "ChemBridge find <file> [--label <l>] [--index <n>] [--formula <f>] [--smiles <s>]",
                "ChemBridge add <file> --smiles <s> [--label <l>] [--at x,y] [--props] [--dry-run]",
                "ChemBridge delete <file> --index <n> | (--label <l> [--nth <k>]) [--dry-run]",
                "ChemBridge replace <file> --index <n> --smiles <s> [--label <l>] [--props] [--dry-run]",
                "ChemBridge annotate <file> --text <t> [--index <n> | --at x,y] [--dry-run]",
                "ChemBridge move <file> --index <n> (--dx <d> --dy <d> | --at x,y) [--dry-run]",
                "ChemBridge arrow <file> --from x,y --to x,y [--dry-run]",
                "ChemBridge edit <file> --ops <ops.json> [--dry-run]"
            };
            h["notes"] = new string[]
            {
                "Every write is verified by re-reading the produced file; a mismatch leaves the original untouched.",
                "A .bak.<timestamp> copy is written next to the original unless --no-backup is given."
            };
            return h;
        }
    }
}
