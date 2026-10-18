using System;
using System.Collections.Generic;
using System.IO;

namespace RoueConvert
{
    // Entry point for every conversion: picks the engine and builds a non-clashing output name next to the source.
    internal static class Converter
    {
        // progress: 0..1, or -1 when unknown.
        public static List<string> Convert(string src, string target, Action<double> progress)
        {
            if (progress == null) progress = delegate { };
            if (!File.Exists(src)) throw new Exception("Fichier introuvable");
            string se = Formats.Norm(src);
            target = Formats.Norm(target);
            string dir = Path.GetDirectoryName(src);
            string baseName = Path.GetFileNameWithoutExtension(src);
            var outs = new List<string>();

            if (se == "pdf")
            {
                outs.AddRange(PdfRender.ToImages(src, dir, baseName, target, progress));
                return outs;
            }

            string dst = UniquePath(dir, baseName, target);
            try
            {
                if (Formats.IsAudio(se) || Formats.IsVideo(se) || (se == "gif" && (target == "mp4" || target == "webm")))
                    Media.Convert(src, dst, target, progress);
                else if (se == "svg")
                    Edge.SvgTo(src, dst, target);
                else if (Formats.IsRaster(se))
                    ImageConv.Convert(src, dst, target);
                else if (Formats.IsText(se))
                    DocConv.Convert(src, se, dst, target);
                else if (Formats.IsData(se))
                    DataConv.Convert(src, se, dst, target);
                else
                    throw new Exception("Conversion non prise en charge");
            }
            catch
            {
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                throw;
            }
            if (!File.Exists(dst)) throw new Exception("Aucun fichier produit");
            outs.Add(dst);
            return outs;
        }

        public static string UniquePath(string dir, string baseName, string ext)
        {
            string p = Path.Combine(dir, baseName + "." + ext);
            for (int i = 1; File.Exists(p); i++)
                p = Path.Combine(dir, baseName + " (" + i + ")." + ext);
            return p;
        }
    }
}
