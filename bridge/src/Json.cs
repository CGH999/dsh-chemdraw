using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChemBridge
{
    /// <summary>Minimal deterministic JSON writer (no dependencies, ordered output).</summary>
    public static class Json
    {
        public static string Stringify(object value)
        {
            StringBuilder sb = new StringBuilder(8192);
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            string s = value as string;
            if (s != null) { WriteString(sb, s); return; }

            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is int || value is long || value is short || value is byte || value is sbyte || value is uint || value is ulong || value is ushort)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("0.#####", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float)
            {
                double f = (float)value;
                if (double.IsNaN(f) || double.IsInfinity(f)) sb.Append("null");
                else sb.Append(f.ToString("0.#####", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal)
            {
                sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            IDictionary<string, object> map = value as IDictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Build an ordered JSON object.</summary>
        public static Dictionary<string, object> Obj()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            Dictionary<string, object> d = Obj();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static List<object> Arr()
        {
            return new List<object>();
        }

        public static double Round(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return Math.Round(v, digits, MidpointRounding.AwayFromZero);
        }
    }
}
