using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoueConvert
{
    // Microsoft Edge in headless mode: HTML -> PDF printing and SVG rasterising.
    internal static class Edge
    {
        static string Common(string profile)
        {
            return "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
                   "--disable-sync --hide-scrollbars --user-data-dir=\"" + profile + "\" ";
        }

        static void RunEdge(string args, string expectedOut, string tmp)
        {
            if (Tools.Edge == null) throw new Exception("Microsoft Edge introuvable");
            string tail;
            Tools.Run(Tools.Edge, Common(Path.Combine(tmp, "profile")) + args, null, 120000, out tail);
            // Edge may hand off to a child process: give the file a moment to appear.
            for (int i = 0; i < 50 && !(File.Exists(expectedOut) && new FileInfo(expectedOut).Length > 0); i++)
                System.Threading.Thread.Sleep(100);
            if (!File.Exists(expectedOut) || new FileInfo(expectedOut).Length == 0)
                throw new Exception("Edge n'a rien produit");
        }

        public static void PrintToPdf(string htmlPath, string pdfOut)
        {
            string tmp = Tools.TempDir();
            try
            {
                string tmpPdf = Path.Combine(tmp, "out.pdf");
                RunEdge("--no-pdf-header-footer --print-to-pdf=\"" + tmpPdf + "\" \"" + new Uri(htmlPath).AbsoluteUri + "\"", tmpPdf, tmp);
                File.Copy(tmpPdf, pdfOut, false);
            }
            finally { Tools.TryDelete(tmp); }
        }

        public static void Screenshot(string htmlPath, string pngOut, int w, int h)
        {
            string tmp = Tools.TempDir();
            try
            {
                string tmpPng = Path.Combine(tmp, "shot.png");
                RunEdge("--default-background-color=00000000 --window-size=" + w + "," + h +
                        " --screenshot=\"" + tmpPng + "\" \"" + new Uri(htmlPath).AbsoluteUri + "\"", tmpPng, tmp);
                File.Copy(tmpPng, pngOut, true);
            }
            finally { Tools.TryDelete(tmp); }
        }

        public static void SvgTo(string svg, string dst, string target)
        {
            double w, h;
            SvgSize(svg, out w, out h);
            string tmp = Tools.TempDir();
            try
            {
                string uri = new Uri(svg).AbsoluteUri;
                if (target == "pdf")
                {
                    string html = Path.Combine(tmp, "page.html");
                    File.WriteAllText(html, "<!doctype html><html><head><meta charset=\"utf-8\"><style>@page{size:" + F(w) + "px " + F(h) +
                        "px;margin:0}html,body{margin:0;padding:0}img{display:block;width:" + F(w) + "px;height:" + F(h) +
                        "px}</style></head><body><img src=\"" + uri + "\"></body></html>", Encoding.UTF8);
                    PrintToPdf(html, dst);
                    return;
                }
                int pw, ph;
                if (target == "ico") { pw = ph = 256; }
                else
                {
                    double s = Math.Max(1, 1024 / Math.Max(w, h));
                    pw = (int)Math.Round(w * s); ph = (int)Math.Round(h * s);
                }
                string page = Path.Combine(tmp, "shot.html");
                File.WriteAllText(page, "<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}" +
                    "img{display:block;width:" + pw + "px;height:" + ph + "px;object-fit:contain}</style></head><body><img src=\"" + uri + "\"></body></html>", Encoding.UTF8);
                string png = Path.Combine(tmp, "out.png");
                Screenshot(page, png, pw, ph);
                ImageConv.Convert(png, dst, target);
            }
            finally { Tools.TryDelete(tmp); }
        }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static void SvgSize(string svg, out double w, out double h)
        {
            w = h = 0;
            try
            {
                var root = XDocument.Load(svg).Root;
                w = Len((string)root.Attribute("width"));
                h = Len((string)root.Attribute("height"));
                var vb = (string)root.Attribute("viewBox");
                if ((w <= 0 || h <= 0) && vb != null)
                {
                    var p = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 4)
                    {
                        double vw = double.Parse(p[2], CultureInfo.InvariantCulture), vh = double.Parse(p[3], CultureInfo.InvariantCulture);
                        if (w > 0) h = w * vh / vw;
                        else if (h > 0) w = h * vw / vh;
                        else { w = vw; h = vh; }
                    }
                }
            }
            catch { }
            if (w <= 0 || h <= 0) { w = 512; h = 512; }
        }

        static double Len(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Contains("%")) return 0;
            var m = Regex.Match(s, @"^\s*([\d.]+)\s*(px|pt|mm|cm|in|em)?\s*$");
            if (!m.Success) return 0;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value)
            {
                case "pt": return v * 96 / 72;
                case "mm": return v * 96 / 25.4;
                case "cm": return v * 96 / 2.54;
                case "in": return v * 96;
                case "em": return v * 16;
            }
            return v;
        }
    }
}
