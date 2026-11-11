using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace TinyHand
{
    /// <summary>
    /// Low-level mouse and keyboard hooks on their own thread. While active, presses are swallowed (so
    /// nothing underneath gets clicked) and reported to the UI thread, unless the interact key is held,
    /// click-through is latched, or the press lands in a pass-through rectangle (toolbar, taskbar, menus).
    /// </summary>
    internal sealed class InputHook
    {
        public const int MsgDown = Native.WM_APP + 1;
        public const int MsgUp = Native.WM_APP + 2;
        public const int MsgInteractDown = Native.WM_APP + 3;
        public const int MsgInteractUp = Native.WM_APP + 4;
        public const int MsgMove = Native.WM_APP + 5;
        public const int MsgDoubleShift = Native.WM_APP + 6;
        public const int MsgPassThroughDown = Native.WM_APP + 7;

        private readonly IntPtr target;
        private Thread thread;
        private uint threadId;
        private IntPtr mouseHook = IntPtr.Zero;
        private IntPtr keyHook = IntPtr.Zero;
        private readonly Native.LowLevelProc mouseProc;
        private readonly Native.LowLevelProc keyProc;

        private volatile bool active;
        private volatile bool clickThrough;
        private volatile int interactVk;
        private volatile Rectangle[] passThrough = new Rectangle[0];
        private int movePending;

        // Hook thread only.
        private volatile bool interactPress;
        private bool passingThrough;
        private bool swallowing;

        // Double-tap Shift detection (hook thread only).
        private uint inputs;
        private bool shiftDown;
        private uint shiftDownAt;
        private uint shiftDownInputs;
        private uint lastTapAt;
        private uint lastTapInputs;
        private bool hasLastTap;

        public volatile bool DetectDoubleShift = true;

        public InputHook(IntPtr target)
        {
            this.target = target;
            mouseProc = MouseProc;
            keyProc = KeyProc;
        }

        public bool IsInstalled { get { return thread != null && mouseHook != IntPtr.Zero; } }

        public bool Active
        {
            get { return active; }
            set { active = value; }
        }

        public bool ClickThrough
        {
            get { return clickThrough; }
            set { clickThrough = value; }
        }

        /// <summary>Virtual key that lets a click reach the app (0 for none).</summary>
        public int InteractVk
        {
            get { return interactVk; }
            set { interactVk = value; }
        }

        public Rectangle[] PassThrough
        {
            get { return passThrough; }
            set { passThrough = value ?? new Rectangle[0]; }
        }

        public bool InteractPressing { get { return interactPress; } }

        public void MoveHandled()
        {
            Interlocked.Exchange(ref movePending, 0);
        }

        public bool Install()
        {
            if (thread != null)
            {
                return mouseHook != IntPtr.Zero;
            }
            ManualResetEvent ready = new ManualResetEvent(false);
            thread = new Thread(() =>
            {
                threadId = Native.GetCurrentThreadId();
                IntPtr mod = Native.GetModuleHandle(null);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
                keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
                ready.Set();
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                }
                if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
                mouseHook = IntPtr.Zero;
                keyHook = IntPtr.Zero;
            });
            thread.Name = "TinyHand input hook";
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            ready.WaitOne();
            if (mouseHook == IntPtr.Zero)
            {
                Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
            }
            return mouseHook != IntPtr.Zero;
        }

        public void Uninstall()
        {
            active = false;
            if (thread == null)
            {
                return;
            }
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(500);
            thread = null;
            interactPress = false;
            passingThrough = false;
            swallowing = false;
            shiftDown = false;
            hasLastTap = false;
        }

        private void Post(int msg)
        {
            Native.PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool IsDown(int m)
        {
            return m == Native.WM_LBUTTONDOWN || m == Native.WM_RBUTTONDOWN || m == Native.WM_MBUTTONDOWN || m == Native.WM_XBUTTONDOWN;
        }

        private static bool IsUp(int m)
        {
            return m == Native.WM_LBUTTONUP || m == Native.WM_RBUTTONUP || m == Native.WM_MBUTTONUP || m == Native.WM_XBUTTONUP;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }
            int m = wParam.ToInt32();
            if (m == Native.WM_MOUSEMOVE)
            {
                if (Interlocked.CompareExchange(ref movePending, 1, 0) == 0)
                {
                    Post(MsgMove);
                }
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }
            bool down = IsDown(m);
            bool up = IsUp(m);
            if (down)
            {
                inputs++;
            }
            if (!active || !(down || up))
            {
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }

            if (down)
            {
                int vk = interactVk;
                if ((vk != 0 && Native.KeyDown(vk)) || clickThrough)
                {
                    interactPress = true;
                    Post(MsgInteractDown);
                }
            }
            if (interactPress)
            {
                if (up)
                {
                    interactPress = false;
                    Post(MsgInteractUp);
                }
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }

            Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            if (down && !swallowing)
            {
                passingThrough = false;
                Point p = new Point(info.pt.X, info.pt.Y);
                foreach (Rectangle r in passThrough)
                {
                    if (r.Contains(p))
                    {
                        passingThrough = true;
                        break;
                    }
                }
                if (passingThrough)
                {
                    Post(MsgPassThroughDown);
                }
            }
            if (passingThrough)
            {
                if (up)
                {
                    passingThrough = false;
                }
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }

            if (down)
            {
                swallowing = true;
                Post(MsgDown);
                return new IntPtr(1);
            }
            swallowing = false;
            Post(MsgUp);
            return new IntPtr(1);
        }

        private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && active && DetectDoubleShift)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                int m = wParam.ToInt32();
                bool isShift = k.vkCode == Native.VK_LSHIFT || k.vkCode == Native.VK_RSHIFT || k.vkCode == Native.VK_SHIFT;
                bool keyDown = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
                bool keyUp = m == Native.WM_KEYUP || m == Native.WM_SYSKEYUP;
                if (isShift && keyDown)
                {
                    if (!shiftDown)
                    {
                        shiftDown = true;
                        shiftDownAt = k.time;
                        shiftDownInputs = inputs;
                    }
                }
                else if (isShift && keyUp)
                {
                    if (shiftDown)
                    {
                        shiftDown = false;
                        bool clean = unchecked(k.time - shiftDownAt) < 300 && inputs == shiftDownInputs;
                        if (!clean)
                        {
                            hasLastTap = false;
                        }
                        else if (hasLastTap && unchecked(shiftDownAt - lastTapAt) < 350 && lastTapInputs == shiftDownInputs)
                        {
                            hasLastTap = false;
                            Post(MsgDoubleShift);
                        }
                        else
                        {
                            hasLastTap = true;
                            lastTapAt = k.time;
                            lastTapInputs = inputs;
                        }
                    }
                }
                else if (keyDown)
                {
                    inputs++;
                }
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }
    }
}
