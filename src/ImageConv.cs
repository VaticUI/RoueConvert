using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoueConvert
{
    // Raster images through WIC (WPF imaging); ffmpeg as fallback decoder (webp/heic/avif) and webp encoder.
    internal static class ImageConv
    {
        public static void Convert(string src, string dst, string target)
        {
            var img = Load(src);
            Save(img, dst, target);
        }

        public static BitmapSource Load(string path)
        {
            try { return LoadWic(path); }
            catch (Exception)
            {
                if (Tools.Ffmpeg == null) throw new Exception("Format d'image non lisible par Windows");
                string tmp = Tools.TempDir();
                try
                {
                    string png = Path.Combine(tmp, "in.png");
                    Media.Run(path, png, "-frames:v 1", null);
                    return LoadWic(png);
                }
                finally { Tools.TryDelete(tmp); }
            }
        }

        static BitmapSource LoadWic(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame best = dec.Frames[0];
                foreach (var f in dec.Frames) // .ico: keep the biggest size
                    if ((long)f.PixelWidth * f.PixelHeight > (long)best.PixelWidth * best.PixelHeight) best = f;
                BitmapSource b = Orient(best);
                b.Freeze();
                return b;
            }
        }

        // Apply the EXIF orientation tag (phone photos).
        static BitmapSource Orient(BitmapFrame f)
        {
            int o = 1;
            try
            {
                var md = f.Metadata as BitmapMetadata;
                if (md != null)
                {
                    object v = md.ContainsQuery("/app1/ifd/{ushort=274}") ? md.GetQuery("/app1/ifd/{ushort=274}") : null;
                    if (v != null) o = System.Convert.ToInt32(v);
                }
            }
            catch { }
            Transform t = null;
            switch (o)
            {
                case 2: t = new ScaleTransform(-1, 1); break;
                case 3: t = new RotateTransform(180); break;
                case 4: t = new ScaleTransform(1, -1); break;
                case 5: var g5 = new TransformGroup(); g5.Children.Add(new RotateTransform(90)); g5.Children.Add(new ScaleTransform(-1, 1)); t = g5; break;
                case 6: t = new RotateTransform(90); break;
                case 7: var g7 = new TransformGroup(); g7.Children.Add(new RotateTransform(270)); g7.Children.Add(new ScaleTransform(-1, 1)); t = g7; break;
                case 8: t = new RotateTransform(270); break;
            }
            if (t == null) return f;
            return new TransformedBitmap(f, t);
        }

        public static void Save(BitmapSource img, string dst, string target)
        {
            BitmapEncoder enc;
            switch (target)
            {
                case "png": enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); break;
                case "jpg": var je = new JpegBitmapEncoder(); je.QualityLevel = 92; enc = je; enc.Frames.Add(BitmapFrame.Create(Flatten(img))); break;
                case "bmp": enc = new BmpBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Flatten(img))); break;
                case "gif": enc = new GifBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); break;
                case "tiff": var te = new TiffBitmapEncoder(); te.Compression = TiffCompressOption.Lzw; enc = te; enc.Frames.Add(BitmapFrame.Create(img)); break;
                case "ico": SaveIco(img, dst); return;
                case "pdf": PdfWriter.FromImages(new List<BitmapSource> { img }, dst); return;
                case "webp":
                    {
                        string tmp = Tools.TempDir();
                        try
                        {
                            string png = Path.Combine(tmp, "in.png");
                            Save(img, png, "png");
                            Media.Run(png, dst, "-c:v libwebp -quality 90", null);
                        }
                        finally { Tools.TryDelete(tmp); }
                        return;
                    }
                default: throw new Exception("Format " + target + " non pris en charge");
            }
            using (var fs = new FileStream(dst, FileMode.CreateNew)) enc.Save(fs);
        }

        // Transparent pixels onto white, 24-bit (for JPEG / BMP / PDF).
        public static BitmapSource Flatten(BitmapSource src)
        {
            var c = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = c.PixelWidth, h = c.PixelHeight;
            var px = new byte[w * h * 4];
            c.CopyPixels(px, w * 4, 0);
            int stride = (w * 3 + 3) & ~3;
            var o = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                int si = y * w * 4, di = y * stride;
                for (int x = 0; x < w; x++, si += 4, di += 3)
                {
                    int a = px[si + 3], ia = 255 - a;
                    o[di] = (byte)((px[si] * a + 255 * ia) / 255);
                    o[di + 1] = (byte)((px[si + 1] * a + 255 * ia) / 255);
                    o[di + 2] = (byte)((px[si + 2] * a + 255 * ia) / 255);
                }
            }
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, o, stride);
            b.Freeze();
            return b;
        }

        // Fits the image in a transparent square (icons).
        public static BitmapSource FitSquare(BitmapSource src, int size)
        {
            double s = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
            double w = src.PixelWidth * s, h = src.PixelHeight * s;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
                dc.DrawImage(src, new Rect((size - w) / 2, (size - h) / 2, w, h));
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        static void SaveIco(BitmapSource img, string dst)
        {
            int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(FitSquare(img, s)));
                using (var ms = new MemoryStream()) { enc.Save(ms); pngs.Add(ms.ToArray()); }
            }
            using (var fs = new FileStream(dst, FileMode.CreateNew))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    bw.Write(d); bw.Write(d); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((short)1); bw.Write((short)32);
                    bw.Write(pngs[i].Length); bw.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) bw.Write(p);
            }
        }

        public static byte[] Jpeg(BitmapSource img, int quality)
        {
            var enc = new JpegBitmapEncoder();
            enc.QualityLevel = quality;
            enc.Frames.Add(BitmapFrame.Create(Flatten(img)));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }
    }
}
