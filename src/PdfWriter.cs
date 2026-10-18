using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace RoueConvert
{
    // Minimal PDF writer: one JPEG image per page, page size = image size.
    internal static class PdfWriter
    {
        public static void FromImages(IList<BitmapSource> images, string dst)
        {
            var objs = new List<byte[]>();
            var kids = new StringBuilder();
            int n = images.Count;
            objs.Add(null); // 1 catalog
            objs.Add(null); // 2 pages
            for (int k = 0; k < n; k++)
            {
                var img = images[k];
                int pageObj = 3 + k * 3, contentObj = 4 + k * 3, imgObj = 5 + k * 3;
                double w = img.PixelWidth * 72.0 / 96, h = img.PixelHeight * 72.0 / 96;
                double s = Math.Min(1, 14400 / Math.Max(w, h)); // PDF page limit: 200 inches
                w *= s; h *= s;
                kids.Append(pageObj).Append(" 0 R ");
                objs.Add(A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(w) + " " + F(h) + "] /Resources << /XObject << /Im0 " +
                           imgObj + " 0 R >> >> /Contents " + contentObj + " 0 R >>"));
                byte[] content = A("q " + F(w) + " 0 0 " + F(h) + " 0 0 cm /Im0 Do Q");
                objs.Add(Stream("<< /Length " + content.Length + " >>", content));
                byte[] jpg = ImageConv.Jpeg(img, 90);
                objs.Add(Stream("<< /Type /XObject /Subtype /Image /Width " + img.PixelWidth + " /Height " + img.PixelHeight +
                                " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>", jpg));
            }
            objs[0] = A("<< /Type /Catalog /Pages 2 0 R >>");
            objs[1] = A("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " + n + " >>");

            using (var fs = new FileStream(dst, FileMode.CreateNew))
            {
                var offsets = new List<long>();
                Write(fs, A("%PDF-1.4\n%âãÏÓ\n"));
                for (int i = 0; i < objs.Count; i++)
                {
                    offsets.Add(fs.Position);
                    Write(fs, A((i + 1) + " 0 obj\n"));
                    Write(fs, objs[i]);
                    Write(fs, A("\nendobj\n"));
                }
                long xref = fs.Position;
                var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
                foreach (var o in offsets) sb.Append(o.ToString("0000000000")).Append(" 00000 n \n");
                sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                Write(fs, A(sb.ToString()));
            }
        }

        static byte[] Stream(string dict, byte[] data)
        {
            var ms = new MemoryStream();
            Write(ms, A(dict + "\nstream\n"));
            Write(ms, data);
            Write(ms, A("\nendstream"));
            return ms.ToArray();
        }

        static void Write(Stream s, byte[] b) { s.Write(b, 0, b.Length); }
        static byte[] A(string s) { return Encoding.GetEncoding(28591).GetBytes(s); }
        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
