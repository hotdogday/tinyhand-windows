using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TinyHand
{
    /// <summary>
    /// A hand-on-a-stick image plus where its parts are, measured from the alpha channel the same way as the
    /// macOS app: fractions of the image height for the fingertip, wrist and end of the rod, and fractions of
    /// the width for the fingertip and rod centres.
    /// </summary>
    internal sealed class HandAsset
    {
        public Bitmap Image;
        public float Aspect;   // width / height
        public float HandTop;
        public float Tip;
        public float Wrist;
        public float RodEnd;
        public float TipX;
        public float RodX;
        public float HandMinX;
        public float HandMaxX;

        private static readonly Dictionary<string, HandAsset> cache = new Dictionary<string, HandAsset>();
        private static readonly List<string> recent = new List<string>();

        public static HandAsset Load(string name)
        {
            HandAsset hit;
            if (cache.TryGetValue(name, out hit))
            {
                Touch(name);
                return hit;
            }
            Bitmap image = LoadImage(name);
            if (image == null)
            {
                return null;
            }
            HandAsset asset = Measure(image);
            if (asset == null)
            {
                image.Dispose();
                return null;
            }
            cache[name] = asset;
            Touch(name);
            return asset;
        }

        private static void Touch(string name)
        {
            recent.Remove(name);
            recent.Add(name);
            while (recent.Count > 2)
            {
                string old = recent[0];
                recent.RemoveAt(0);
                HandAsset gone;
                if (cache.TryGetValue(old, out gone))
                {
                    cache.Remove(old);
                    gone.Image.Dispose();
                }
            }
        }

        public static Stream OpenResource(string fileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream s = asm.GetManifestResourceStream("TinyHand." + fileName);
            if (s != null)
            {
                return s;
            }
            // Loose files next to the exe also work (handy while developing).
            string dir = Path.GetDirectoryName(asm.Location);
            foreach (string sub in new[] { "", "assets", Path.Combine("assets", "hands"), Path.Combine("assets", "sounds"), Path.Combine("assets", "icon") })
            {
                string p = Path.Combine(Path.Combine(dir, sub), fileName);
                if (File.Exists(p))
                {
                    return File.OpenRead(p);
                }
            }
            return null;
        }

        private static Bitmap LoadImage(string name)
        {
            using (Stream s = OpenResource(name + ".png"))
            {
                if (s == null)
                {
                    return null;
                }
                using (Bitmap raw = new Bitmap(s))
                {
                    Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    }
                    return copy;
                }
            }
        }

        private static HandAsset Measure(Bitmap image)
        {
            int w = image.Width;
            int h = image.Height;
            byte[] alpha = new byte[w * h];
            BitmapData data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        alpha[y * w + x] = row[x * 4 + 3];
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }

            int[] counts = new int[h];
            for (int y = 0; y < h; y++)
            {
                int n = 0;
                int r = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[r + x] > 128) n++;
                }
                counts[y] = n;
            }
            Func<int, float> center = y =>
            {
                int r = y * w, lo = -1, hi = -1;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[r + x] > 128)
                    {
                        if (lo < 0) lo = x;
                        hi = x;
                    }
                }
                return lo < 0 ? 0.5f : (lo + hi) / 2f / w;
            };

            int top = -1;
            for (int y = 0; y < h; y++)
            {
                if (counts[y] > 0) { top = y; break; }
            }
            if (top < 0)
            {
                return null;
            }
            int searchEnd = Math.Min(h, top + (int)(0.45 * h));
            int maxW = 0;
            for (int y = top; y < searchEnd; y++) maxW = Math.Max(maxW, counts[y]);
            if (maxW == 0)
            {
                return null;
            }
            int fist = -1;
            for (int y = top; y < h; y++)
            {
                if (counts[y] > 0.6 * maxW) { fist = y; break; }
            }
            if (fist < 0) return null;
            int wrist = -1;
            for (int y = fist; y < h; y++)
            {
                if (counts[y] < 0.35 * maxW) { wrist = y; break; }
            }
            if (wrist < 0) return null;
            int rodW = counts[Math.Min(h - 1, wrist + h / 50)];
            int lastRow = h - 1;
            for (int y = h - 1; y >= 0; y--)
            {
                if (counts[y] > 0) { lastRow = y; break; }
            }
            int from = Math.Min(h - 1, wrist + h / 33);
            int handle = -1;
            for (int y = from; y < h; y++)
            {
                if (counts[y] > 1.8 * rodW) { handle = y; break; }
            }
            int rodEnd = handle >= 0 ? handle : lastRow;

            int minX = w, maxX = 0;
            for (int y = top; y < wrist; y++)
            {
                int r = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[r + x] > 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }

            float fh = h;
            HandAsset a = new HandAsset();
            a.Image = image;
            a.Aspect = w / fh;
            a.HandTop = Math.Max(0, top - 0.004f * fh) / fh;
            a.Tip = top / fh;
            a.Wrist = wrist / fh;
            a.RodEnd = rodEnd / fh;
            a.TipX = center(Math.Min(h - 1, top + h / 200));
            a.RodX = center((wrist + rodEnd) / 2);
            a.HandMinX = minX / (float)w;
            a.HandMaxX = maxX / (float)w;
            return a;
        }

        public Bitmap MakeThumbnail(int px)
        {
            float h = Image.Height, w = Image.Width;
            float handW = (HandMaxX - HandMinX) * w;
            float handH = (Wrist - HandTop) * h;
            float side = Math.Max(handW, handH) * 1.06f;
            float cx = (HandMinX + HandMaxX) / 2 * w;
            float cy = (HandTop + Wrist) / 2 * h;
            RectangleF src = new RectangleF(cx - side / 2, cy - side / 2, side, side);
            Bitmap thumb = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(Image, new RectangleF(0, 0, px, px), src, GraphicsUnit.Pixel);
            }
            return thumb;
        }

        // ---- Scaled copy and its soft shadow, rebuilt when the pointer size or screen changes ----

        public Bitmap Display;
        public Bitmap Shadow;
        public int ShadowPad;
        private int displayHeight;

        public void PrepareDisplay(int neededHeight, float blurPx)
        {
            const int step = 64;
            int needed = (neededHeight + step - 1) / step * step;
            int height = needed >= Image.Height * 9 / 10 ? Image.Height : Math.Max(step, needed);
            if (height == displayHeight && Display != null)
            {
                return;
            }
            displayHeight = height;
            if (Display != null && Display != Image) Display.Dispose();
            if (Shadow != null) Shadow.Dispose();
            if (height == Image.Height)
            {
                Display = Image;
            }
            else
            {
                int width = Math.Max(1, (int)Math.Round(Image.Width * (double)height / Image.Height));
                Display = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(Display))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(Image, new Rectangle(0, 0, width, height));
                }
            }
            MakeShadow(Math.Max(2, (int)Math.Round(blurPx)));
        }

        /// <summary>A black, blurred copy of the display image's alpha, padded so the blur isn't clipped.</summary>
        private void MakeShadow(int radius)
        {
            // Blur at quarter size, then scale back up: cheap and smooth.
            const int down = 4;
            int r = Math.Max(1, radius / down);
            int pad = r * 3 * down;
            int sw = (Display.Width + 2 * pad) / down + 1;
            int sh = (Display.Height + 2 * pad) / down + 1;
            Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(Display, new RectangleF(pad / (float)down, pad / (float)down, Display.Width / (float)down, Display.Height / (float)down));
            }
            byte[] a = new byte[sw * sh];
            BitmapData data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            byte[] row = new byte[sw * 4];
            for (int y = 0; y < sh; y++)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, sw * 4);
                for (int x = 0; x < sw; x++) a[y * sw + x] = row[x * 4 + 3];
            }
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(a, sw, sh, r);
            }
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    byte v = (byte)(a[y * sw + x] * 0.30f);
                    int i = x * 4;
                    row[i] = 0;
                    row[i + 1] = 0;
                    row[i + 2] = 0;
                    row[i + 3] = v;
                }
                Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), sw * 4);
            }
            small.UnlockBits(data);

            Shadow = new Bitmap(Display.Width + 2 * pad, Display.Height + 2 * pad, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(Shadow))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, new Rectangle(0, 0, sw * down, sh * down));
            }
            small.Dispose();
            ShadowPad = pad;
        }

        private static void BoxBlur(byte[] a, int w, int h, int r)
        {
            byte[] tmp = new byte[a.Length];
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int sum = 0, row = y * w;
                for (int x = -r; x <= r; x++) sum += a[row + Math.Max(0, Math.Min(w - 1, x))];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = (byte)(sum / div);
                    sum += a[row + Math.Min(w - 1, x + r + 1)] - a[row + Math.Max(0, x - r)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int y = -r; y <= r; y++) sum += tmp[Math.Max(0, Math.Min(h - 1, y)) * w + x];
                for (int y = 0; y < h; y++)
                {
                    a[y * w + x] = (byte)(sum / div);
                    sum += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x];
                }
            }
        }

        // ---- Thumbnails for the toolbar and the settings gallery, cached on disk ----

        private static readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>();
        private static readonly object thumbLock = new object();
        private const int ThumbPx = 128;

        public static string CacheDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyHand");
            }
        }

        private static string ThumbPath(string name)
        {
            return Path.Combine(Path.Combine(CacheDir, "thumbs"), name + "-v1.png");
        }

        /// <summary>A thumbnail if one is ready; never blocks on decoding a full image.</summary>
        public static Bitmap CachedThumbnail(string name)
        {
            lock (thumbLock)
            {
                Bitmap b;
                return thumbs.TryGetValue(name, out b) ? b : null;
            }
        }

        public static void PreloadThumbnails(IEnumerable<string> names, Action done)
        {
            List<string> list = new List<string>(names);
            Thread t = new Thread(() =>
            {
                foreach (string name in list)
                {
                    try
                    {
                        BuildThumbnail(name);
                    }
                    catch (Exception e)
                    {
                        Log.Write("thumbnail " + name + " failed: " + e.Message);
                    }
                }
                if (done != null)
                {
                    done();
                }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private static void BuildThumbnail(string name)
        {
            lock (thumbLock)
            {
                if (thumbs.ContainsKey(name)) return;
            }
            Bitmap thumb = null;
            string path = ThumbPath(name);
            if (File.Exists(path))
            {
                using (Bitmap disk = new Bitmap(path))
                {
                    thumb = new Bitmap(disk);
                }
            }
            else
            {
                Bitmap image = LoadImage(name);
                if (image == null) return;
                HandAsset a = Measure(image);
                if (a == null)
                {
                    image.Dispose();
                    return;
                }
                thumb = a.MakeThumbnail(ThumbPx);
                image.Dispose();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    thumb.Save(path, ImageFormat.Png);
                }
                catch
                {
                }
            }
            lock (thumbLock)
            {
                if (!thumbs.ContainsKey(name)) thumbs[name] = thumb;
                else thumb.Dispose();
            }
        }
    }
}
