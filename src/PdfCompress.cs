using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoueConvert
{
    // Built-in PDF optimiser: recompresses / downsizes embedded images as JPEG, deflates the other streams,
    // unpacks object streams and drops every object no longer reachable from the trailer.
    // Text, fonts and vector content are kept as they are (still selectable).
    internal static class PdfCompress
    {
        static readonly Encoding L1 = Encoding.GetEncoding(28591);

        class Obj
        {
            public int Num, Gen;
            public long Pos;
            public string Body;     // non-stream value
            public string Dict;     // stream dictionary
            public byte[] Data;     // stream bytes
            public bool IsStream { get { return Data != null; } }
            public string Text { get { return IsStream ? Dict : Body; } }
        }

        public static void Compress(string src, string dst, bool strong, Action<double> progress)
        {
            byte[] raw = File.ReadAllBytes(src);
            string s = L1.GetString(raw);
            int head = s.IndexOf("%PDF-", StringComparison.Ordinal);
            if (head < 0 || head > 1024) throw new Exception("Ce fichier n'est pas un PDF valide");
            string version = s.Substring(head, 8);

            var objs = Parse(s, raw);
            ExpandObjectStreams(objs);
            var trailer = FindTrailer(s, objs);
            if (Get(trailer, "/Encrypt") != null) throw new Exception("PDF protégé : compression impossible");
            if (Get(trailer, "/Root") == null) throw new Exception("Structure du PDF non reconnue");

            var reach = Reachable(objs, trailer);

            // Image masks must stay lossless.
            var masks = new HashSet<int>();
            foreach (var n in reach)
            {
                Obj o = objs[n];
                if (!o.IsStream) continue;
                var d = ParseDict(o.Dict);
                foreach (var key in new[] { "/SMask", "/Mask" })
                {
                    int r = RefNum(Get(d, key));
                    if (r >= 0) masks.Add(r);
                }
            }

            var streams = reach.Where(n => objs[n].IsStream).ToList();
            int done = 0;
            foreach (var n in streams)
            {
                Obj o = objs[n];
                try { Optimise(o, objs, masks.Contains(n), strong); } catch { }
                // Always store the real length directly.
                var d = ParseDict(o.Dict);
                Set(d, "/Length", o.Data.Length.ToString(CultureInfo.InvariantCulture));
                o.Dict = WriteDict(d);
                progress(0.95 * ++done / streams.Count);
            }

            reach = Reachable(objs, trailer);
            Write(dst, version, objs, reach, trailer);
            progress(1);

            long before = raw.LongLength, after = new FileInfo(dst).Length;
            if (after > before * 0.97)
            {
                File.Delete(dst);
                throw new Exception("Ce PDF est déjà optimisé (gain inférieur à 3 %)");
            }
        }

        // ---------- image & stream optimisation ----------

        static void Optimise(Obj o, Dictionary<int, Obj> objs, bool isMask, bool strong)
        {
            var d = ParseDict(o.Dict);
            var filters = Names(Get(d, "/Filter"));
            bool image = Get(d, "/Subtype") == "/Image";

            if (image && !isMask && TryRecompressImage(o, d, filters, objs, strong)) return;

            // Plain deflate for everything else (content streams, fonts, masks...).
            if (filters.Count == 0)
            {
                byte[] z = Deflate(o.Data);
                if (z.Length < o.Data.Length)
                {
                    o.Data = z;
                    Set(d, "/Filter", "/FlateDecode");
                    o.Dict = WriteDict(d);
                }
            }
            else if (filters.Count == 1 && filters[0] == "/FlateDecode" && Get(d, "/DecodeParms") == null)
            {
                byte[] plain = Inflate(o.Data);
                if (plain == null) return;
                byte[] z = Deflate(plain);
                if (z.Length < o.Data.Length) o.Data = z;
            }
        }

        static bool TryRecompressImage(Obj o, List<KeyValuePair<string, string>> d, List<string> filters,
                                       Dictionary<int, Obj> objs, bool strong)
        {
            if (Get(d, "/ImageMask") == "true" || Get(d, "/Decode") != null) return false;
            int w = Int(Get(d, "/Width")), h = Int(Get(d, "/Height"));
            if (w <= 0 || h <= 0 || (long)w * h < 120 * 120 || o.Data.Length < 12000) return false;

            int comps = Components(Resolve(Get(d, "/ColorSpace"), objs), objs);
            if (comps != 1 && comps != 3) return false;

            bool dct = filters.Count >= 1 && filters[filters.Count - 1] == "/DCTDecode";
            if (filters.Any(f => f != "/FlateDecode" && f != "/DCTDecode")) return false;
            if (filters.Count(f => f == "/DCTDecode") > 1 || (filters.Count == 2 && !(filters[0] == "/FlateDecode" && dct))) return false;

            byte[] data = o.Data;
            if (filters.Count > 0 && filters[0] == "/FlateDecode")
            {
                data = Inflate(data);
                if (data == null) return false;
            }

            BitmapSource img;
            if (dct)
            {
                var dec = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                img = dec.Frames[0];
                if (img.Format == PixelFormats.Cmyk32) return false;
            }
            else
            {
                if (Int(Get(d, "/BitsPerComponent")) != 8) return false;
                var parms = ParseDict(Resolve(Get(d, "/DecodeParms"), objs) ?? "<<>>");
                int pred = Int(Get(parms, "/Predictor"));
                if (pred >= 10) data = Unpredict(data, w, comps);
                else if (pred > 1) return false;
                if (data == null || data.Length < (long)w * h * comps) return false;
                img = BitmapSource.Create(w, h, 96, 96, comps == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, data, w * comps);
            }

            int maxSide = strong ? 1400 : 2200;
            int quality = strong ? 50 : 72;
            int nw = w, nh = h;
            if (Math.Max(w, h) > maxSide)
            {
                double k = (double)maxSide / Math.Max(w, h);
                nw = Math.Max(1, (int)Math.Round(w * k));
                nh = Math.Max(1, (int)Math.Round(h * k));
                img = Resize(img, nw, nh);
            }
            BitmapSource final = comps == 1
                ? (BitmapSource)new FormatConvertedBitmap(img, PixelFormats.Gray8, null, 0)
                : new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);
            var enc = new JpegBitmapEncoder();
            enc.QualityLevel = quality;
            enc.Frames.Add(BitmapFrame.Create(final));
            byte[] jpg;
            using (var ms = new MemoryStream()) { enc.Save(ms); jpg = ms.ToArray(); }

            if (jpg.Length > o.Data.Length * 0.9) return false;
            o.Data = jpg;
            Set(d, "/Filter", "/DCTDecode");
            Remove(d, "/DecodeParms");
            Set(d, "/Width", nw.ToString(CultureInfo.InvariantCulture));
            Set(d, "/Height", nh.ToString(CultureInfo.InvariantCulture));
            Set(d, "/BitsPerComponent", "8");
            o.Dict = WriteDict(d);
            return true;
        }

        static BitmapSource Resize(BitmapSource src, int w, int h)
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        static int Components(string cs, Dictionary<int, Obj> objs)
        {
            if (cs == null) return 0;
            cs = cs.Trim();
            if (cs == "/DeviceRGB" || cs == "/CalRGB") return 3;
            if (cs == "/DeviceGray" || cs == "/CalGray") return 1;
            var m = Regex.Match(cs, @"^\[\s*/ICCBased\s+(\d+)\s+\d+\s+R\s*\]$");
            if (m.Success)
            {
                Obj icc;
                if (objs.TryGetValue(int.Parse(m.Groups[1].Value), out icc) && icc.IsStream)
                    return Int(Get(ParseDict(icc.Dict), "/N"));
            }
            return 0; // Indexed, CMYK, Lab, Separation...: left untouched
        }

        // PNG predictors (Predictor >= 10), 8 bits per component.
        static byte[] Unpredict(byte[] data, int w, int comps)
        {
            int row = w * comps, bpp = comps;
            int rows = data.Length / (row + 1);
            var o = new byte[rows * row];
            var prev = new byte[row];
            for (int y = 0; y < rows; y++)
            {
                int type = data[y * (row + 1)];
                int si = y * (row + 1) + 1, di = y * row;
                for (int x = 0; x < row; x++)
                {
                    int a = x >= bpp ? o[di + x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                    int v = data[si + x];
                    switch (type)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) / 2; break;
                        case 4:
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        case 0: break;
                        default: return null;
                    }
                    o[di + x] = (byte)v;
                }
                Buffer.BlockCopy(o, di, prev, 0, row);
            }
            return o;
        }

        // ---------- zlib ----------

        static byte[] Inflate(byte[] z)
        {
            if (z.Length < 3 || (z[0] & 0x0F) != 8) return null;
            try
            {
                using (var ds = new DeflateStream(new MemoryStream(z, 2, z.Length - 2), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[65536];
                    try
                    {
                        int r;
                        while ((r = ds.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, r);
                    }
                    catch (InvalidDataException) { if (ms.Length == 0) return null; } // tolerate a damaged tail
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0xDA);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        // ---------- parsing ----------

        static readonly Regex RxObj = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b");
        static readonly Regex RxIntObj = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\s*(\d+)\s*endobj");
        static readonly Regex RxRef = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+R(?![A-Za-z])");

        static Dictionary<int, Obj> Parse(string s, byte[] raw)
        {
            var ints = new Dictionary<int, int>();
            foreach (Match m in RxIntObj.Matches(s)) ints[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[3].Value);

            var all = new Dictionary<int, Obj>();
            int pos = 0;
            while (true)
            {
                var m = RxObj.Match(s, pos);
                if (!m.Success) break;
                int p = SkipWs(s, m.Index + m.Length);
                int end = SkipValue(s, p);
                if (end < 0) { pos = m.Index + m.Length; continue; }
                var o = new Obj { Num = int.Parse(m.Groups[1].Value), Gen = int.Parse(m.Groups[2].Value), Pos = m.Index };
                string value = s.Substring(p, end - p);
                int q = SkipWs(s, end);
                if (string.CompareOrdinal(s, q, "stream", 0, 6) == 0)
                {
                    int ds = q + 6;
                    if (ds < s.Length && s[ds] == '\r') ds++;
                    if (ds < s.Length && s[ds] == '\n') ds++;
                    int len = -1;
                    string lv = Get(ParseDict(value), "/Length");
                    if (lv != null)
                    {
                        int r = RefNum(lv);
                        if (r >= 0) { int v; if (ints.TryGetValue(r, out v)) len = v; }
                        else len = Int(lv);
                    }
                    int de;
                    if (len >= 0 && ds + len <= s.Length && string.CompareOrdinal(s, SkipWs(s, ds + len), "endstream", 0, 9) == 0)
                        de = ds + len;
                    else
                    {
                        int es = s.IndexOf("endstream", ds, StringComparison.Ordinal);
                        if (es < 0) break;
                        de = es;
                        if (de > ds && s[de - 1] == '\n') de--;
                        if (de > ds && s[de - 1] == '\r') de--;
                    }
                    o.Dict = value;
                    o.Data = new byte[de - ds];
                    Buffer.BlockCopy(raw, ds, o.Data, 0, de - ds);
                    pos = s.IndexOf("endstream", de, StringComparison.Ordinal) + 9;
                }
                else
                {
                    o.Body = value;
                    pos = q;
                }
                Obj prev;
                if (!all.TryGetValue(o.Num, out prev) || prev.Pos <= o.Pos) all[o.Num] = o;
            }
            if (all.Count == 0) throw new Exception("Structure du PDF non reconnue");
            return all;
        }

        static void ExpandObjectStreams(Dictionary<int, Obj> objs)
        {
            foreach (var os in objs.Values.Where(o => o.IsStream && Get(ParseDict(o.Dict), "/Type") == "/ObjStm").ToList())
            {
                var d = ParseDict(os.Dict);
                var f = Names(Get(d, "/Filter"));
                byte[] data = os.Data;
                if (f.Count == 1 && f[0] == "/FlateDecode") data = Inflate(data);
                else if (f.Count != 0) data = null;
                if (data == null) throw new Exception("Flux d'objets du PDF illisible");
                string t = L1.GetString(data);
                int n = Int(Get(d, "/N")), first = Int(Get(d, "/First"));
                var nums = Regex.Matches(t.Substring(0, Math.Min(first, t.Length)), @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
                for (int i = 0; i < n && 2 * i + 1 < nums.Count; i++)
                {
                    int num = nums[2 * i], start = first + nums[2 * i + 1];
                    int stop = 2 * i + 3 < nums.Count && i + 1 < n ? first + nums[2 * i + 3] : t.Length;
                    if (start > t.Length || stop > t.Length || stop < start) continue;
                    var o = new Obj { Num = num, Gen = 0, Pos = os.Pos + 1, Body = t.Substring(start, stop - start).Trim() };
                    Obj prev;
                    if (!objs.TryGetValue(num, out prev) || prev.Pos < o.Pos) objs[num] = o;
                }
            }
        }

        static List<KeyValuePair<string, string>> FindTrailer(string s, Dictionary<int, Obj> objs)
        {
            var result = new List<KeyValuePair<string, string>>();
            var sources = new List<List<KeyValuePair<string, string>>>();
            int t = s.LastIndexOf("trailer", StringComparison.Ordinal);
            while (t >= 0)
            {
                int p = SkipWs(s, t + 7);
                int e = SkipValue(s, p);
                if (e > p && s[p] == '<') sources.Add(ParseDict(s.Substring(p, e - p)));
                t = t > 0 ? s.LastIndexOf("trailer", t - 1, StringComparison.Ordinal) : -1;
            }
            foreach (var x in objs.Values.Where(o => o.IsStream && Get(ParseDict(o.Dict), "/Type") == "/XRef").OrderByDescending(o => o.Pos))
                sources.Add(ParseDict(x.Dict));
            foreach (var key in new[] { "/Root", "/Info", "/ID", "/Encrypt" })
                foreach (var src in sources)
                {
                    string v = Get(src, key);
                    if (v != null) { result.Add(new KeyValuePair<string, string>(key, v)); break; }
                }
            return result;
        }

        static HashSet<int> Reachable(Dictionary<int, Obj> objs, List<KeyValuePair<string, string>> trailer)
        {
            var seen = new HashSet<int>();
            var todo = new Stack<int>();
            foreach (var kv in trailer) foreach (Match m in RxRef.Matches(kv.Value)) todo.Push(int.Parse(m.Groups[1].Value));
            while (todo.Count > 0)
            {
                int n = todo.Pop();
                Obj o;
                if (!seen.Add(n) || !objs.TryGetValue(n, out o)) continue;
                foreach (Match m in RxRef.Matches(o.Text)) todo.Push(int.Parse(m.Groups[1].Value));
            }
            seen.RemoveWhere(n => !objs.ContainsKey(n));
            return seen;
        }

        static bool IsWs(char c) { return c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == '\0'; }
        static bool IsDelim(char c) { return IsWs(c) || "()<>[]{}/%".IndexOf(c) >= 0; }

        static int SkipWs(string s, int p)
        {
            while (p < s.Length)
            {
                if (IsWs(s[p])) p++;
                else if (s[p] == '%') { while (p < s.Length && s[p] != '\n' && s[p] != '\r') p++; }
                else break;
            }
            return p;
        }

        // Returns the index just past one PDF value starting at p, or -1.
        static int SkipValue(string s, int p)
        {
            if (p >= s.Length) return -1;
            char c = s[p];
            if (c == '<' && p + 1 < s.Length && s[p + 1] == '<')
            {
                p += 2;
                while (true)
                {
                    p = SkipWs(s, p);
                    if (p >= s.Length) return -1;
                    if (s[p] == '>' && p + 1 < s.Length && s[p + 1] == '>') return p + 2;
                    p = SkipValue(s, p);
                    if (p < 0) return -1;
                }
            }
            if (c == '[')
            {
                p++;
                while (true)
                {
                    p = SkipWs(s, p);
                    if (p >= s.Length) return -1;
                    if (s[p] == ']') return p + 1;
                    p = SkipValue(s, p);
                    if (p < 0) return -1;
                }
            }
            if (c == '(')
            {
                int depth = 0;
                for (; p < s.Length; p++)
                {
                    if (s[p] == '\\') { p++; continue; }
                    if (s[p] == '(') depth++;
                    else if (s[p] == ')' && --depth == 0) return p + 1;
                }
                return -1;
            }
            if (c == '<') { int e = s.IndexOf('>', p); return e < 0 ? -1 : e + 1; }
            if (c == '/')
            {
                p++;
                while (p < s.Length && !IsDelim(s[p])) p++;
                return p;
            }
            if (c == ')' || c == '>' || c == ']' || c == '}') return -1;
            int start = p;
            while (p < s.Length && !IsDelim(s[p])) p++;
            if (p == start) return -1;
            // "12 0 R" reference
            string tok = s.Substring(start, p - start);
            if (Regex.IsMatch(tok, @"^\d+$"))
            {
                var m = Regex.Match(s.Substring(p, Math.Min(24, s.Length - p)), @"^\s+\d+\s+R(?![A-Za-z])");
                if (m.Success) return p + m.Length;
            }
            return p;
        }

        static List<KeyValuePair<string, string>> ParseDict(string d)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (d == null) return list;
            d = d.Trim();
            if (!d.StartsWith("<<")) return list;
            int p = 2;
            while (true)
            {
                p = SkipWs(d, p);
                if (p >= d.Length || (d[p] == '>' && p + 1 < d.Length && d[p + 1] == '>')) break;
                if (d[p] != '/') { int e0 = SkipValue(d, p); if (e0 < 0) break; p = e0; continue; }
                int ke = SkipValue(d, p);
                string key = d.Substring(p, ke - p);
                int vs = SkipWs(d, ke);
                int ve = SkipValue(d, vs);
                if (ve < 0) break;
                list.Add(new KeyValuePair<string, string>(key, d.Substring(vs, ve - vs)));
                p = ve;
            }
            return list;
        }

        static string WriteDict(List<KeyValuePair<string, string>> d)
        {
            var sb = new StringBuilder("<<");
            foreach (var kv in d) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append(' ');
            return sb.Append(">>").ToString();
        }

        static string Get(List<KeyValuePair<string, string>> d, string key)
        {
            foreach (var kv in d) if (kv.Key == key) return kv.Value;
            return null;
        }

        static void Set(List<KeyValuePair<string, string>> d, string key, string value)
        {
            for (int i = 0; i < d.Count; i++)
                if (d[i].Key == key) { d[i] = new KeyValuePair<string, string>(key, value); return; }
            d.Add(new KeyValuePair<string, string>(key, value));
        }

        static void Remove(List<KeyValuePair<string, string>> d, string key) { d.RemoveAll(kv => kv.Key == key); }

        static string Resolve(string v, Dictionary<int, Obj> objs)
        {
            int r = RefNum(v);
            Obj o;
            if (r >= 0 && objs.TryGetValue(r, out o)) return o.Text;
            return v;
        }

        static int RefNum(string v)
        {
            if (v == null) return -1;
            var m = Regex.Match(v.Trim(), @"^(\d+)\s+\d+\s+R$");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        static List<string> Names(string v)
        {
            if (v == null) return new List<string>();
            return Regex.Matches(v, @"/[^\s/\[\]<>()]+").Cast<Match>().Select(m => m.Value).ToList();
        }

        static int Int(string v)
        {
            int i;
            return v != null && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : -1;
        }

        // ---------- writing ----------

        static void Write(string dst, string version, Dictionary<int, Obj> objs, HashSet<int> keep,
                          List<KeyValuePair<string, string>> trailer)
        {
            int max = keep.Count == 0 ? 0 : keep.Max();
            var offsets = new Dictionary<int, long>();
            using (var fs = new FileStream(dst, FileMode.CreateNew))
            {
                W(fs, version + "\n%âãÏÓ\n");
                foreach (int n in keep.OrderBy(x => x))
                {
                    Obj o = objs[n];
                    offsets[n] = fs.Position;
                    W(fs, n + " " + o.Gen + " obj\n");
                    if (o.IsStream)
                    {
                        W(fs, o.Dict + "\nstream\n");
                        fs.Write(o.Data, 0, o.Data.Length);
                        W(fs, "\nendstream");
                    }
                    else W(fs, o.Body);
                    W(fs, "\nendobj\n");
                }
                long xref = fs.Position;
                var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(max + 1).Append('\n').Append("0000000000 65535 f \n");
                for (int n = 1; n <= max; n++)
                {
                    long off;
                    if (offsets.TryGetValue(n, out off)) sb.Append(off.ToString("0000000000")).Append(' ').Append(objs[n].Gen.ToString("00000")).Append(" n \n");
                    else sb.Append("0000000000 65535 f \n");
                }
                var t = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("/Size", (max + 1).ToString(CultureInfo.InvariantCulture)) };
                t.AddRange(trailer.Where(kv => kv.Key != "/Encrypt"));
                sb.Append("trailer\n").Append(WriteDict(t)).Append("\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                W(fs, sb.ToString());
            }
        }

        static void W(Stream s, string text) { byte[] b = L1.GetBytes(text); s.Write(b, 0, b.Length); }
    }
}
