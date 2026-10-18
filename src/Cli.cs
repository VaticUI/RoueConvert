#if CLI
using System;
using System.Linq;
using System.Windows.Automation;

namespace RoueConvert
{
    // Console build used for testing: RoueConvert.Cli.exe convert|targets|probe|locate ...
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Tools.Init();
            try
            {
                switch (args.Length > 0 ? args[0] : "")
                {
                    case "tools":
                        Console.WriteLine("ffmpeg: " + (Tools.Ffmpeg ?? "-"));
                        Console.WriteLine("edge:   " + (Tools.Edge ?? "-"));
                        return 0;
                    case "targets":
                        Console.WriteLine(string.Join(" ", Formats.TargetsFor(args[1])));
                        return 0;
                    case "convert":
                        foreach (var o in Converter.Convert(args[1], args[2], null)) Console.WriteLine(o);
                        return 0;
                    case "probe":
                        {
                            var pt = new Native.POINT { X = int.Parse(args[1]), Y = int.Parse(args[2]) };
                            IntPtr h = Native.WindowFromPoint(pt);
                            var kind = ShellResolver.Classify(h);
                            Console.WriteLine("class: " + Native.ClassOf(h) + "  kind: " + kind);
                            Console.WriteLine("name:  " + ShellResolver.ItemNameAt(pt, h));
                            if (kind != ShellResolver.Kind.None) Console.WriteLine("path:  " + ShellResolver.Resolve(pt, h, kind));
                            return 0;
                        }
                    case "locate":
                        {
                            // Screen centre of an item named args[1] in any Explorer window / the desktop.
                            var cond = new AndCondition(new PropertyCondition(AutomationElement.NameProperty, args[1]),
                                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
                            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                            {
                                string cls = top.Current.ClassName;
                                if (cls != "CabinetWClass" && cls != "Progman" && cls != "WorkerW") continue;
                                var it = top.FindFirst(TreeScope.Descendants, cond);
                                if (it == null) continue;
                                var r = it.Current.BoundingRectangle;
                                if (r.IsEmpty) continue;
                                Console.WriteLine((int)(r.Left + Math.Min(r.Width / 2, 40)) + " " + (int)(r.Top + r.Height / 2));
                                return 0;
                            }
                            Console.WriteLine("not found");
                            return 1;
                        }
                }
                Console.WriteLine("usage: tools | targets <file> | convert <file> <ext> | probe <x> <y> | locate <name>");
                return 2;
            }
            catch (Exception ex)
            {
                var e = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                Console.WriteLine("ERREUR: " + e.Message);
                return 1;
            }
        }
    }
}
#endif
