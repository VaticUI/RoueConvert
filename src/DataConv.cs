using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace RoueConvert
{
    // Tabular / structured data: csv, tsv, xlsx, json, xml, yaml.
    internal static class DataConv
    {
        static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static void Convert(string src, string se, string dst, string target)
        {
            if (se == "json" || se == "xml")
            {
                object data = se == "json" ? ParseJson(File.ReadAllText(src)) : XmlToObject(XDocument.Load(src).Root);
                switch (target)
                {
                    case "json": File.WriteAllText(dst, Json(data), Utf8); return;
                    case "xml": XmlFromObject(data).Save(dst); return;
                    case "yaml": File.WriteAllText(dst, Yaml(data), Utf8); return;
                    case "csv": WriteCsv(dst, TableFromObject(data), Sep()); return;
                }
                throw new Exception("Cible non prise en charge");
            }

            List<List<string>> rows;
            if (se == "xlsx") rows = ReadXlsx(src);
            else rows = ReadCsv(File.ReadAllText(src), se == "tsv" ? '\t' : DetectSep(src));
            switch (target)
            {
                case "csv": WriteCsv(dst, rows, Sep()); return;
                case "tsv": WriteCsv(dst, rows, '\t'); return;
                case "xlsx": WriteXlsx(dst, rows); return;
                case "json": File.WriteAllText(dst, Json(ObjectsFromTable(rows)), Utf8); return;
            }
            throw new Exception("Cible non prise en charge");
        }

        // Excel in a French locale expects ';'.
        static char Sep()
        {
            string s = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            return string.IsNullOrEmpty(s) ? ',' : s[0];
        }

        // ---------- CSV ----------

        static char DetectSep(string path)
        {
            string first = File.ReadLines(path).FirstOrDefault() ?? "";
            var cands = new[] { ';', ',', '\t', '|' };
            return cands.OrderByDescending(c => first.Count(x => x == c)).First();
        }

        public static List<List<string>> ReadCsv(string text, char sep)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool q = false;
            text = text.TrimStart('﻿');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                    else cell.Append(c);
                }
                else if (c == '"' && cell.Length == 0) q = true;
                else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        static void WriteCsv(string dst, List<List<string>> rows, char sep)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(string.Join(sep.ToString(), r.Select(c =>
                    c.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
                sb.Append("\r\n");
            }
            File.WriteAllText(dst, sb.ToString(), Utf8Bom);
        }

        // ---------- XLSX ----------

        static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        static List<List<string>> ReadXlsx(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                Func<string, XDocument> load = name =>
                {
                    var e = zip.GetEntry(name);
                    if (e == null) return null;
                    using (var s = e.Open()) return XDocument.Load(s);
                };
                var shared = new List<string>();
                var sst = load("xl/sharedStrings.xml");
                if (sst != null)
                    foreach (var si in sst.Root.Elements(S + "si"))
                        shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));

                string sheetPath = "xl/worksheets/sheet1.xml";
                var wb = load("xl/workbook.xml");
                var rels = load("xl/_rels/workbook.xml.rels");
                if (wb != null && rels != null)
                {
                    var first = wb.Root.Descendants(S + "sheet").FirstOrDefault();
                    if (first != null)
                    {
                        string rid = (string)first.Attribute(Rel + "id");
                        var rel = rels.Root.Elements(Pkg + "Relationship").FirstOrDefault(r => (string)r.Attribute("Id") == rid);
                        if (rel != null)
                        {
                            string t = ((string)rel.Attribute("Target")).TrimStart('/');
                            sheetPath = t.StartsWith("xl/") ? t : "xl/" + t;
                        }
                    }
                }
                var sheet = load(sheetPath);
                if (sheet == null) throw new Exception("Feuille Excel introuvable");
                var rows = new List<List<string>>();
                foreach (var r in sheet.Root.Descendants(S + "row"))
                {
                    int ri = (int?)r.Attribute("r") ?? rows.Count + 1;
                    while (rows.Count < ri - 1) rows.Add(new List<string>());
                    var row = new List<string>();
                    foreach (var c in r.Elements(S + "c"))
                    {
                        string refr = (string)c.Attribute("r");
                        int col = refr != null ? ColIndex(refr) : row.Count;
                        while (row.Count < col) row.Add("");
                        string type = (string)c.Attribute("t");
                        string v = (string)c.Element(S + "v");
                        string val;
                        if (type == "s" && v != null) val = shared[int.Parse(v)];
                        else if (type == "inlineStr") val = string.Concat(c.Descendants(S + "t").Select(t => t.Value));
                        else if (type == "b") val = v == "1" ? "VRAI" : "FAUX";
                        else val = v ?? "";
                        row.Add(val);
                    }
                    rows.Add(row);
                }
                int width = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
                foreach (var row in rows) while (row.Count < width) row.Add("");
                return rows;
            }
        }

        static int ColIndex(string cellRef)
        {
            int n = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return n - 1;
        }

        static string ColName(int i)
        {
            string s = "";
            for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
            return s;
        }

        static void WriteXlsx(string dst, List<List<string>> rows)
        {
            var sd = new StringBuilder();
            for (int r = 0; r < rows.Count; r++)
            {
                sd.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < rows[r].Count; c++)
                {
                    string v = rows[r][c];
                    if (v.Length == 0) continue;
                    string refr = ColName(c) + (r + 1);
                    double d;
                    string norm = v.Trim().Replace(" ", "").Replace(" ", "");
                    bool num = r > 0 && norm.Length < 16 && !(norm.Length > 1 && norm[0] == '0' && char.IsDigit(norm[1]))
                               && (double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                                   || double.TryParse(norm.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d));
                    if (num && double.TryParse(norm.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        sd.Append("<c r=\"").Append(refr).Append("\"><v>").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                    else
                        sd.Append("<c r=\"").Append(refr).Append("\"").Append(r == 0 ? " s=\"1\"" : "").Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(System.Security.SecurityElement.Escape(Clean(v))).Append("</t></is></c>");
                }
                sd.Append("</row>");
            }
            int cols = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
            var colsXml = new StringBuilder();
            if (cols > 0)
            {
                colsXml.Append("<cols>");
                for (int c = 0; c < cols; c++)
                {
                    int len = rows.Take(200).Select(r => c < r.Count ? r[c].Length : 0).DefaultIfEmpty(0).Max();
                    colsXml.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"")
                           .Append(Math.Min(60, Math.Max(8, len + 2))).Append("\" customWidth=\"1\"/>");
                }
                colsXml.Append("</cols>");
            }
            string sheet = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                (rows.Count > 1 ? "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" : "") +
                colsXml + "<sheetData>" + sd + "</sheetData></worksheet>";
            string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "</styleSheet>";
            string workbook = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Feuille1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
            string wbRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
            string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
            string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
            Zip.Write(dst, new Dictionary<string, string>
            {
                {"[Content_Types].xml", types}, {"_rels/.rels", rels}, {"xl/workbook.xml", workbook},
                {"xl/_rels/workbook.xml.rels", wbRels}, {"xl/styles.xml", styles}, {"xl/worksheets/sheet1.xml", sheet}
            });
        }

        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
            return sb.ToString();
        }

        // ---------- JSON / objects ----------

        static object ParseJson(string text)
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 1000;
            return js.DeserializeObject(text.TrimStart('﻿'));
        }

        static List<object> ObjectsFromTable(List<List<string>> rows)
        {
            var list = new List<object>();
            if (rows.Count == 0) return list;
            var head = rows[0].Select((h, i) => h.Trim().Length > 0 ? h.Trim() : "col" + (i + 1)).ToList();
            foreach (var r in rows.Skip(1))
            {
                if (r.All(c => c.Length == 0)) continue;
                var o = new Dictionary<string, object>();
                for (int i = 0; i < head.Count; i++)
                {
                    string v = i < r.Count ? r[i] : "";
                    o[head[i]] = Typed(v);
                }
                list.Add(o);
            }
            return list;
        }

        static object Typed(string v)
        {
            string t = v.Trim();
            if (t.Length == 0) return v;
            if (t == "true" || t == "false") return t == "true";
            double d;
            if (t.Length < 16 && !(t.Length > 1 && t[0] == '0' && char.IsDigit(t[1]))
                && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return v;
        }

        static List<List<string>> TableFromObject(object data)
        {
            IEnumerable<object> items;
            var dict = data as IDictionary<string, object>;
            if (data is IList && !(data is string)) items = ((IList)data).Cast<object>();
            else if (dict != null)
            {
                // { "users": [ ... ] } -> use the first array of objects found.
                var arr = dict.Values.FirstOrDefault(v => v is IList && ((IList)v).Count > 0 && ((IList)v)[0] is IDictionary<string, object>);
                items = arr != null ? ((IList)arr).Cast<object>() : new object[] { data };
            }
            else items = new[] { data };

            var flat = items.Select(it =>
            {
                var d = new Dictionary<string, string>();
                Flatten(it, "", d);
                return d;
            }).ToList();
            var cols = new List<string>();
            foreach (var d in flat) foreach (var k in d.Keys) if (!cols.Contains(k)) cols.Add(k);
            var rows = new List<List<string>> { cols };
            foreach (var d in flat) rows.Add(cols.Select(c => { string v; return d.TryGetValue(c, out v) ? v : ""; }).ToList());
            return rows;
        }

        static void Flatten(object o, string prefix, Dictionary<string, string> into)
        {
            var dict = o as IDictionary<string, object>;
            if (dict != null)
            {
                foreach (var kv in dict) Flatten(kv.Value, prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, into);
                return;
            }
            var list = o as IList;
            if (list != null && !(o is string))
            {
                if (list.Cast<object>().All(x => !(x is IDictionary<string, object>) && !(x is IList)))
                    into[prefix.Length == 0 ? "valeur" : prefix] = string.Join(", ", list.Cast<object>().Select(Scalar));
                else
                    for (int i = 0; i < list.Count; i++) Flatten(list[i], prefix + "[" + i + "]", into);
                return;
            }
            into[prefix.Length == 0 ? "valeur" : prefix] = Scalar(o);
        }

        static string Scalar(object o)
        {
            if (o == null) return "";
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is IFormattable) return ((IFormattable)o).ToString(null, CultureInfo.InvariantCulture);
            return o.ToString();
        }

        static string Json(object o)
        {
            var sb = new StringBuilder();
            WriteJson(o, sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void WriteJson(object o, StringBuilder sb, int ind)
        {
            string pad = new string(' ', (ind + 1) * 2), end = new string(' ', ind * 2);
            var dict = o as IDictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in dict)
                {
                    sb.Append(pad).Append(Str(kv.Key)).Append(": ");
                    WriteJson(kv.Value, sb, ind + 1);
                    sb.Append(++i < dict.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append('}');
                return;
            }
            var list = o as IList;
            if (list != null && !(o is string))
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(pad);
                    WriteJson(list[i], sb, ind + 1);
                    sb.Append(i + 1 < list.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append(']');
                return;
            }
            if (o == null) sb.Append("null");
            else if (o is bool) sb.Append((bool)o ? "true" : "false");
            else if (o is string) sb.Append(Str((string)o));
            else if (o is IFormattable) sb.Append(((IFormattable)o).ToString(o is double ? "R" : null, CultureInfo.InvariantCulture));
            else sb.Append(Str(o.ToString()));
        }

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ---------- XML ----------

        static string XName(string k)
        {
            var sb = new StringBuilder();
            foreach (char c in k) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            string n = sb.ToString();
            if (n.Length == 0 || !(char.IsLetter(n[0]) || n[0] == '_')) n = "_" + n;
            return n;
        }

        static XDocument XmlFromObject(object data)
        {
            var root = new XElement("root");
            FillXml(root, data);
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        static void FillXml(XElement el, object o)
        {
            var dict = o as IDictionary<string, object>;
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    var list = kv.Value as IList;
                    if (list != null && !(kv.Value is string))
                        foreach (var it in list) { var c = new XElement(XName(kv.Key)); FillXml(c, it); el.Add(c); }
                    else { var c = new XElement(XName(kv.Key)); FillXml(c, kv.Value); el.Add(c); }
                }
                return;
            }
            var l = o as IList;
            if (l != null && !(o is string))
            {
                foreach (var it in l) { var c = new XElement("item"); FillXml(c, it); el.Add(c); }
                return;
            }
            if (o != null) el.Value = Scalar(o);
        }

        static object XmlToObject(XElement el)
        {
            var d = new Dictionary<string, object>();
            foreach (var a in el.Attributes())
                if (!a.IsNamespaceDeclaration) d["@" + a.Name.LocalName] = a.Value;
            var kids = el.Elements().ToList();
            if (kids.Count == 0)
            {
                if (d.Count == 0) return Typed(el.Value);
                if (el.Value.Trim().Length > 0) d["#text"] = el.Value.Trim();
                return d;
            }
            foreach (var g in kids.GroupBy(k => k.Name.LocalName))
            {
                var vals = g.Select(XmlToObject).ToList();
                d[g.Key] = vals.Count == 1 ? vals[0] : (object)vals.Cast<object>().ToArray();
            }
            var top = new Dictionary<string, object>();
            if (el.Parent == null) { top[el.Name.LocalName] = d; return top; }
            return d;
        }

        // ---------- YAML ----------

        static string Yaml(object o)
        {
            var sb = new StringBuilder();
            WriteYaml(o, sb, 0, false);
            return sb.ToString();
        }

        static void WriteYaml(object o, StringBuilder sb, int ind, bool inline)
        {
            string pad = new string(' ', ind * 2);
            var dict = o as IDictionary<string, object>;
            var list = o as IList;
            if (dict != null && dict.Count > 0)
            {
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!(inline && first)) sb.Append(pad);
                    first = false;
                    sb.Append(YKey(kv.Key)).Append(':');
                    WriteYamlValue(kv.Value, sb, ind);
                }
                return;
            }
            if (list != null && !(o is string) && list.Count > 0)
            {
                bool first = true;
                foreach (var it in list)
                {
                    if (!(inline && first)) sb.Append(pad);
                    first = false;
                    sb.Append("- ");
                    var id = it as IDictionary<string, object>;
                    var il = it as IList;
                    if ((id != null && id.Count > 0) || (il != null && !(it is string) && il.Count > 0)) WriteYaml(it, sb, ind + 1, true);
                    else sb.Append(YScalar(it)).Append('\n');
                }
                return;
            }
            sb.Append(pad).Append(YScalar(o)).Append('\n');
        }

        static void WriteYamlValue(object v, StringBuilder sb, int ind)
        {
            var vd = v as IDictionary<string, object>;
            var vl = v as IList;
            if ((vd != null && vd.Count > 0) || (vl != null && !(v is string) && vl.Count > 0))
            {
                sb.Append('\n');
                WriteYaml(v, sb, ind + 1, false);
            }
            else sb.Append(' ').Append(YScalar(v)).Append('\n');
        }

        static string YKey(string k)
        {
            return k.Length > 0 && k.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') ? k : YQuote(k);
        }

        static string YScalar(object o)
        {
            if (o == null) return "null";
            if (o is IDictionary<string, object>) return "{}";
            if (o is IList && !(o is string)) return "[]";
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is IFormattable) return ((IFormattable)o).ToString(null, CultureInfo.InvariantCulture);
            string s = o.ToString();
            bool plain = s.Length > 0 && s.Trim() == s && s.IndexOfAny(":#{}[],&*!|>'\"%@`\n".ToCharArray()) < 0
                && !"-?".Contains(s[0]) && !new[] { "true", "false", "null", "yes", "no", "on", "off", "~" }.Contains(s.ToLowerInvariant())
                && !(Typed(s) is double);
            return plain ? s : YQuote(s);
        }

        static string YQuote(string s) { return Str(s); }
    }
}
