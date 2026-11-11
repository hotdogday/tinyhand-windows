using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TinyHand
{
    /// <summary>A message-only window that hands its messages to a callback.</summary>
    internal sealed class MessageWindow : NativeWindow, IDisposable
    {
        public delegate bool Handler(ref Message m);
        public Handler OnMessage;

        public MessageWindow(string caption)
        {
            CreateParams cp = new CreateParams();
            cp.Caption = caption;
            cp.Parent = new IntPtr(-3); // HWND_MESSAGE
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (OnMessage != null && OnMessage(ref m))
            {
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    /// <summary>System-wide hotkeys (RegisterHotKey).</summary>
    internal static class HotKeys
    {
        private static MessageWindow window;
        private static readonly Dictionary<int, Action> handlers = new Dictionary<int, Action>();
        private static int nextId = 1;

        private static void Ensure()
        {
            if (window != null) return;
            window = new MessageWindow("TinyHand.HotKeys");
            window.OnMessage = (ref Message m) =>
            {
                if (m.Msg != Native.WM_HOTKEY) return false;
                Action a;
                if (handlers.TryGetValue(m.WParam.ToInt32(), out a)) a();
                return true;
            };
        }

        /// <summary>Returns an id, or 0 if the combination is taken.</summary>
        public static int Register(uint modifiers, Keys key, Action action)
        {
            Ensure();
            int id = nextId++;
            if (!Native.RegisterHotKey(window.Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)key))
            {
                return 0;
            }
            handlers[id] = action;
            return id;
        }

        public static void Unregister(int id)
        {
            if (id == 0 || window == null) return;
            Native.UnregisterHotKey(window.Handle, id);
            handlers.Remove(id);
        }
    }
}
