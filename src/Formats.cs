using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoueConvert
{
    internal enum Fam { Image, Vector, Pdf, Audio, Video, Text, Data, None }

    // Which formats each file type can "logically" be turned into.
    internal static class Formats
    {
        static readonly Dictionary<string, string> Alias = new Dictionary<string, string>
        {
            {"jpeg","jpg"},{"jpe","jpg"},{"jfif","jpg"},{"tif","tiff"},{"htm","html"},{"markdown","md"},
            {"yml","yaml"},{"oga","ogg"},{"mpeg","mpg"},{"aif","aiff"},{"heif","heic"},{"text","txt"},{"log","txt"}
        };

        static readonly HashSet<string> Raster = new HashSet<string> { "png", "jpg", "bmp", "tiff", "ico", "webp", "heic", "avif", "gif" };
        static readonly HashSet<string> Audio = new HashSet<string> { "mp3", "wav", "flac", "ogg", "m4a", "opus", "aac", "wma", "aiff" };
        static readonly HashSet<string> Video = new HashSet<string> { "mp4", "mkv", "webm", "mov", "avi", "wmv", "flv", "m4v", "mpg", "ts", "3gp" };
        static readonly HashSet<string> TextFmt = new HashSet<string> { "txt", "md", "html", "docx", "rtf" };
        static readonly HashSet<string> DataFmt = new HashSet<string> { "csv", "tsv", "xlsx", "json", "xml", "yaml" };

        static readonly string[] RasterOut = { "png", "jpg", "webp", "bmp", "gif", "tiff", "ico", "pdf" };
        static readonly string[] AudioOut = { "mp3", "wav", "flac", "ogg", "m4a", "opus" };
        static readonly string[] VideoOut = { "mp4", "mkv", "webm", "mov", "avi", "gif", "mp3", "wav" };

        // Targets that need ffmpeg.
        static readonly HashSet<string> NeedFfmpeg = new HashSet<string> { "webp" };

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            {"png","Image"},{"jpg","Photo"},{"webp","Image web"},{"bmp","Bitmap"},{"gif","Animation"},{"tiff","Impression"},
            {"ico","Icône"},{"pdf","Document"},{"svg","Vectoriel"},{"mp3","Audio"},{"wav","Audio brut"},{"flac","Sans perte"},
            {"ogg","Vorbis"},{"m4a","AAC"},{"opus","Opus"},{"mp4","Vidéo"},{"mkv","Matroska"},{"webm","Vidéo web"},
            {"mov","QuickTime"},{"avi","Vidéo"},{"txt","Texte"},{"md","Markdown"},{"html","Page web"},{"docx","Word"},
            {"rtf","Texte riche"},{"csv","Tableur"},{"tsv","Tabulé"},{"xlsx","Excel"},{"json","Données"},{"xml","Données"},
            {"yaml","Config"}
        };

        public static string Norm(string pathOrExt)
        {
            string ext = pathOrExt ?? "";
            if (ext.IndexOf('.') >= 0 || ext.IndexOf('\\') >= 0) ext = Path.GetExtension(ext);
            ext = ext.TrimStart('.').ToLowerInvariant();
            string a;
            return Alias.TryGetValue(ext, out a) ? a : ext;
        }

        public static bool IsRaster(string e) { return Raster.Contains(e); }
        public static bool IsAudio(string e) { return Audio.Contains(e); }
        public static bool IsVideo(string e) { return Video.Contains(e); }
        public static bool IsText(string e) { return TextFmt.Contains(e); }
        public static bool IsData(string e) { return DataFmt.Contains(e); }

        public static string[] TargetsFor(string path)
        {
            string e = Norm(path);
            IEnumerable<string> t;
            if (e == "gif") t = new[] { "png", "jpg", "webp", "bmp", "tiff", "pdf", "mp4", "webm" };
            else if (e == "heic" || e == "avif") t = new[] { "jpg", "png", "webp", "bmp", "tiff", "pdf" };
            else if (Raster.Contains(e)) t = RasterOut;
            else if (e == "svg") t = new[] { "png", "jpg", "pdf", "ico" };
            else if (e == "pdf") t = new[] { "png", "jpg" };
            else if (Audio.Contains(e)) t = AudioOut;
            else if (Video.Contains(e)) t = VideoOut;
            else if (e == "txt") t = new[] { "md", "html", "docx", "rtf", "pdf" };
            else if (e == "md") t = new[] { "html", "docx", "txt", "pdf" };
            else if (e == "html") t = new[] { "pdf", "docx", "md", "txt" };
            else if (e == "docx") t = new[] { "pdf", "txt", "html", "md", "rtf" };
            else if (e == "rtf") t = new[] { "docx", "pdf", "txt", "html", "md" };
            else if (e == "csv") t = new[] { "xlsx", "json", "tsv" };
            else if (e == "tsv") t = new[] { "csv", "xlsx", "json" };
            else if (e == "xlsx") t = new[] { "csv", "json" };
            else if (e == "json") t = new[] { "csv", "xml", "yaml" };
            else if (e == "xml") t = new[] { "json" };
            else t = new string[0];

            bool ff = Tools.Ffmpeg != null;
            bool edge = Tools.Edge != null;
            bool media = Audio.Contains(e) || Video.Contains(e) || e == "heic" || e == "avif";
            return t.Where(x => x != e)
                    .Where(x => ff || !(media || NeedFfmpeg.Contains(x) || (e == "gif" && (x == "mp4" || x == "webm"))))
                    .Where(x => edge || !(e == "svg" || (x == "pdf" && (TextFmt.Contains(e)))))
                    .ToArray();
        }

        public static Fam FamilyOf(string e)
        {
            if (e == "pdf") return Fam.Pdf;
            if (e == "svg") return Fam.Vector;
            if (Raster.Contains(e)) return Fam.Image;
            if (Audio.Contains(e)) return Fam.Audio;
            if (Video.Contains(e)) return Fam.Video;
            if (TextFmt.Contains(e)) return Fam.Text;
            if (DataFmt.Contains(e)) return Fam.Data;
            return Fam.None;
        }

        public static string Label(string e)
        {
            string l;
            return Labels.TryGetValue(e, out l) ? l : e.ToUpperInvariant();
        }
    }
}
