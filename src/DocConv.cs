using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Xml.Linq;

namespace RoueConvert
{
    // Text documents. Every source is read into a small block model (headings, paragraphs, lists, code, tables)
    // and written back out; PDF goes through HTML + Edge.
    internal static class DocConv
    {
        internal class Run { public string Text; public bool B, I, Code; public string Href; }
        internal class Block
        {
            public string Kind = "p"; // p h1..h6 li oli pre quote hr table
            public List<Run> Runs = new List<Run>();
            public List<List<string>> Rows;
            public string PlainText { get { return string.Concat(Runs.Select(r => r.Text)); } }
        }

        public static void Convert(string src, string se, string dst, string target)
        {
            if (se == "html" && target == "pdf") { Edge.PrintToPdf(src, dst); return; }

            List<Block> doc;
            string title = Path.GetFileNameWithoutExtension(src);
            switch (se)
            {
                case "txt": doc = FromText(ReadText(src)); break;
                case "md": doc = FromMarkdown(ReadText(src)); break;
                case "html": doc = FromHtml(ReadText(src)); break;
                case "docx": doc = FromDocx(src); break;
                case "rtf": doc = FromRtf(src); break;
                default: throw new Exception("Source non prise en charge");
            }

            switch (target)
            {
                case "txt": File.WriteAllText(dst, ToText(doc), new UTF8Encoding(true)); break;
                case "md": File.WriteAllText(dst, ToMarkdown(doc), new UTF8Encoding(false)); break;
                case "html": File.WriteAllText(dst, ToHtml(doc, title, se == "txt"), new UTF8Encoding(false)); break;
                case "rtf": File.WriteAllText(dst, ToRtf(doc), Encoding.ASCII); break;
                case "docx": WriteDocx(doc, dst); break;
                case "pdf":
                    {
                        string tmp = Tools.TempDir();
                        try
                        {
                            string html = Path.Combine(tmp, "doc.html");
                            File.WriteAllText(html, ToHtml(doc, title, se == "txt"), new UTF8Encoding(false));
                            Edge.PrintToPdf(html, dst);
                        }
                        finally { Tools.TryDelete(tmp); }
                        break;
                    }
                default: throw new Exception("Cible non prise en charge");
            }
        }

        static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // UTF-8 (with or without BOM), else fall back to the Windows ANSI code page.
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        static Run R(string t) { var r = new Run(); r.Text = t; return r; }
        static Block Bk(string kind) { var b = new Block(); b.Kind = kind; return b; }

        // ---------- readers ----------

        static List<Block> FromText(string text)
        {
            var doc = new List<Block>();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var b = Bk("p");
                b.Runs.Add(R(line));
                doc.Add(b);
            }
            return doc;
        }

        static readonly Regex RxHead = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
        static readonly Regex RxUl = new Regex(@"^\s*[-*+]\s+(.*)$");
        static readonly Regex RxOl = new Regex(@"^\s*\d+[.)]\s+(.*)$");
        static readonly Regex RxHr = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");
        static readonly Regex RxTableSep = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

        static List<Block> FromMarkdown(string md)
        {
            var doc = new List<Block>();
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var para = new StringBuilder();
            Action flush = () =>
            {
                if (para.Length == 0) return;
                var b = Bk("p"); b.Runs = Inline(para.ToString().Trim()); doc.Add(b); para.Clear();
            };
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.TrimStart().StartsWith("```"))
                {
                    flush();
                    var code = new StringBuilder();
                    for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Append(lines[i]).Append('\n');
                    var b = Bk("pre"); b.Runs.Add(R(code.ToString().TrimEnd('\n'))); doc.Add(b);
                    continue;
                }
                if (l.Trim().Length == 0) { flush(); continue; }
                Match m;
                if ((m = RxHead.Match(l)).Success) { flush(); var b = Bk("h" + m.Groups[1].Length); b.Runs = Inline(m.Groups[2].Value); doc.Add(b); continue; }
                if (RxHr.IsMatch(l)) { flush(); doc.Add(Bk("hr")); continue; }
                if (l.TrimStart().StartsWith("|") && i + 1 < lines.Length && RxTableSep.IsMatch(lines[i + 1]))
                {
                    flush();
                    var t = Bk("table"); t.Rows = new List<List<string>>();
                    t.Rows.Add(Cells(l));
                    for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith("|"); i++) t.Rows.Add(Cells(lines[i]));
                    i--;
                    doc.Add(t);
                    continue;
                }
                if ((m = RxUl.Match(l)).Success && !RxHr.IsMatch(l)) { flush(); var b = Bk("li"); b.Runs = Inline(m.Groups[1].Value); doc.Add(b); continue; }
                if ((m = RxOl.Match(l)).Success) { flush(); var b = Bk("oli"); b.Runs = Inline(m.Groups[1].Value); doc.Add(b); continue; }
                if (l.StartsWith(">")) { flush(); var b = Bk("quote"); b.Runs = Inline(l.TrimStart('>').Trim()); doc.Add(b); continue; }
                para.Append(l.Trim()).Append(' ');
            }
            flush();
            return doc;
        }

        static List<string> Cells(string row)
        {
            row = row.Trim();
            if (row.StartsWith("|")) row = row.Substring(1);
            if (row.EndsWith("|")) row = row.Substring(0, row.Length - 1);
            return row.Split('|').Select(c => c.Trim()).ToList();
        }

        static readonly Regex RxLink = new Regex(@"^!?\[([^\]]*)\]\(([^)\s]*)[^)]*\)");

        static List<Run> Inline(string s)
        {
            var runs = new List<Run>();
            var sb = new StringBuilder();
            bool b = false, it = false;
            Action flush = () =>
            {
                if (sb.Length == 0) return;
                var r = R(sb.ToString()); r.B = b; r.I = it; runs.Add(r); sb.Clear();
            };
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i) { flush(); var r = R(s.Substring(i + 1, end - i - 1)); r.Code = true; runs.Add(r); i = end; continue; }
                }
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c) { flush(); b = !b; i++; continue; }
                if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(s[i - 1]) || (i + 1 < s.Length && !char.IsLetterOrDigit(s[i + 1])))))
                { flush(); it = !it; continue; }
                if (c == '[' || (c == '!' && i + 1 < s.Length && s[i + 1] == '['))
                {
                    var m = RxLink.Match(s.Substring(i));
                    if (m.Success)
                    {
                        flush();
                        var r = R(m.Groups[1].Value); r.B = b; r.I = it; r.Href = m.Groups[2].Value; runs.Add(r);
                        i += m.Length - 1;
                        continue;
                    }
                }
                sb.Append(c);
            }
            flush();
            return runs;
        }

        static readonly Regex RxTag = new Regex(@"<!--.*?-->|<(/?)([a-zA-Z0-9]+)([^>]*)>|([^<]+)", RegexOptions.Singleline);
        static readonly Regex RxHref = new Regex("href\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        static readonly HashSet<string> BlockTags = new HashSet<string> { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "pre", "blockquote", "tr", "table", "ul", "ol", "section", "article", "header", "footer", "br", "hr" };

        static List<Block> FromHtml(string html)
        {
            var doc = new List<Block>();
            var bm = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (bm.Success) html = bm.Groups[1].Value;
            Block cur = null;
            int bold = 0, ital = 0, code = 0, skip = 0, pre = 0;
            string href = null;
            bool ordered = false;
            List<List<string>> table = null; List<string> row = null; StringBuilder cell = null;

            Func<string, Block> start = kind => { cur = Bk(kind); doc.Add(cur); return cur; };
            foreach (Match m in RxTag.Matches(html))
            {
                if (m.Value.StartsWith("<!--")) continue;
                if (m.Groups[4].Success)
                {
                    if (skip > 0) continue;
                    string t = WebUtility.HtmlDecode(m.Groups[4].Value);
                    if (pre == 0) t = Regex.Replace(t, @"\s+", " ");
                    if (cell != null) { cell.Append(t); continue; }
                    if (cur == null || cur.Kind == "hr") { if (t.Trim().Length == 0) continue; start("p"); t = t.TrimStart(); }
                    var r = R(t); r.B = bold > 0; r.I = ital > 0; r.Code = code > 0; r.Href = href;
                    cur.Runs.Add(r);
                    continue;
                }
                bool close = m.Groups[1].Value == "/";
                string tag = m.Groups[2].Value.ToLowerInvariant();
                switch (tag)
                {
                    case "script": case "style": case "head": case "title": case "noscript": skip += close ? -1 : 1; if (skip < 0) skip = 0; continue;
                    case "b": case "strong": bold += close ? -1 : 1; continue;
                    case "i": case "em": ital += close ? -1 : 1; continue;
                    case "code": if (pre == 0) code += close ? -1 : 1; continue;
                    case "a": href = close ? null : (RxHref.Match(m.Groups[3].Value).Success ? RxHref.Match(m.Groups[3].Value).Groups[1].Value : null); continue;
                    case "ul": ordered = false; break;
                    case "ol": ordered = !close; break;
                    case "table": if (!close) table = new List<List<string>>(); else if (table != null) { var tb = Bk("table"); tb.Rows = table; doc.Add(tb); table = null; cur = null; } continue;
                    case "tr": if (table != null) { if (!close) { row = new List<string>(); table.Add(row); } else row = null; } continue;
                    case "td": case "th":
                        if (row != null) { if (!close) cell = new StringBuilder(); else if (cell != null) { row.Add(cell.ToString().Trim()); cell = null; } }
                        continue;
                }
                if (skip > 0 || !BlockTags.Contains(tag)) continue;
                if (tag == "hr") { doc.Add(Bk("hr")); cur = null; continue; }
                if (tag == "br") { if (cell != null) cell.Append(' '); else if (pre > 0 && cur != null) cur.Runs.Add(R("\n")); else cur = null; continue; }
                if (tag == "pre") pre += close ? -1 : 1;
                if (close) { cur = null; continue; }
                if (tag[0] == 'h' && tag.Length == 2 && char.IsDigit(tag[1])) start(tag);
                else if (tag == "li") start(ordered ? "oli" : "li");
                else if (tag == "pre") start("pre");
                else if (tag == "blockquote") start("quote");
                else cur = null;
            }
            foreach (var b in doc) if (b.Runs.Count > 0) b.Runs[b.Runs.Count - 1].Text = b.Runs[b.Runs.Count - 1].Text.TrimEnd();
            return doc.Where(b => b.Kind == "hr" || b.Kind == "table" || b.PlainText.Trim().Length > 0).ToList();
        }

        static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        static List<Block> FromDocx(string path)
        {
            XDocument xd;
            ordered = new HashSet<string>();
            using (var zip = ZipFile.OpenRead(path))
            {
                var e = zip.GetEntry("word/document.xml");
                if (e == null) throw new Exception("Document Word invalide");
                using (var s = e.Open()) xd = XDocument.Load(s);
                var ne = zip.GetEntry("word/numbering.xml");
                if (ne != null)
                {
                    XDocument nd;
                    using (var s = ne.Open()) nd = XDocument.Load(s);
                    var decAbs = new HashSet<string>(nd.Root.Elements(W + "abstractNum")
                        .Where(a => a.Descendants(W + "numFmt").Select(f => (string)f.Attribute(W + "val")).FirstOrDefault() is string
                                    && (string)a.Descendants(W + "numFmt").First().Attribute(W + "val") != "bullet")
                        .Select(a => (string)a.Attribute(W + "abstractNumId")));
                    foreach (var n in nd.Root.Elements(W + "num"))
                    {
                        var an = n.Element(W + "abstractNumId");
                        if (an != null && decAbs.Contains((string)an.Attribute(W + "val"))) ordered.Add((string)n.Attribute(W + "numId"));
                    }
                }
            }
            var doc = new List<Block>();
            var body = xd.Root.Element(W + "body");
            if (body == null) return doc;
            foreach (var el in body.Elements())
            {
                if (el.Name == W + "p") doc.Add(DocxPara(el));
                else if (el.Name == W + "tbl")
                {
                    var t = Bk("table"); t.Rows = new List<List<string>>();
                    foreach (var tr in el.Elements(W + "tr"))
                        t.Rows.Add(tr.Elements(W + "tc").Select(tc => string.Join(" ", tc.Elements(W + "p").Select(p => DocxPara(p).PlainText))).ToList());
                    doc.Add(t);
                }
            }
            return doc;
        }

        static HashSet<string> ordered = new HashSet<string>();

        static Block DocxPara(XElement p)
        {
            var b = Bk("p");
            var ppr = p.Element(W + "pPr");
            if (ppr != null)
            {
                var st = ppr.Element(W + "pStyle");
                string style = st == null ? "" : ((string)st.Attribute(W + "val") ?? "").ToLowerInvariant();
                var hm = Regex.Match(style, @"^(heading|titre|berschrift)\s*(\d)$");
                if (hm.Success) b.Kind = "h" + Math.Min(6, int.Parse(hm.Groups[2].Value));
                else if (style == "title" || style == "titre") b.Kind = "h1";
                else if (style == "subtitle" || style == "sous-titre") b.Kind = "h2";
                else if (style == "code" || style.Contains("htmlpreformatted") || style == "sourcecode") b.Kind = "pre";
                else if (style == "quote" || style == "citation" || style == "intensequote") b.Kind = "quote";
                else if (ppr.Element(W + "numPr") != null || style.Contains("list"))
                {
                    var nid = ppr.Descendants(W + "numId").FirstOrDefault();
                    b.Kind = nid != null && ordered.Contains((string)nid.Attribute(W + "val")) ? "oli" : "li";
                }
                var bdr = ppr.Element(W + "pBdr");
                if (bdr != null && bdr.Element(W + "bottom") != null && !p.Descendants(W + "t").Any()) b.Kind = "hr";
            }
            foreach (var r in p.Descendants(W + "r"))
            {
                var rpr = r.Element(W + "rPr");
                bool bold = On(rpr, "b"), ital = On(rpr, "i");
                string href = null;
                var sb = new StringBuilder();
                foreach (var c in r.Elements())
                {
                    if (c.Name == W + "t") sb.Append(c.Value);
                    else if (c.Name == W + "tab") sb.Append('\t');
                    else if (c.Name == W + "br" || c.Name == W + "cr") sb.Append('\n');
                }
                if (sb.Length == 0) continue;
                var run = R(sb.ToString()); run.B = bold; run.I = ital; run.Href = href;
                b.Runs.Add(run);
            }
            return b;
        }

        static bool On(XElement rpr, string name)
        {
            if (rpr == null) return false;
            var e = rpr.Element(W + name);
            if (e == null) return false;
            var v = (string)e.Attribute(W + "val");
            return v == null || (v != "0" && v != "false" && v != "none");
        }

        static List<Block> FromRtf(string path)
        {
            var fd = new FlowDocument();
            var range = new TextRange(fd.ContentStart, fd.ContentEnd);
            using (var fs = File.OpenRead(path)) range.Load(fs, DataFormats.Rtf);
            var doc = new List<Block>();
            WalkBlocks(fd.Blocks, doc, null);
            return doc;
        }

        static void WalkBlocks(IEnumerable<System.Windows.Documents.Block> blocks, List<Block> doc, string listKind)
        {
            foreach (var bl in blocks)
            {
                var p = bl as Paragraph;
                if (p != null)
                {
                    var b = Bk(listKind ?? "p");
                    if (listKind == null && p.FontSize >= 20) b.Kind = p.FontSize >= 26 ? "h1" : "h2";
                    WalkInlines(p.Inlines, b, false, false);
                    if (b.Kind == "p" && b.Runs.Count > 0)
                    {
                        var lm = Regex.Match(b.Runs[0].Text, @"^\s*(•|·|-|\d+[.)])\s*	\s*");
                        if (lm.Success) { b.Kind = char.IsDigit(lm.Groups[1].Value[0]) ? "oli" : "li"; b.Runs[0].Text = b.Runs[0].Text.Substring(lm.Length); }
                    }
                    doc.Add(b);
                    continue;
                }
                var l = bl as List;
                if (l != null)
                {
                    bool ordered = l.MarkerStyle == TextMarkerStyle.Decimal || l.MarkerStyle == TextMarkerStyle.LowerLatin || l.MarkerStyle == TextMarkerStyle.UpperRoman;
                    foreach (var item in l.ListItems) WalkBlocks(item.Blocks, doc, ordered ? "oli" : "li");
                    continue;
                }
                var sec = bl as Section;
                if (sec != null) { WalkBlocks(sec.Blocks, doc, listKind); continue; }
                var tbl = bl as Table;
                if (tbl != null)
                {
                    var t = Bk("table"); t.Rows = new List<List<string>>();
                    foreach (var g in tbl.RowGroups)
                        foreach (var row in g.Rows)
                            t.Rows.Add(row.Cells.Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()).ToList());
                    doc.Add(t);
                }
            }
        }

        static void WalkInlines(InlineCollection inlines, Block b, bool bold, bool ital)
        {
            foreach (var inl in inlines)
            {
                bool bb = bold || inl.FontWeight.ToOpenTypeWeight() >= 600;
                bool ii = ital || inl.FontStyle == FontStyles.Italic;
                var run = inl as System.Windows.Documents.Run;
                if (run != null) { var r = R(run.Text); r.B = bb; r.I = ii; b.Runs.Add(r); continue; }
                if (inl is LineBreak) { b.Runs.Add(R("\n")); continue; }
                var span = inl as Span;
                if (span != null) WalkInlines(span.Inlines, b, bb, ii);
            }
        }

        // ---------- writers ----------

        static string ToText(List<Block> doc)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var b in doc)
            {
                if (b.Kind != "oli") n = 0;
                switch (b.Kind)
                {
                    case "hr": sb.Append("----------------------------------------\r\n"); break;
                    case "li": sb.Append("  • ").Append(b.PlainText).Append("\r\n"); break;
                    case "oli": sb.Append("  ").Append(++n).Append(". ").Append(b.PlainText).Append("\r\n"); break;
                    case "table": foreach (var r in b.Rows) sb.Append(string.Join("\t", r)).Append("\r\n"); break;
                    default:
                        if (b.Kind.StartsWith("h") && sb.Length > 0) sb.Append("\r\n");
                        sb.Append(b.PlainText.Replace("\n", "\r\n")).Append("\r\n");
                        if (b.Kind.StartsWith("h")) sb.Append("\r\n");
                        break;
                }
            }
            return sb.ToString();
        }

        static string MdInline(List<Run> runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                string t = r.Text;
                if (t.Length == 0) continue;
                if (r.Code) { sb.Append('`').Append(t).Append('`'); continue; }
                string lead = t.Substring(0, t.Length - t.TrimStart().Length), trail = t.Substring(t.TrimEnd().Length);
                t = t.Trim();
                if (t.Length == 0) { sb.Append(lead); continue; }
                if (r.Href != null) t = "[" + t + "](" + r.Href + ")";
                if (r.I) t = "*" + t + "*";
                if (r.B) t = "**" + t + "**";
                sb.Append(lead).Append(t).Append(trail);
            }
            return sb.ToString();
        }

        static string ToMarkdown(List<Block> doc)
        {
            var sb = new StringBuilder();
            int n = 0;
            string prev = null;
            foreach (var b in doc)
            {
                if (b.Kind == "p" && b.PlainText.Trim().Length == 0) continue;
                bool listy = b.Kind == "li" || b.Kind == "oli";
                if (prev != null && !(listy && prev == b.Kind)) sb.Append('\n');
                if (b.Kind != "oli") n = 0;
                switch (b.Kind)
                {
                    case "hr": sb.Append("---\n"); break;
                    case "li": sb.Append("- ").Append(MdInline(b.Runs)).Append('\n'); break;
                    case "oli": sb.Append(++n).Append(". ").Append(MdInline(b.Runs)).Append('\n'); break;
                    case "quote": sb.Append("> ").Append(MdInline(b.Runs)).Append('\n'); break;
                    case "pre": sb.Append("```\n").Append(b.PlainText).Append("\n```\n"); break;
                    case "table":
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            sb.Append("| ").Append(string.Join(" | ", b.Rows[i].Select(c => c.Replace("|", "\\|")))).Append(" |\n");
                            if (i == 0) sb.Append("|").Append(string.Concat(b.Rows[0].Select(c => " --- |"))).Append('\n');
                        }
                        break;
                    default:
                        if (b.Kind.StartsWith("h")) sb.Append(new string('#', b.Kind[1] - '0')).Append(' ');
                        var runs = b.Kind.StartsWith("h")
                            ? b.Runs.Select(r => { var c = R(r.Text); c.I = r.I; c.Code = r.Code; c.Href = r.Href; return c; }).ToList()
                            : b.Runs;
                        sb.Append(MdInline(runs).Replace("\n", "  \n")).Append('\n');
                        break;
                }
                prev = b.Kind;
            }
            return sb.ToString();
        }

        static string H(string s) { return WebUtility.HtmlEncode(s); }

        static string HtmlInline(List<Run> runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                string t = H(r.Text).Replace("\n", "<br>");
                if (r.Code) t = "<code>" + t + "</code>";
                if (r.I) t = "<em>" + t + "</em>";
                if (r.B) t = "<strong>" + t + "</strong>";
                if (r.Href != null) t = "<a href=\"" + H(r.Href) + "\">" + t + "</a>";
                sb.Append(t);
            }
            return sb.ToString();
        }

        static string ToHtml(List<Block> doc, string title, bool plain)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(H(title)).Append("</title>\n<style>\n")
              .Append("body{font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:15px;line-height:1.55;color:#1d1d1f;max-width:820px;margin:40px auto;padding:0 24px}\n")
              .Append("h1,h2,h3,h4{line-height:1.25;margin:1.4em 0 .5em}h1{font-size:2em}h2{font-size:1.5em}h3{font-size:1.2em}\n")
              .Append("pre,code{font-family:Consolas,'Cascadia Mono',monospace;background:#f3f3f5;border-radius:4px}code{padding:1px 4px}pre{padding:12px;white-space:pre-wrap}\n")
              .Append("blockquote{border-left:4px solid #d0d0d7;margin:0;padding:2px 14px;color:#555}table{border-collapse:collapse;margin:1em 0}td,th{border:1px solid #ccc;padding:5px 10px;text-align:left}th{background:#f3f3f5}\n")
              .Append(".plain p{margin:0;white-space:pre-wrap;min-height:1.55em}\n")
              .Append("@page{margin:18mm}@media print{body{margin:0;max-width:none}}*{-webkit-print-color-adjust:exact;print-color-adjust:exact}\n")
              .Append("</style>\n</head>\n<body").Append(plain ? " class=\"plain\"" : "").Append(">\n");
            string list = null;
            foreach (var b in doc)
            {
                string want = b.Kind == "li" ? "ul" : b.Kind == "oli" ? "ol" : null;
                if (list != want) { if (list != null) sb.Append("</").Append(list).Append(">\n"); if (want != null) sb.Append("<").Append(want).Append(">\n"); list = want; }
                switch (b.Kind)
                {
                    case "hr": sb.Append("<hr>\n"); break;
                    case "li": case "oli": sb.Append("<li>").Append(HtmlInline(b.Runs)).Append("</li>\n"); break;
                    case "quote": sb.Append("<blockquote><p>").Append(HtmlInline(b.Runs)).Append("</p></blockquote>\n"); break;
                    case "pre": sb.Append("<pre><code>").Append(H(b.PlainText)).Append("</code></pre>\n"); break;
                    case "table":
                        sb.Append("<table>\n");
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            string c = i == 0 ? "th" : "td";
                            sb.Append("<tr>").Append(string.Concat(b.Rows[i].Select(x => "<" + c + ">" + H(x) + "</" + c + ">"))).Append("</tr>\n");
                        }
                        sb.Append("</table>\n");
                        break;
                    default:
                        string tag = b.Kind.StartsWith("h") ? b.Kind : "p";
                        sb.Append("<").Append(tag).Append(">").Append(HtmlInline(b.Runs)).Append("</").Append(tag).Append(">\n");
                        break;
                }
            }
            if (list != null) sb.Append("</").Append(list).Append(">\n");
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        static string RtfText(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\line ");
                else if (c == '\t') sb.Append("\\tab ");
                else if (c < 128) sb.Append(c);
                else sb.Append("\\u").Append((int)(short)c).Append('?');
            }
            return sb.ToString();
        }

        static string ToRtf(List<Block> doc)
        {
            var sb = new StringBuilder();
            sb.Append("{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Calibri;}{\\f1\\fmodern Consolas;}}\\viewkind4\\uc1\n");
            int n = 0;
            foreach (var b in doc)
            {
                if (b.Kind != "oli") n = 0;
                sb.Append("\\pard\\plain\\sa120\\f0\\fs22 ");
                switch (b.Kind)
                {
                    case "hr": sb.Append("\\brdrb\\brdrs\\brdrw10\\brsp20 \\par\n"); continue;
                    case "li": sb.Append("\\fi-260\\li520 \\u8226? \\tab "); break;
                    case "oli": sb.Append("\\fi-260\\li520 ").Append(++n).Append(".\\tab "); break;
                    case "quote": sb.Append("\\li520\\i "); break;
                    case "pre": sb.Append("\\f1\\fs20 ").Append(RtfText(b.PlainText)).Append("\\par\n"); continue;
                    case "table":
                        foreach (var row in b.Rows)
                        {
                            sb.Append("\\trowd\\trgaph108");
                            for (int c = 1; c <= row.Count; c++) sb.Append("\\clbrdrt\\brdrs\\clbrdrl\\brdrs\\clbrdrb\\brdrs\\clbrdrr\\brdrs\\cellx").Append(c * 2200);
                            sb.Append(' ');
                            foreach (var cell in row) sb.Append("\\pard\\intbl ").Append(RtfText(cell)).Append("\\cell ");
                            sb.Append("\\row\n");
                        }
                        continue;
                    default:
                        if (b.Kind.StartsWith("h")) sb.Append("\\b\\fs").Append(new[] { 0, 40, 32, 28, 24, 22, 22 }[b.Kind[1] - '0']).Append(' ');
                        break;
                }
                foreach (var r in b.Runs)
                {
                    sb.Append('{');
                    string ctl = (r.B ? "\\b" : "") + (r.I ? "\\i" : "") + (r.Code ? "\\f1" : "") + (r.Href != null ? "\\ul" : "");
                    sb.Append(ctl).Append(ctl.Length > 0 ? " " : "").Append(RtfText(r.Text)).Append('}');
                }
                sb.Append("\\par\n");
            }
            sb.Append("}");
            return sb.ToString();
        }

        static string X(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
            return System.Security.SecurityElement.Escape(sb.ToString());
        }

        static string DocxRuns(List<Run> runs, bool mono)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                sb.Append("<w:r><w:rPr>");
                if (r.B) sb.Append("<w:b/>");
                if (r.I) sb.Append("<w:i/>");
                if (r.Code || mono) sb.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/>");
                if (r.Href != null) sb.Append("<w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/>");
                sb.Append("</w:rPr>");
                var parts = r.Text.Replace("\r", "").Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append("<w:br/>");
                    var tabs = parts[i].Split('\t');
                    for (int j = 0; j < tabs.Length; j++)
                    {
                        if (j > 0) sb.Append("<w:tab/>");
                        if (tabs[j].Length > 0) sb.Append("<w:t xml:space=\"preserve\">").Append(X(tabs[j])).Append("</w:t>");
                    }
                }
                sb.Append("</w:r>");
            }
            return sb.ToString();
        }

        static void WriteDocx(List<Block> doc, string dst)
        {
            var body = new StringBuilder();
            foreach (var b in doc)
            {
                switch (b.Kind)
                {
                    case "hr": body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"auto\"/></w:pBdr></w:pPr></w:p>"); break;
                    case "li": body.Append("<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>").Append(DocxRuns(b.Runs, false)).Append("</w:p>"); break;
                    case "oli": body.Append("<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr></w:pPr>").Append(DocxRuns(b.Runs, false)).Append("</w:p>"); break;
                    case "quote": body.Append("<w:p><w:pPr><w:pStyle w:val=\"Quote\"/></w:pPr>").Append(DocxRuns(b.Runs, false)).Append("</w:p>"); break;
                    case "pre": body.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr>").Append(DocxRuns(b.Runs, true)).Append("</w:p>"); break;
                    case "table":
                        body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr>");
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            body.Append("<w:tr>");
                            foreach (var c in b.Rows[i])
                                body.Append("<w:tc><w:p><w:r>").Append(i == 0 ? "<w:rPr><w:b/></w:rPr>" : "").Append("<w:t xml:space=\"preserve\">").Append(X(c)).Append("</w:t></w:r></w:p></w:tc>");
                            body.Append("</w:tr>");
                        }
                        body.Append("</w:tbl><w:p/>");
                        break;
                    default:
                        body.Append("<w:p>");
                        if (b.Kind.StartsWith("h")) body.Append("<w:pPr><w:pStyle w:val=\"Heading").Append(b.Kind[1]).Append("\"/></w:pPr>");
                        body.Append(DocxRuns(b.Runs, false)).Append("</w:p>");
                        break;
                }
            }

            const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
            string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + ns + "><w:body>" + body +
                "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1418\" w:right=\"1418\" w:bottom=\"1418\" w:left=\"1418\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

            var styles = new StringBuilder();
            styles.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles " + ns + ">")
                  .Append("<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:lang w:val=\"fr-FR\"/></w:rPr></w:rPrDefault>")
                  .Append("<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>")
                  .Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>");
            int[] sz = { 0, 36, 30, 26, 24, 22, 22 };
            for (int i = 1; i <= 6; i++)
                styles.Append("<w:style w:type=\"paragraph\" w:styleId=\"Heading").Append(i).Append("\"><w:name w:val=\"heading ").Append(i)
                      .Append("\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"")
                      .Append(i - 1).Append("\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"1F3864\"/><w:sz w:val=\"").Append(sz[i]).Append("\"/></w:rPr></w:style>");
            styles.Append("<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"720\"/></w:pPr></w:style>")
                  .Append("<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:ind w:left=\"720\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"595959\"/></w:rPr></w:style>")
                  .Append("<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:sz w:val=\"20\"/></w:rPr></w:style>")
                  .Append("<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/></w:tblBorders><w:tblCellMar><w:left w:w=\"108\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>")
                  .Append("</w:styles>");

            string numbering = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering " + ns + ">" +
                "<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"singleLevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"\u2022\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
                "<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"singleLevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>";

            string contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/></Types>";
            string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
            string docRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>";

            Zip.Write(dst, new Dictionary<string, string>
            {
                {"[Content_Types].xml", contentTypes}, {"_rels/.rels", rels}, {"word/document.xml", document},
                {"word/styles.xml", styles.ToString()}, {"word/numbering.xml", numbering}, {"word/_rels/document.xml.rels", docRels}
            });
        }
    }

    internal static class Zip
    {
        public static void Write(string dst, Dictionary<string, string> parts)
        {
            using (var fs = new FileStream(dst, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kv in parts)
                {
                    var e = zip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
                }
            }
        }
    }
}
