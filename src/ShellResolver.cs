using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace RoueConvert
{
    // Finds which file sits under the cursor in an Explorer window (tabs included) or on the desktop.
    internal static class ShellResolver
    {
        public enum Kind { None, Explorer, Desktop }

        [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            [PreserveSig] int GetWindow(out IntPtr hwnd);
        }

        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

        // Cheap check, safe to run inside the mouse hook.
        public static Kind Classify(IntPtr h)
        {
            string c = Native.ClassOf(h);
            if (c == "DirectUIHWND")
            {
                if (Native.ClassOf(Native.GetParent(h)) != "SHELLDLL_DefView") return Kind.None;
                string rc = Native.ClassOf(Native.GetAncestor(h, Native.GA_ROOT));
                if (rc == "CabinetWClass" || rc == "ExploreWClass") return Kind.Explorer;
                return Kind.None;
            }
            if (c == "SysListView32")
            {
                IntPtr p = Native.GetParent(h);
                if (Native.ClassOf(p) != "SHELLDLL_DefView") return Kind.None;
                string pc = Native.ClassOf(Native.GetParent(p));
                if (pc == "Progman" || pc == "WorkerW") return Kind.Desktop;
            }
            return Kind.None;
        }

        public static string Resolve(Native.POINT pt, IntPtr hwnd, Kind kind)
        {
            string name = ItemNameAt(pt, hwnd);
            if (string.IsNullOrEmpty(name)) return null;
            if (kind == Kind.Desktop)
            {
                string p = FindInDirs(new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }, name);
                if (p != null) return p;
                return FindViaNamespace(0, name);
            }
            return ResolveExplorer(hwnd, name);
        }

        public static string ItemNameAt(Native.POINT pt, IntPtr hwnd)
        {
            var p = new System.Windows.Point(pt.X, pt.Y);
            // Fast path: hit-test. Transparent overlays (NVIDIA, Discord...) can swallow it, so the
            // result must belong to the clicked window's process.
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                var el = AutomationElement.FromPoint(p);
                if (el != null && el.Current.ProcessId == pid)
                {
                    var walker = TreeWalker.ControlViewWalker;
                    for (int i = 0; el != null && i < 5; i++)
                    {
                        var ct = el.Current.ControlType;
                        if (ct == ControlType.ListItem || ct == ControlType.DataItem) return (el.Current.Name ?? "").Trim();
                        if (ct == ControlType.List || ct == ControlType.Window) break;
                        el = walker.GetParent(el);
                    }
                }
            }
            catch { }

            // Fallback: search the items of the clicked view for the one under the point.
            try
            {
                var view = AutomationElement.FromHandle(hwnd);
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
                foreach (var scope in new[] { TreeScope.Children, TreeScope.Descendants })
                {
                    foreach (AutomationElement it in view.FindAll(scope, cond))
                    {
                        var r = it.Current.BoundingRectangle;
                        if (!r.IsEmpty && r.Contains(p)) return (it.Current.Name ?? "").Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        static string ResolveExplorer(IntPtr hwnd, string name)
        {
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic wins = shell.Windows();
            var cands = new List<object>();
            int n = wins.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    dynamic w = wins.Item(i);
                    if (w == null) continue;
                    if (new IntPtr(Convert.ToInt64(w.HWND)) != root) continue;
                    cands.Add((object)w);
                }
                catch { }
            }
            if (cands.Count == 0) return null;

            // Windows 11 tabs share one top-level HWND: keep the tab whose view contains the clicked window.
            object chosen = null;
            if (cands.Count > 1)
            {
                foreach (var w in cands)
                {
                    IntPtr tab = TabWindow(w);
                    if (tab != IntPtr.Zero && (tab == hwnd || Native.IsChild(tab, hwnd))) { chosen = w; break; }
                }
            }
            if (chosen == null) chosen = cands[0];

            dynamic win = chosen;
            dynamic folder = win.Document.Folder;
            string dir = null;
            try { dir = folder.Self.Path; } catch { }
            if (dir != null && Directory.Exists(dir))
            {
                string p = FindInDirs(new[] { dir }, name);
                if (p != null) return p;
            }
            return FindInFolder(folder, name);
        }

        static IntPtr TabWindow(object w)
        {
            try
            {
                var sp = (IComServiceProvider)w;
                Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
                object o;
                if (sp.QueryService(ref sid, ref iid, out o) != 0 || o == null) return IntPtr.Zero;
                IntPtr h;
                ((IShellBrowser)o).GetWindow(out h);
                Marshal.ReleaseComObject(o);
                return h;
            }
            catch { return IntPtr.Zero; }
        }

        // Explorer may hide known extensions, so the displayed name can be "photo" for "photo.png".
        static string FindInDirs(IEnumerable<string> dirs, string name)
        {
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    string exact = Path.Combine(dir, name);
                    if (File.Exists(exact)) return exact;
                    var matches = Directory.GetFiles(dir, name + ".*")
                        .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count == 0) continue;
                    var good = matches.FirstOrDefault(f => Formats.TargetsFor(f).Length > 0);
                    return good ?? matches[0];
                }
                catch { }
            }
            return null;
        }

        static string FindViaNamespace(int csidl, string name)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                return FindInFolder(shell.NameSpace(csidl), name);
            }
            catch { return null; }
        }

        static string FindInFolder(dynamic folder, string name)
        {
            try
            {
                foreach (dynamic it in folder.Items())
                {
                    if ((string)it.Name != name) continue;
                    if ((bool)it.IsFolder) continue;
                    string p = it.Path;
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }
    }
}
