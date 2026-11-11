using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TinyHand
{
    /// <summary>
    /// Drawing on the screen: smoothed strokes with an optional arrowhead, fading out a few seconds after
    /// they're finished. Rendered into its own full-screen click-through layered window.
    /// </summary>
    internal sealed class Ink : IDisposable
    {
        private sealed class Stroke
        {
            public readonly List<PointF> Points = new List<PointF>();
            public Color Color;
            public float Width;
            public bool Arrow;
            public DateTime FadeAt = DateTime.MaxValue;
            public float Alpha = 1;
        }

        private readonly LayeredWindow window = new LayeredWindow(true);
        private readonly List<Stroke> strokes = new List<Stroke>();
        private Stroke current;
        private Rectangle screen;
        private Rectangle lastDirty = Rectangle.Empty;
        private bool dirty;
        private const double FadeDuration = 0.6;

        public Color Color = PointerStyle.Purple;

        public bool IsDrawing { get { return current != null; } }
        public int StrokeCount { get { return strokes.Count; } }
        public bool IsAnimating
        {
            get
            {
                foreach (Stroke s in strokes)
                {
                    if (s != current && s.FadeAt != DateTime.MaxValue) return true;
                }
                return false;
            }
        }
        public IntPtr Handle { get { return window.Handle; } }

        public void SetScreen(Rectangle bounds)
        {
            if (bounds == screen)
            {
                return;
            }
            screen = bounds;
            Clear();
            window.EnsureCanvas(bounds.Width, bounds.Height);
            lastDirty = Rectangle.Empty;
        }

        public bool Arrow
        {
            get { return current != null && current.Arrow; }
            set
            {
                if (current != null && current.Arrow != value)
                {
                    current.Arrow = value;
                    dirty = true;
                }
            }
        }

        public void Begin(PointF p, float width)
        {
            current = new Stroke();
            current.Color = Color;
            current.Width = width;
            current.Points.Add(p);
            strokes.Add(current);
            dirty = true;
        }

        public void Extend(PointF p)
        {
            if (current == null) return;
            PointF last = current.Points[current.Points.Count - 1];
            if (Dist(p, last) < 1.5f) return;
            current.Points.Add(p);
            dirty = true;
        }

        public void End()
        {
            if (current == null) return;
            double fade = Prefs.FadeSeconds;
            if (fade > 0)
            {
                current.FadeAt = DateTime.UtcNow.AddSeconds(Math.Max(0.5, fade));
            }
            current = null;
            dirty = true;
        }

        public void Clear()
        {
            current = null;
            if (strokes.Count == 0) return;
            strokes.Clear();
            dirty = true;
            Render();
        }

        /// <summary>Advances fades and redraws if anything changed.</summary>
        public void Render()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = strokes.Count - 1; i >= 0; i--)
            {
                Stroke s = strokes[i];
                if (s.FadeAt == DateTime.MaxValue || now < s.FadeAt) continue;
                double t = (now - s.FadeAt).TotalSeconds / FadeDuration;
                if (t >= 1)
                {
                    strokes.RemoveAt(i);
                }
                else
                {
                    s.Alpha = (float)(1 - t * t); // ease in
                }
                dirty = true;
            }
            if (!dirty) return;
            dirty = false;
            if (strokes.Count == 0)
            {
                // Erase what was drawn, not just hide it: the next stroke only repaints around itself,
                // so anything left on the canvas would come back with it.
                if (window.Canvas != null)
                {
                    window.Graphics.ResetClip();
                    window.Graphics.Clear(Color.Transparent);
                }
                window.Hide();
                lastDirty = Rectangle.Empty;
                return;
            }
            if (window.Canvas == null) return;

            Graphics g = window.Graphics;
            Rectangle area = Rectangle.Empty;
            List<GraphicsPath> paths = new List<GraphicsPath>();
            foreach (Stroke s in strokes)
            {
                GraphicsPath path = BuildPath(s);
                paths.Add(path);
                RectangleF b = path.GetBounds();
                float pad = s.Width * 2 + 8;
                b.Inflate(pad, pad);
                Rectangle rb = Rectangle.Round(b);
                area = area.IsEmpty ? rb : Rectangle.Union(area, rb);
            }
            area.Intersect(new Rectangle(Point.Empty, window.CanvasSize));
            Rectangle clearArea = lastDirty.IsEmpty ? area : Rectangle.Union(area, lastDirty);
            g.SetClip(clearArea);
            g.Clear(Color.Transparent);
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            for (int i = 0; i < strokes.Count; i++)
            {
                Stroke s = strokes[i];
                GraphicsPath path = paths[i];
                int sa = (int)(0.18f * 255 * s.Alpha);
                using (Pen shadow = new Pen(Color.FromArgb(sa, 0, 0, 0), s.Width + 2))
                {
                    shadow.StartCap = shadow.EndCap = LineCap.Round;
                    shadow.LineJoin = LineJoin.Round;
                    g.TranslateTransform(1, 1.5f);
                    g.DrawPath(shadow, path);
                    g.ResetTransform();
                }
                using (Pen pen = new Pen(Color.FromArgb((int)(255 * s.Alpha), s.Color), s.Width))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, path);
                }
                path.Dispose();
            }
            lastDirty = area;
            window.Present(new Rectangle(Point.Empty, window.CanvasSize), screen.Location, 255);
        }

        public void BringToTop()
        {
            window.BringToTop();
        }

        private static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static GraphicsPath BuildPath(Stroke s)
        {
            GraphicsPath path = new GraphicsPath();
            List<PointF> pts = s.Points;
            if (pts.Count == 1)
            {
                PointF p = pts[0];
                path.AddLine(p, new PointF(p.X + 0.01f, p.Y));
                return path;
            }
            // Quadratic smoothing through midpoints (as the original), as cubic Béziers.
            PointF cur = pts[0];
            for (int i = 1; i < pts.Count; i++)
            {
                PointF ctrl = pts[i - 1];
                PointF mid = new PointF((ctrl.X + pts[i].X) / 2, (ctrl.Y + pts[i].Y) / 2);
                PointF c1 = new PointF(cur.X + 2f / 3 * (ctrl.X - cur.X), cur.Y + 2f / 3 * (ctrl.Y - cur.Y));
                PointF c2 = new PointF(mid.X + 2f / 3 * (ctrl.X - mid.X), mid.Y + 2f / 3 * (ctrl.Y - mid.Y));
                path.AddBezier(cur, c1, c2, mid);
                cur = mid;
            }
            path.AddLine(cur, pts[pts.Count - 1]);
            if (s.Arrow)
            {
                AddHead(path, pts, Math.Max(14, s.Width * 3.2f));
            }
            return path;
        }

        // Aims the head along the last stretch of the stroke, so a wobble at the very end doesn't twist it.
        private static void AddHead(GraphicsPath path, List<PointF> pts, float size)
        {
            PointF end = pts[pts.Count - 1];
            PointF back = pts[0];
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                if (Dist(end, pts[i]) >= size * 1.2f)
                {
                    back = pts[i];
                    break;
                }
            }
            float length = Dist(end, back);
            if (length <= 1) return;
            float head = Math.Min(size, length * 0.8f);
            double angle = Math.Atan2(end.Y - back.Y, end.X - back.X);
            double spread = Math.PI / 7;
            foreach (int side in new[] { -1, 1 })
            {
                double a = angle + Math.PI + side * spread;
                path.StartFigure();
                path.AddLine(end, new PointF(end.X + (float)Math.Cos(a) * head, end.Y + (float)Math.Sin(a) * head));
            }
        }

        public void Dispose()
        {
            window.Dispose();
        }
    }
}
