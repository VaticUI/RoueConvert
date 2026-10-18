using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("RoueConvert")]
[assembly: System.Reflection.AssemblyProduct("RoueConvert")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace RoueConvert
{
    internal static class App
    {
        public static Dispatcher UI;
        static ClickWatcher watcher;
        static WF.NotifyIcon tray;
        static WheelWindow current;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [STAThread]
        public static int Main(string[] args)
        {
#if CLI
            return Cli.Run(args);
#else
            Tools.Init();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            UI = app.Dispatcher;

            // Debug helper: RoueConvert.exe --wheel "file" shows the wheel at the cursor.
            if (args.Length >= 2 && args[0] == "--wheel")
            {
                Native.POINT pt;
                Native.GetCursorPos(out pt);
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var w = Show(args[1], pt);
                    if (w == null) app.Shutdown(); else w.Closed += delegate { app.Shutdown(); };
                }));
                app.Run();
                return 0;
            }

            bool created;
            var mutex = new Mutex(true, "RoueConvert.SingleInstance", out created);
            if (!created) return 0;

            SetupTray();
            watcher = new ClickWatcher();
            watcher.CtrlClick += OnCtrlClick;
            watcher.Start();
            app.Run();
            watcher.Stop();
            tray.Visible = false;
            tray.Dispose();
            GC.KeepAlive(mutex);
            return 0;
#endif
        }

        static void OnCtrlClick(Native.POINT pt, IntPtr hwnd, ShellResolver.Kind kind)
        {
            string path = null;
            try { path = ShellResolver.Resolve(pt, hwnd, kind); } catch { }
            if (path == null || Show(path, pt) == null) ClickWatcher.Replay();
        }

        static WheelWindow Show(string path, Native.POINT pt)
        {
            var targets = Formats.TargetsFor(path);
            if (targets.Length == 0) return null;
            if (current != null)
            {
                if (current.Working) return current;
                current.Close();
            }
            var w = new WheelWindow(path, targets, pt);
            w.Closed += delegate { if (current == w) current = null; };
            current = w;
            w.Show();
            return w;
        }

        static void SetupTray()
        {
            tray = new WF.NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "RoueConvert — Ctrl + clic sur un fichier";
            var menu = new WF.ContextMenuStrip();
            var enabled = new WF.ToolStripMenuItem("Activé") { Checked = true, CheckOnClick = true };
            enabled.CheckedChanged += delegate { watcher.Enabled = enabled.Checked; };
            var startup = new WF.ToolStripMenuItem("Lancer au démarrage de Windows") { Checked = IsStartup(), CheckOnClick = true };
            startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
            var about = new WF.ToolStripMenuItem("Outils : ffmpeg " + (Tools.Ffmpeg != null ? "✓" : "✗ (audio/vidéo désactivés)") +
                                                 " · Edge " + (Tools.Edge != null ? "✓" : "✗ (PDF de texte désactivé)")) { Enabled = false };
            var quit = new WF.ToolStripMenuItem("Quitter");
            quit.Click += delegate { UI.InvokeShutdown(); };
            menu.Items.Add(enabled);
            menu.Items.Add(startup);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(about);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.ShowBalloonTip(3000, "RoueConvert est actif", "Maintiens Ctrl et clique sur un fichier pour le convertir.", WF.ToolTipIcon.None);
        }

        static bool IsStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("RoueConvert") != null;
        }

        static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("RoueConvert", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue("RoueConvert", false);
            }
        }

        // Small coloured wheel drawn at runtime.
        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var cols = new[] { Color.FromArgb(0x3D, 0xA5, 0xFF), Color.FromArgb(0xFF, 0x5C, 0x5C), Color.FromArgb(0xB0, 0x72, 0xFF),
                                       Color.FromArgb(0xFF, 0x9F, 0x43), Color.FromArgb(0x4C, 0xD6, 0xA0), Color.FromArgb(0xF5, 0xC5, 0x42) };
                    for (int i = 0; i < cols.Length; i++)
                        using (var b = new SolidBrush(cols[i]))
                            g.FillPie(b, 1, 1, 30, 30, -90 + i * 60 + 3, 54);
                    using (var b = new SolidBrush(Color.FromArgb(0x16, 0x18, 0x20)))
                        g.FillEllipse(b, 9, 9, 14, 14);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }
    }
}
