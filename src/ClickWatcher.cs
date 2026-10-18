using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RoueConvert
{
    // Global low-level mouse hook running on its own thread so a busy UI never lags the mouse.
    // A Ctrl+left click over an Explorer / desktop file view is swallowed and reported to the UI thread.
    internal sealed class ClickWatcher
    {
        public static readonly IntPtr Marker = new IntPtr(0x52434E56); // "RCNV": our own injected clicks

        public volatile bool Enabled = true;
        public event Action<Native.POINT, IntPtr, ShellResolver.Kind> CtrlClick;

        Thread thread;
        uint threadId;
        IntPtr hook;
        Native.LowLevelProc proc;
        bool swallowUp;

        public void Start()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "MouseHook";
            thread.Start();
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        void Loop()
        {
            threadId = Native.GetCurrentThreadId();
            proc = HookProc;
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Native.UnhookWindowsHookEx(hook);
        }

        static bool CtrlOnly()
        {
            return Native.KeyDown(Native.VK_CONTROL) && !Native.KeyDown(Native.VK_SHIFT) && !Native.KeyDown(Native.VK_MENU)
                && !Native.KeyDown(Native.VK_LWIN) && !Native.KeyDown(Native.VK_RWIN);
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool ours = m.dwExtraInfo == Marker;
                if (msg == Native.WM_LBUTTONDOWN && !ours && Enabled && CtrlOnly())
                {
                    IntPtr h = Native.WindowFromPoint(m.pt);
                    var kind = ShellResolver.Classify(h);
                    if (kind != ShellResolver.Kind.None)
                    {
                        swallowUp = true;
                        var pt = m.pt;
                        App.UI.BeginInvoke(new Action(delegate
                        {
                            var e = CtrlClick;
                            if (e != null) e(pt, h, kind);
                        }));
                        return new IntPtr(1);
                    }
                }
                if (msg == Native.WM_LBUTTONUP && swallowUp && !ours)
                {
                    swallowUp = false;
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // Re-send a Ctrl+click we swallowed but that did not land on a convertible file,
        // so Explorer's normal behaviour (toggling the selection...) still happens.
        public static void Replay()
        {
            bool ctrl = Native.KeyDown(Native.VK_CONTROL);
            var list = new System.Collections.Generic.List<Native.INPUT>();
            if (!ctrl) list.Add(Key(Native.VK_CONTROL, false));
            list.Add(Mouse(Native.MOUSEEVENTF_LEFTDOWN));
            list.Add(Mouse(Native.MOUSEEVENTF_LEFTUP));
            if (!ctrl) list.Add(Key(Native.VK_CONTROL, true));
            var arr = list.ToArray();
            Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static Native.INPUT Mouse(uint flags)
        {
            var i = new Native.INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.dwFlags = flags;
            i.u.mi.dwExtraInfo = Marker;
            return i;
        }

        static Native.INPUT Key(int vk, bool up)
        {
            var i = new Native.INPUT();
            i.type = Native.INPUT_KEYBOARD;
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0;
            i.u.ki.dwExtraInfo = Marker;
            return i;
        }
    }
}
