using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RoueConvert
{
    // Audio / video through ffmpeg, with progress parsed from its "time=" output.
    internal static class Media
    {
        const string Even = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

        static string ArgsFor(string target, bool copy)
        {
            switch (target)
            {
                case "mp3": return "-vn -c:a libmp3lame -q:a 2";
                case "wav": return "-vn -c:a pcm_s16le";
                case "flac": return "-vn -c:a flac";
                case "ogg": return "-vn -c:a libvorbis -q:a 5";
                case "m4a": return "-vn -c:a aac -b:a 192k";
                case "opus": return "-vn -c:a libopus -b:a 128k";
                case "mp4":
                case "mov": return "-vf \"" + Even + "\" -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart";
                case "mkv": return copy ? "-map 0 -c copy" : "-vf \"" + Even + "\" -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k";
                case "webm": return "-vf \"" + Even + "\" -c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -pix_fmt yuv420p -c:a libopus -b:a 128k";
                case "avi": return "-c:v mpeg4 -q:v 3 -c:a libmp3lame -q:a 3";
                case "gif": return "-an -vf \"fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4\" -loop 0";
                case "webp": return "-c:v libwebp -quality 90";
            }
            throw new Exception("Format " + target + " non pris en charge");
        }

        public static void Convert(string src, string dst, string target, Action<double> progress)
        {
            if (Tools.Ffmpeg == null) throw new Exception("ffmpeg introuvable");
            if (target == "mkv")
            {
                try { Run(src, dst, ArgsFor(target, true), progress); return; }
                catch { try { File.Delete(dst); } catch { } }
            }
            Run(src, dst, ArgsFor(target, false), progress);
        }

        public static void Run(string src, string dst, string args, Action<double> progress)
        {
            double duration = 0;
            var rxDur = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            var rxTime = new Regex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            string full = "-hide_banner -nostdin -y -i \"" + src + "\" " + args + " \"" + dst + "\"";
            string tail;
            int code = Tools.Run(Tools.Ffmpeg, full, line =>
            {
                if (progress == null) return;
                var m = rxDur.Match(line);
                if (m.Success && duration == 0) duration = Secs(m);
                m = rxTime.Match(line);
                if (m.Success && duration > 0) progress(Math.Min(1, Secs(m) / duration));
            }, 6 * 60 * 60 * 1000, out tail);
            if (code != 0 || !File.Exists(dst) || new FileInfo(dst).Length == 0)
            {
                try { File.Delete(dst); } catch { }
                throw new Exception(Explain(tail));
            }
        }

        static double Secs(Match m)
        {
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        static string Explain(string tail)
        {
            if (tail.Contains("does not contain any stream") || tail.Contains("matches no streams"))
                return "Aucune piste compatible (pas d'audio ?)";
            string l = Tools.LastLine(tail);
            return "ffmpeg : " + (l.Length > 140 ? l.Substring(0, 140) + "…" : l);
        }
    }
}
