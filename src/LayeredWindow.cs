using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TinyHand
{
    /// <summary>
    /// A borderless, always-on-top, per-pixel-alpha window. Draw into <see cref="Canvas"/> (premultiplied
    /// ARGB backed by a DIB section) and call <see cref="Present"/> with the part of the canvas to show.
    /// </summary>
    internal class LayeredWindow : NativeWindow, IDisposable
    {
        private IntPtr dib = IntPtr.Zero;
        private IntPtr memDC = IntPtr.Zero;
        private IntPtr oldBitmap = IntPtr.Zero;
        private Bitmap canvas;
        private Graphics graphics;
        private bool visible;
        private readonly bool clickThrough;

        public LayeredWindow(bool clickThrough)
        {
            this.clickThrough = clickThrough;
            CreateParams cp = new CreateParams();
            cp.Caption = "TinyHand";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            if (clickThrough)
            {
                cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            }
            cp.X = 0;
            cp.Y = 0;
            cp.Width = 1;
            cp.Height = 1;
            CreateHandle(cp);
        }

        public Bitmap Canvas { get { return canvas; } }
        public Graphics Graphics { get { return graphics; } }
        public Size CanvasSize { get { return canvas == null ? Size.Empty : canvas.Size; } }
        public bool Visible { get { return visible; } }

        /// <summary>Makes sure the canvas is at least this big. Returns true if it was reallocated (and cleared).</summary>
        public bool EnsureCanvas(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (canvas != null && canvas.Width == width && canvas.Height == height)
            {
                return false;
            }
            FreeCanvas();
            Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
            bi.biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height; // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr bits;
            dib = Native.CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldBitmap = Native.SelectObject(memDC, dib);
            canvas = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            graphics = Graphics.FromImage(canvas);
            graphics.Clear(Color.Transparent);
            return true;
        }

        /// <summary>Shows the <paramref name="source"/> part of the canvas at <paramref name="screen"/> (top-left, pixels).</summary>
        /// <summary>Draw into the canvas but never show the window (render tests).</summary>
        public static bool Offscreen;

        public void Present(Rectangle source, Point screen, byte opacity)
        {
            if (Offscreen)
            {
                graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
                return;
            }
            if (canvas == null || source.Width <= 0 || source.Height <= 0)
            {
                Hide();
                return;
            }
            graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            Native.POINT dst = new Native.POINT(screen.X, screen.Y);
            Native.SIZE size = new Native.SIZE(source.Width, source.Height);
            Native.POINT src = new Native.POINT(source.X, source.Y);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.SourceConstantAlpha = opacity;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
            if (!visible)
            {
                visible = true;
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                BringToTop();
            }
        }

        public void Hide()
        {
            if (!visible)
            {
                return;
            }
            visible = false;
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        public void BringToTop()
        {
            if (visible)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            if (clickThrough && m.Msg == Native.WM_NCHITTEST)
            {
                m.Result = new IntPtr(-1); // HTTRANSPARENT
                return;
            }
            base.WndProc(ref m);
        }

        private void FreeCanvas()
        {
            if (graphics != null)
            {
                graphics.Dispose();
                graphics = null;
            }
            if (canvas != null)
            {
                canvas.Dispose();
                canvas = null;
            }
            if (memDC != IntPtr.Zero)
            {
                Native.SelectObject(memDC, oldBitmap);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero)
            {
                Native.DeleteObject(dib);
                dib = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            FreeCanvas();
            DestroyHandle();
        }
    }
}
