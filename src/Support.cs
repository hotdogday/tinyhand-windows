using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TinyHand
{
    internal static class Log
    {
        private static readonly object gate = new object();

        public static string PathName
        {
            get { return Path.Combine(HandAsset.CacheDir, "tinyhand.log"); }
        }

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(HandAsset.CacheDir);
                    FileInfo fi = new FileInfo(PathName);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        File.Delete(PathName + ".old");
                        File.Move(PathName, PathName + ".old");
                    }
                    File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }


    /// <summary>
    /// Hides the system pointer everywhere by swapping every standard cursor for a blank one, and puts the
    /// user's cursors back with SPI_SETCURSORS. Restore runs on exit, on crash and on the next launch too,
    /// so the pointer can't stay invisible.
    /// </summary>
    internal static class CursorHider
    {
        private static readonly uint[] Ids =
        {
            32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651,
            32671, 32672,
        };

        private static bool hidden;
        public static bool IsHidden { get { return hidden; } }

        public static void Hide()
        {
            if (hidden)
            {
                return;
            }
            hidden = true;
            byte[] and = new byte[32 * 32 / 8];
            byte[] xor = new byte[32 * 32 / 8];
            for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
            IntPtr inst = Native.GetModuleHandle(null);
            foreach (uint id in Ids)
            {
                IntPtr blank = Native.CreateCursor(inst, 0, 0, 32, 32, and, xor);
                if (blank != IntPtr.Zero && !Native.SetSystemCursor(blank, id))
                {
                    Native.DestroyIcon(blank);
                }
            }
        }

        public static void Show()
        {
            if (!hidden)
            {
                return;
            }
            hidden = false;
            Restore();
        }

        public static void Restore()
        {
            Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
    }
}
