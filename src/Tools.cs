using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RoueConvert
{
    // External helpers: ffmpeg (audio / video / webp) and Edge headless (PDF printing, SVG rendering).
    internal static class Tools
    {
        public static string Ffmpeg;
        public static string Edge;

        public static void Init()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            Ffmpeg = FirstExisting(
                Path.Combine(exeDir, "ffmpeg.exe"),
                FindOnPath("ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe");
            Edge = FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"));
        }

        static string FirstExisting(params string[] paths)
        {
            foreach (var p in paths) if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        static string FindOnPath(string exe)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in path.Split(';'))
            {
                try
                {
                    if (d.Trim().Length == 0) continue;
                    var p = Path.Combine(d.Trim().Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        public static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "RoueConvert", Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // Runs a process hidden; returns the exit code. stderr lines are streamed to onLine; the tail is kept for errors.
        public static int Run(string exe, string args, Action<string> onLine, int timeoutMs, out string tail)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardOutputEncoding = Encoding.UTF8;
            var last = new StringBuilder();
            object gate = new object();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (gate)
                    {
                        if (e.Data.Trim().Length > 0)
                        {
                            last.AppendLine(e.Data);
                            if (last.Length > 4000) last.Remove(0, last.Length - 4000);
                        }
                    }
                    if (onLine != null) onLine(e.Data);
                };
                p.ErrorDataReceived += h;
                p.OutputDataReceived += h;
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    lock (gate) tail = last.ToString();
                    throw new Exception("Délai dépassé (" + Path.GetFileName(exe) + ")");
                }
                p.WaitForExit();
                lock (gate) tail = last.ToString();
                return p.ExitCode;
            }
        }

        public static string LastLine(string tail)
        {
            var lines = tail.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }
    }
}
