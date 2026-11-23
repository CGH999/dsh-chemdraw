using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChemBridge
{
    /// <summary>Late-bound ChemDraw automation wrapper.</summary>
    public sealed class ChemDraw : IDisposable
    {
        public const string ProgId = "ChemDraw_x64.Application";

        private object _app;
        private object _doc;
        private bool _owned;      // true when this process launched the instance
        private bool _hidden;

        public bool Owned { get { return _owned; } }

        public static ChemDraw Acquire()
        {
            ChemDraw c = new ChemDraw();

            object existing = null;
            try { existing = Marshal.GetActiveObject(ProgId); }
            catch { existing = null; }

            if (existing != null)
            {
                // A user session is running: reuse it, never hide it, never quit it.
                c._app = existing;
                c._owned = false;
            }
            else
            {
                Type t = Type.GetTypeFromProgID(ProgId);
                if (t == null) throw new InvalidOperationException("ChemDraw COM server is not registered: " + ProgId);
                c._app = Activator.CreateInstance(t);
                c._owned = true;
                try { SetProperty(c._app, "Visible", false); c._hidden = true; }
                catch { }
            }
            return c;
        }

        public object Application { get { return _app; } }
        public object Document { get { return _doc; } }

        public object Documents
        {
            get { return GetProperty(_app, "Documents"); }
        }

        public int DocumentCount
        {
            get
            {
                try
                {
                    object docs = Documents;
                    if (docs == null) return -1;
                    return Convert.ToInt32(GetProperty(docs, "Count"));
                }
                catch { return -1; }
            }
        }

        public void Open(string path)
        {
            object docs = Documents;
            if (docs == null) throw new InvalidOperationException("ChemDraw is not responding (no Documents collection)");
            try
            {
                _doc = Invoke(docs, "Open", path, null, null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ChemDraw could not open " + path + ": " + Innermost(ex), ex);
            }
            if (_doc == null)
                throw new InvalidOperationException(
                    "ChemDraw returned no document for " + path + " (the file may not be a valid ChemDraw document, or ChemDraw is still starting)");
        }

        /// <summary>Create a blank document (used when a new file has to be written).</summary>
        public void AddDocument()
        {
            object docs = Documents;
            if (docs == null) throw new InvalidOperationException("ChemDraw is not responding (no Documents collection)");
            try
            {
                _doc = Invoke(docs, "Add");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ChemDraw could not create a document: " + Innermost(ex), ex);
            }
            if (_doc == null) throw new InvalidOperationException("ChemDraw returned no document");
        }

        /// <summary>Deepest message, so callers see the COM failure instead of a wrapper.</summary>
        public static string Innermost(Exception ex)
        {
            string message = ex.Message;
            for (Exception e = ex.InnerException; e != null; e = e.InnerException)
            {
                if (!string.IsNullOrEmpty(e.Message)) message = e.Message;
            }
            return message;
        }

        public void SaveAs(string path, string mime)
        {
            if (_doc == null) throw new InvalidOperationException("No document is open");
            Invoke(_doc, "SaveAs", path, mime);
        }

        public void SaveAs(string path, string mime, object third)
        {
            if (_doc == null) throw new InvalidOperationException("No document is open");
            Invoke(_doc, "SaveAs", path, mime, third);
        }

        public int NumPagesWide { get { return GetInt("NumPagesWide"); } }
        public int NumPagesHigh { get { return GetInt("NumPagesHigh"); } }
        public int ObjectCount { get { return GetInt("Objects", "Count"); } }
        public int CaptionCount { get { return GetInt("Captions", "Count"); } }
        public int ArrowCount { get { return GetInt("Arrows", "Count"); } }
        public int ReactionCount { get { return GetInt("ReactionSchemes", "Count"); } }

        private int GetInt(string member)
        {
            try { return Convert.ToInt32(GetProperty(_doc, member)); }
            catch { return -1; }
        }

        private int GetInt(string member, string sub)
        {
            try { return Convert.ToInt32(GetProperty(GetProperty(_doc, member), sub)); }
            catch { return -1; }
        }

        public void CloseDocument()
        {
            if (_doc == null) return;
            try { Invoke(_doc, "Close", false, null); }
            catch { }
            finally { _doc = null; }
        }

        public void Dispose()
        {
            CloseDocument();
            if (_owned)
            {
                // Never leave a hidden instance behind; a crashed earlier run must not accumulate processes.
                try { Invoke(_app, "Quit"); }
                catch { }
            }
            try
            {
                if (Marshal.IsComObject(_app)) Marshal.ReleaseComObject(_app);
            }
            catch { }
            _app = null;
        }

        // ---- late binding helpers ----

        public static object GetProperty(object target, string name)
        {
            if (target == null) return null;
            return target.GetType().InvokeMember(name,
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                null, target, new object[0]);
        }

        public static void SetProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name,
                BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
                null, target, new object[] { value });
        }

        public static object Invoke(object target, string name, params object[] args)
        {
            if (target == null) return null;
            if (args == null) args = new object[0];
            return target.GetType().InvokeMember(name,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null, target, args);
        }
    }

    /// <summary>Locates the ChemDraw installation and native ChemScript engine.</summary>
    public sealed class ChemPaths
    {
        public string Home;
        public string ChemScriptLib;
        public string ChemScriptNet;
        public string ChemDrawExeDir;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AddDllDirectory(string lpPathName);

        public static ChemPaths Resolve()
        {
            ChemPaths p = new ChemPaths();
            string home = Environment.GetEnvironmentVariable("CHEMDRAW_HOME");
            if (string.IsNullOrEmpty(home))
                throw new InvalidOperationException("ChemDraw installation not found: set CHEMDRAW_HOME to the folder that contains ChemScript and ChemDraw");
            p.Home = home;
            p.ChemScriptLib = Path.Combine(home, "ChemScript", "Lib");
            p.ChemScriptNet = Path.Combine(p.ChemScriptLib, "Net");
            p.ChemDrawExeDir = Path.Combine(home, "ChemDraw");

            if (!Directory.Exists(p.ChemScriptLib))
                throw new InvalidOperationException("ChemScript not found at " + p.ChemScriptLib + " (set CHEMDRAW_HOME)");
            if (!File.Exists(Path.Combine(p.ChemScriptNet, "CambridgeSoft.ChemScript.dll")))
                throw new InvalidOperationException("ChemScript .NET assembly not found in " + p.ChemScriptNet);

            // Native ChemScript.dll is resolved through the DLL search path.
            try { SetDllDirectory(p.ChemScriptLib); }
            catch { }
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path == null || path.IndexOf(p.ChemScriptLib, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Environment.SetEnvironmentVariable("PATH",
                    p.ChemScriptLib + ";" + p.ChemDrawExeDir + ";" + (path == null ? "" : path));
            }
            return p;
        }

        public void HookAssemblyResolver()
        {
            string dir = ChemScriptNet;
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name;
                    string candidate = Path.Combine(dir, name + ".dll");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                catch { }
                return null;
            };
        }
    }
}
