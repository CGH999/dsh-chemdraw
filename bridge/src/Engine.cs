using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CambridgeSoft.ChemScript;

namespace ChemBridge
{
    public sealed class StructureInfo
    {
        public bool Ok;
        public string Error = "";
        public string Smiles = "";
        public string Inchi = "";
        public string Formula = "";
        public string Name = "";
        public double Mw;
        public double ExactMass;
        public int Charge;
        public int Atoms;
        public int Bonds;
        public string SourceMime = "";

        /// <summary>InChI with stereo/isotope layers removed, for tolerant comparison.</summary>
        public string InchiSkeleton
        {
            get
            {
                if (string.IsNullOrEmpty(Inchi)) return "";
                int slash = Inchi.IndexOf('/');
                string body = slash < 0 ? Inchi : Inchi.Substring(0, slash);
                return body;
            }
        }

        /// <summary>
        /// Identity used to verify an edit. Not every drawing yields an InChI (abbreviation groups,
        /// polymers, metal complexes give an empty one), so fall back to a structural fingerprint
        /// instead of comparing empty strings.
        /// </summary>
        public string Key
        {
            get
            {
                if (!string.IsNullOrEmpty(Inchi)) return Inchi;
                return "no-inchi:" + Formula + "|" + Atoms + "|" + Charge;
            }
        }
    }

    /// <summary>
    /// ChemScript chemical engine: single-structure analysis, CDXML generation, 3D embedding.
    /// Every call takes one structure only (the engine silently fails on multi-structure input).
    /// </summary>
    public sealed class Engine
    {
        private readonly string _workDir;

        public Engine(string workDir)
        {
            _workDir = workDir;
            if (!Directory.Exists(_workDir)) Directory.CreateDirectory(_workDir);
        }

        public static StructureInfo FromStructureData(StructureData sd, string mime)
        {
            StructureInfo si = new StructureInfo();
            si.SourceMime = mime;
            try
            {
                si.Ok = !sd.IsEmpty && sd.Atoms.Count > 0;
                if (!si.Ok) { si.Error = "empty structure"; return si; }
                si.Smiles = sd.Smiles;
                si.Inchi = sd.Inchi;
                si.Formula = sd.Formula();
                si.Mw = sd.Weight;
                si.ExactMass = sd.ExactMass;
                si.Charge = sd.TotalCharge;
                si.Atoms = sd.Atoms.Count;
                si.Bonds = sd.Bonds.Count;
                try { si.Name = sd.ChemicalName(); }
                catch { si.Name = ""; }
            }
            catch (Exception ex)
            {
                si.Ok = false;
                si.Error = ex.Message;
            }
            return si;
        }

        /// <summary>Analyze one CDXML fragment, already wrapped in a standalone &lt;CDXML&gt; document.</summary>
        public StructureInfo AnalyzeCdxml(string cdxml)
        {
            StructureData sd = new StructureData();
            try
            {
                sd.ReadData(cdxml, "text/xml");
                return FromStructureData(sd, "text/xml");
            }
            catch (Exception ex)
            {
                StructureInfo si = new StructureInfo();
                si.Ok = false;
                si.Error = ex.Message;
                return si;
            }
        }

        public StructureInfo AnalyzeSmiles(string smiles)
        {
            StructureData sd = new StructureData();
            try
            {
                sd.ReadData(smiles, "chemical/x-smiles");
                return FromStructureData(sd, "chemical/x-smiles");
            }
            catch (Exception ex)
            {
                StructureInfo si = new StructureInfo();
                si.Ok = false;
                si.Error = ex.Message;
                return si;
            }
        }

        public StructureInfo AnalyzeMolfile(string molblock)
        {
            StructureData sd = new StructureData();
            try
            {
                sd.ReadData(molblock, "chemical/x-mdl-molfile");
                return FromStructureData(sd, "chemical/x-mdl-molfile");
            }
            catch (Exception ex)
            {
                StructureInfo si = new StructureInfo();
                si.Ok = false;
                si.Error = ex.Message;
                return si;
            }
        }

        /// <summary>Build a standalone CDXML file for a structure given as SMILES.</summary>
        public bool GenerateCdxml(string smiles, string outPath, out StructureInfo info)
        {
            info = null;
            StructureData sd = new StructureData();
            try
            {
                sd.ReadData(smiles, "chemical/x-smiles");
                if (sd.IsEmpty) return false;
                // Aromatic bonds would otherwise render as dashed "delocalized" bonds in the target drawing.
                try { sd.ConvertToFixedBondRep(); } catch { }
                if (File.Exists(outPath)) File.Delete(outPath);
                sd.WriteFile(outPath, "cdxml");
                info = FromStructureData(sd, "chemical/x-smiles");
                return File.Exists(outPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Generate an MM2-optimized 3D conformer and return heavy-atom coordinates plus bonds.</summary>
        public bool Generate3D(string smiles, out List<double[]> atoms, out List<int[]> bonds, out double energy, out string error)
        {
            atoms = new List<double[]>();
            bonds = new List<int[]>();
            energy = 0;
            error = "";
            string mol = Path.Combine(_workDir, "conformer" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mol");
            StructureData sd = new StructureData();
            try
            {
                sd.ReadData(smiles, "chemical/x-smiles");
                if (sd.IsEmpty) { error = "empty structure"; return false; }
                if (!sd.ConvertTo3DStructure(true)) { error = "3D conversion failed"; return false; }
                try { sd.Mm2OptimizeGeometry(0.01, 500); }
                catch { }
                try { energy = sd.Mm2Energy(); }
                catch { energy = 0; }
                sd.WriteFile(mol, "chemical/x-mdl-molfile-v3000");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                // sd has no deterministic finalizer need in a short-lived process
            }

            if (!File.Exists(mol)) { error = "molfile not written"; return false; }
            try
            {
                ParseV3000(File.ReadAllLines(mol), atoms, bonds);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(mol); } catch { }
            }
            return atoms.Count > 0;
        }

        /// <summary>Extract atom coordinates (x,y,z) and bonds from a V3000 molfile.</summary>
        public static void ParseV3000(string[] lines, List<double[]> atoms, List<int[]> bonds)
        {
            bool inAtoms = false, inBonds = false;
            const string tag = "M  V30 ";
            foreach (string raw in lines)
            {
                string line = raw;
                if (!line.StartsWith(tag, StringComparison.Ordinal)) continue;
                string body = line.Substring(tag.Length).Trim();
                if (body.StartsWith("BEGIN ATOM", StringComparison.Ordinal)) { inAtoms = true; continue; }
                if (body.StartsWith("END ATOM", StringComparison.Ordinal)) { inAtoms = false; continue; }
                if (body.StartsWith("BEGIN BOND", StringComparison.Ordinal)) { inBonds = true; continue; }
                if (body.StartsWith("END BOND", StringComparison.Ordinal)) { inBonds = false; continue; }

                string[] f = body.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (inAtoms && f.Length >= 5)
                {
                    double x, y, z;
                    if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                    if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                    if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                    atoms.Add(new double[] { x, y, z });
                }
                else if (inBonds && f.Length >= 4)
                {
                    int a, b;
                    if (int.TryParse(f[2], out a) && int.TryParse(f[3], out b))
                        bonds.Add(new int[] { a - 1, b - 1 });
                }
            }
        }
    }
}
