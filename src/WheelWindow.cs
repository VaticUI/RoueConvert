using System;
using System.Collections.Generic;

using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;

namespace RoueConvert
{
    // Transparent overlay over the monitor under the cursor, with the radial menu centred on the click.
    internal sealed class WheelWindow : Window
    {
        const double R = 156, Ri = 74, CR = 64, Pad = 26;
        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        readonly string src;
        readonly string[] targets;
        readonly Native.POINT cursor;
        readonly Canvas root = new Canvas();
        readonly Canvas wheel = new Canvas();
        readonly List<Path> slices = new List<Path>();
        readonly List<FrameworkElement> labels = new List<FrameworkElement>();
        TextBlock centerBig, centerName, centerHint;
        Path ring;
        Ellipse disc;
        bool working, closing;
        int hovered = -1;
        string lastTarget;

        static string Size(long b)
        {
            return b >= 1048576 ? (b / 1048576.0).ToString("0.0") + " Mo" : Math.Max(1, b / 1024) + " Ko";
        }

        public bool Working { get { return working; } }

        public WheelWindow(string path, string[] targets, Native.POINT pt)
        {
            src = path;
            this.targets = targets;
            cursor = pt;
            Title = "RoueConvert";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = -20000; Width = 10; Height = 10;
            Content = root;
            root.Background = Brushes.Transparent;
            FontFamily = UiFont;

            SourceInitialized += delegate { PlaceOnMonitor(); };
            Loaded += delegate
            {
                PlaceOnMonitor();
                Dispatcher.BeginInvoke(new Action(Build), System.Windows.Threading.DispatcherPriority.Loaded);
                ForceActivate();
            };
            PreviewMouseDown += OnMouseDown;
            KeyDown += OnKey;
            Deactivated += delegate { if (!working) FadeClose(); };
        }

        // ---------- placement ----------

        void PlaceOnMonitor()
        {
            var r = Native.MonitorRectAt(cursor);
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Native.SWP_NOACTIVATE);
        }

        void ForceActivate()
        {
            var h = new WindowInteropHelper(this).Handle;
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, IntPtr.Zero), me = Native.GetCurrentThreadId();
            bool attached = fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.SetForegroundWindow(h);
            Activate();
            Focus();
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }

        // ---------- building ----------

        static Color Accent(Fam f)
        {
            switch (f)
            {
                case Fam.Image: return Color.FromRgb(0x3D, 0xA5, 0xFF);
                case Fam.Vector: return Color.FromRgb(0x2E, 0xD3, 0xC6);
                case Fam.Pdf: return Color.FromRgb(0xFF, 0x5C, 0x5C);
                case Fam.Audio: return Color.FromRgb(0xB0, 0x72, 0xFF);
                case Fam.Video: return Color.FromRgb(0xFF, 0x9F, 0x43);
                case Fam.Text: return Color.FromRgb(0x4C, 0xD6, 0xA0);
                case Fam.Data: return Color.FromRgb(0xF5, 0xC5, 0x42);
            }
            return Color.FromRgb(0x9A, 0xA0, 0xB0);
        }

        static string Glyph(Fam f)
        {
            switch (f)
            {
                case Fam.Image: return "";
                case Fam.Vector: return "";
                case Fam.Pdf: return "";
                case Fam.Audio: return "";
                case Fam.Video: return "";
                case Fam.Text: return "";
                case Fam.Data: return "";
            }
            return "";
        }

        static Color TargetColor(string ext)
        {
            if (ext == "gif") return Accent(Fam.Image);
            return Accent(Formats.FamilyOf(ext));
        }

        void Build()
        {
            double size = 2 * (R + Pad);
            double c = size / 2;
            wheel.Width = size; wheel.Height = size;

            // Centre on the click, kept fully on screen.
            Point p = PointFromScreen(new Point(cursor.X, cursor.Y));
            double x = Math.Max(c, Math.Min(ActualWidth - c, p.X));
            double y = Math.Max(c, Math.Min(ActualHeight - c, p.Y));
            Canvas.SetLeft(wheel, x - c);
            Canvas.SetTop(wheel, y - c);
            root.Children.Add(wheel);

            // Soft backdrop disc.
            var back = new Ellipse { Width = 2 * R + 14, Height = 2 * R + 14, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x0C, 0x0D, 0x12)) };
            back.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black };
            Canvas.SetLeft(back, c - R - 7); Canvas.SetTop(back, c - R - 7);
            wheel.Children.Add(back);

            int n = targets.Length;
            double step = 360.0 / n, gap = n > 1 ? 2.2 : 0;
            for (int i = 0; i < n; i++)
            {
                double a0 = -90 - step / 2 + i * step + gap / 2;
                double a1 = a0 + step - gap;
                if (n == 1) { a0 = -90; a1 = 269.9; }
                Color acc = TargetColor(targets[i]);

                var slice = new Path
                {
                    Data = Slice(c, c, Ri, R, a0, a1),
                    Fill = new SolidColorBrush(Color.FromArgb(0xEE, 0x1B, 0x1E, 0x29)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand,
                    Tag = i
                };
                slice.RenderTransform = new ScaleTransform(1, 1, c, c);
                int idx = i;
                slice.MouseEnter += delegate { Hover(idx); };
                slice.MouseLeave += delegate { if (hovered == idx) Hover(-1); };
                slice.MouseLeftButtonUp += delegate { Start(idx); };
                wheel.Children.Add(slice);
                slices.Add(slice);

                // Coloured rim on the outer edge.
                var rim = new Path { Data = Slice(c, c, R - 4, R, a0, a1), Fill = new SolidColorBrush(acc), IsHitTestVisible = false, Opacity = 0.9 };
                wheel.Children.Add(rim);

                double mid = (a0 + a1) / 2 * Math.PI / 180, lr = (Ri + R) / 2 + 2;
                var st = new StackPanel { Width = 96, IsHitTestVisible = false };
                st.Children.Add(new TextBlock { Text = Glyph(Formats.FamilyOf(targets[i])), FontFamily = IconFont, FontSize = 15, Foreground = new SolidColorBrush(acc), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
                st.Children.Add(new TextBlock { Text = Formats.Display(targets[i]), FontSize = Formats.Display(targets[i]).Length > 6 ? 13 : n > 8 ? 13 : 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                st.Children.Add(new TextBlock { Text = Formats.Label(targets[i]), FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center });
                st.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(st, c + lr * Math.Cos(mid) - st.DesiredSize.Width / 2);
                Canvas.SetTop(st, c + lr * Math.Sin(mid) - st.DesiredSize.Height / 2);
                wheel.Children.Add(st);
                labels.Add(st);
            }

            // Centre: source format, file name, status.
            disc = new Ellipse
            {
                Width = 2 * CR, Height = 2 * CR,
                Fill = new SolidColorBrush(Color.FromArgb(0xF4, 0x12, 0x14, 0x1C)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1
            };
            Canvas.SetLeft(disc, c - CR); Canvas.SetTop(disc, c - CR);
            wheel.Children.Add(disc);

            ring = new Path { Stroke = new SolidColorBrush(Accent(Formats.FamilyOf(Formats.Norm(src)))), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0 };
            ring.RenderTransform = new RotateTransform(0, c, c);
            wheel.Children.Add(ring);

            var center = new StackPanel { Width = 2 * CR - 18, IsHitTestVisible = false };
            centerBig = new TextBlock { Text = "." + Formats.Norm(src).ToUpperInvariant(), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Accent(Formats.FamilyOf(Formats.Norm(src)))), HorizontalAlignment = HorizontalAlignment.Center };
            centerName = new TextBlock { Text = IOPath.GetFileName(src), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0), MaxHeight = 42, TextWrapping = TextWrapping.NoWrap };
            centerHint = new TextBlock { Text = "Convertir en…", FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            center.Children.Add(centerBig); center.Children.Add(centerName); center.Children.Add(centerHint);
            center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(center, c - center.DesiredSize.Width / 2);
            Canvas.SetTop(center, c - center.DesiredSize.Height / 2);
            wheel.Children.Add(center);

            // Open animation.
            var sc = new ScaleTransform(0.55, 0.55, c, c);
            wheel.RenderTransform = sc;
            wheel.Opacity = 0;
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
            wheel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var bg = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Background = bg;
            bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x30, 0, 0, 0), TimeSpan.FromMilliseconds(200)));
        }

        static Point P(double cx, double cy, double rad, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a));
        }

        static Geometry Slice(double cx, double cy, double r1, double r2, double a0, double a1)
        {
            bool large = (a1 - a0) > 180;
            var fig = new PathFigure { StartPoint = P(cx, cy, r2, a0), IsClosed = true };
            fig.Segments.Add(new ArcSegment(P(cx, cy, r2, a1), new Size(r2, r2), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(cx, cy, r1, a1), true));
            fig.Segments.Add(new ArcSegment(P(cx, cy, r1, a0), new Size(r1, r1), 0, large, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        static Geometry Arc(double cx, double cy, double rad, double a0, double a1)
        {
            if (a1 - a0 >= 359.9) return new EllipseGeometry(new Point(cx, cy), rad, rad);
            var fig = new PathFigure { StartPoint = P(cx, cy, rad, a0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(P(cx, cy, rad, a1), new Size(rad, rad), 0, (a1 - a0) > 180, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        // ---------- interaction ----------

        void Hover(int i)
        {
            if (working) return;
            hovered = i;
            for (int k = 0; k < slices.Count; k++)
            {
                bool on = k == i;
                Color acc = TargetColor(targets[k]);
                slices[k].Fill = new SolidColorBrush(on ? Color.FromArgb(0xF2, (byte)(acc.R * 0.55), (byte)(acc.G * 0.55), (byte)(acc.B * 0.55)) : Color.FromArgb(0xEE, 0x1B, 0x1E, 0x29));
                var st = (ScaleTransform)slices[k].RenderTransform;
                double to = on ? 1.06 : 1;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(110)));
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(110)));
            }
            centerHint.Text = i >= 0 ? "→ " + Formats.Display(targets[i]) : "Convertir en…";
            centerHint.Foreground = i >= 0 ? new SolidColorBrush(TargetColor(targets[i])) : new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Right) { if (!working) FadeClose(); e.Handled = true; return; }
            if (e.OriginalSource is Path && slices.Contains((Path)e.OriginalSource)) return;
            if (working && centerBig.Text == "✕") { FadeClose(); return; }
            if (!working) { FadeClose(); e.Handled = true; }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !working) { FadeClose(); return; }
            int k = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) k = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) k = e.Key - Key.NumPad1;
            if (k >= 0 && k < targets.Length) Start(k);
            else if (e.Key == Key.Enter && hovered >= 0) Start(hovered);
        }

        void Start(int i)
        {
            if (working) return;
            Hover(i);
            working = true;
            string target = targets[i];
            lastTarget = target;
            Color acc = TargetColor(target);
            for (int k = 0; k < slices.Count; k++)
            {
                double o = k == i ? 1 : 0.28;
                slices[k].Opacity = o; labels[k].Opacity = o;
                slices[k].Cursor = Cursors.Arrow;
            }
            centerBig.Text = Formats.IsCompress(target) ? ".PDF" : Formats.Display(target);
            centerBig.Foreground = new SolidColorBrush(acc);
            centerHint.Text = "Conversion…";
            ring.Stroke = new SolidColorBrush(acc);
            ring.Opacity = 1;
            SetRing(-1);

            var t = new Thread(() =>
            {
                try
                {
                    var outs = Converter.Convert(src, target, p => Dispatcher.BeginInvoke(new Action(() => SetRing(p))));
                    Dispatcher.BeginInvoke(new Action(() => Done(outs)));
                }
                catch (Exception ex)
                {
                    var inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                    Dispatcher.BeginInvoke(new Action(() => Fail(inner.Message)));
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        bool spinning;
        void SetRing(double p)
        {
            double c = wheel.Width / 2, rad = (CR + Ri) / 2 + 1;
            var rot = (RotateTransform)ring.RenderTransform;
            if (p < 0)
            {
                ring.Data = Arc(c, c, rad, -90, 10);
                if (!spinning)
                {
                    spinning = true;
                    rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
                }
                return;
            }
            if (spinning) { spinning = false; rot.BeginAnimation(RotateTransform.AngleProperty, null); rot.Angle = 0; }
            ring.Data = Arc(c, c, rad, -90, -90 + Math.Max(1, Math.Min(360, p * 360)));
            if (working && centerHint.Text != "Terminé") centerHint.Text = Math.Round(p * 100) + " %";
        }

        void Done(List<string> outs)
        {
            SetRing(1);
            var green = Color.FromRgb(0x4C, 0xD6, 0x7A);
            ring.Stroke = new SolidColorBrush(green);
            centerBig.Text = "✓";
            centerBig.Foreground = new SolidColorBrush(green);
            centerName.Text = outs.Count == 1 ? IOPath.GetFileName(outs[0]) : outs.Count + " fichiers";
            centerHint.Text = "Terminé";
            centerHint.Foreground = new SolidColorBrush(green);
            if (outs.Count == 1 && Formats.IsCompress(lastTarget))
            {
                long a = new System.IO.FileInfo(src).Length, b = new System.IO.FileInfo(outs[0]).Length;
                centerHint.Text = "−" + Math.Round(100.0 * (a - b) / a) + " % · " + Size(b);
            }
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            timer.Tick += delegate { timer.Stop(); working = false; FadeClose(); };
            timer.Start();
        }

        void Fail(string msg)
        {
            SetRing(1);
            var red = Color.FromRgb(0xFF, 0x5C, 0x5C);
            ring.Stroke = new SolidColorBrush(red);
            centerBig.Text = "✕";
            centerBig.Foreground = new SolidColorBrush(red);
            centerName.Text = msg;
            centerName.TextWrapping = TextWrapping.Wrap;
            centerName.FontSize = 9.5;
            centerHint.Text = "Échec";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += delegate { timer.Stop(); working = false; FadeClose(); };
            timer.Start();
        }

        public void FadeClose()
        {
            if (closing) return;
            closing = true;
            working = false;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            a.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, a);
        }
    }
}
