using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace TinyHand
{
    /// <summary>
    /// The hand on a stick. The stick is held from below the bottom of the screen, a little to the right of
    /// the cursor, and swings to keep the fingertip on the cursor. Only the bounding box of the arm is
    /// pushed to its layered window each frame.
    /// </summary>
    internal sealed class ArmView : IDisposable
    {
        private readonly LayeredWindow window = new LayeredWindow(true);
        private PointerStyle style;
        private HandAsset asset;
        private readonly ImageAttributes clampEdges = new ImageAttributes();

        private const float MinRodFrac = 0.05f;
        private const float GripLeanFrac = 0.30f;
        private const float GripDepthFrac = 0.12f;
        private const float GripFollow = 0.08f;
        private const float LiftFrac = 0.06f;

        private Rectangle screen;
        private float handLenFrac;
        private bool showShadow;
        private float pressAmount;
        private float? gripX;
        private float gripTarget;
        private Rectangle lastBox = Rectangle.Empty;
        private string lastPose;

        public bool Held;
        public bool PointerHidden;

        public ArmView()
        {
            clampEdges.SetWrapMode(WrapMode.TileFlipXY);
            ReadPrefs();
        }

        public IntPtr Handle { get { return window.Handle; } }
        public PointerStyle Style { get { return style; } }
        public float InkWidth { get { return Math.Max(3, screen.Height * 0.0055f); } }

        public bool IsSettling
        {
            get
            {
                if (pressAmount > 0.002f) return true;
                return gripX.HasValue && Math.Abs(gripTarget - gripX.Value) > 0.5f;
            }
        }

        public void ReadPrefs()
        {
            handLenFrac = (float)Prefs.PointerSize;
            showShadow = Prefs.ShowShadow;
            lastPose = null;
        }

        public void SetScreen(Rectangle bounds)
        {
            if (bounds == screen) return;
            screen = bounds;
            window.EnsureCanvas(bounds.Width, bounds.Height);
            lastBox = Rectangle.Empty;
            lastPose = null;
            gripX = null;
        }

        public void SetStyle(PointerStyle s)
        {
            style = s;
            asset = s.IsLaser ? null : HandAsset.Load(s.AssetName);
            if (!s.IsLaser && asset == null)
            {
                style = PointerStyle.Laser;
            }
            lastPose = null;
        }

        public void Press()
        {
            pressAmount = 1;
        }

        /// <summary>Moves the arm toward <paramref name="cursor"/> (screen-local pixels, y down).</summary>
        public void Aim(PointF cursor, float dt)
        {
            float W = screen.Width, H = screen.Height;
            if (W <= 0 || H <= 0) return;
            float steps = Math.Max(0, Math.Min(dt * 120f, 30));

            float targetX = Math.Min(Math.Max(cursor.X + GripLeanFrac * W, W * 0.15f), W * 1.05f);
            float follow = 1 - (float)Math.Pow(1 - GripFollow, steps);
            float gx = gripX.HasValue ? gripX.Value + (targetX - gripX.Value) * follow : targetX;
            gripX = gx;
            gripTarget = targetX;
            if (!Held)
            {
                pressAmount *= (float)Math.Pow(1 - 0.18, steps);
                if (pressAmount < 0.001f) pressAmount = 0;
            }

            if (PointerHidden)
            {
                window.Hide();
                lastPose = null;
                return;
            }
            if (style == null || style.IsLaser || asset == null)
            {
                AimLaser(cursor);
                return;
            }

            // Geometry in y-up coordinates (as the original), grip below the bottom edge.
            float cyUp = H - cursor.Y;
            float gyUp = -GripDepthFrac * H;
            float dx = cursor.X - gx;
            float dy = cyUp - gyUp;
            double reach = Math.Sqrt(dx * dx + dy * dy);
            double aimAngle = Math.Atan2(dy, dx);

            HandAsset a = asset;
            float handPts = handLenFrac * H * (1 - 0.06f * pressAmount);
            float s = handPts / (a.Wrist - a.HandTop);
            float widthPts = s * a.Aspect;
            float handTipPts = (a.Wrist - a.Tip) * s;
            float tipOffX = (a.TipX - a.RodX) * widthPts;
            double along = Math.Sqrt(Math.Max(0, reach * reach - tipOffX * tipOffX));
            float rodLen = (float)Math.Max(MinRodFrac * H, along - handTipPts);
            double tipAngle = Math.Atan2(rodLen + handTipPts, tipOffX);
            double rotation = aimAngle - tipAngle;

            float lift = LiftFrac * H * (1 - 0.8f * pressAmount);
            string pose = string.Join(",", new[] { rotation * 100, rodLen, handPts, gx, lift, cursor.X, cursor.Y }
                .Select(v => Math.Round(v * 2).ToString()));
            if (pose == lastPose && window.Visible) return;
            lastPose = pose;

            float restPts = handLenFrac * H / (a.Wrist - a.HandTop);
            a.PrepareDisplay((int)Math.Ceiling(restPts), LiftFrac * H * 0.3f);
            Bitmap img = a.Display;
            float iw = img.Width, ih = img.Height;

            // Dest rects in the arm's local frame: origin at the grip, the arm pointing up (-y).
            float left = -a.RodX * widthPts;
            RectangleF rodDest = new RectangleF(left, -rodLen - 1, widthPts, rodLen + 1);
            RectangleF handDest = new RectangleF(left, -(rodLen + handPts), widthPts, handPts);
            RectangleF rodSrc = new RectangleF(0, a.Wrist * ih, iw, (a.RodEnd - a.Wrist) * ih);
            RectangleF handSrc = new RectangleF(0, a.HandTop * ih, iw, (a.Wrist - a.HandTop) * ih);

            float gripScreenX = gx;
            float gripScreenY = H + GripDepthFrac * H;
            float angleDeg = (float)(-rotation * 180 / Math.PI);
            PointF shadowOffset = new PointF(lift * 0.6f, lift);

            // Bounding box of the arm (and shadow) on screen.
            Matrix m = new Matrix();
            m.Translate(gripScreenX, gripScreenY);
            m.Rotate(angleDeg);
            float pad = a.ShadowPad * (widthPts / iw);
            PointF[] corners =
            {
                new PointF(left - pad, -(rodLen + handPts) - pad), new PointF(left + widthPts + pad, -(rodLen + handPts) - pad),
                new PointF(left - pad, 0), new PointF(left + widthPts + pad, 0),
            };
            m.TransformPoints(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (PointF p in corners)
            {
                minX = Math.Min(minX, Math.Min(p.X, p.X + shadowOffset.X));
                minY = Math.Min(minY, Math.Min(p.Y, p.Y + shadowOffset.Y));
                maxX = Math.Max(maxX, Math.Max(p.X, p.X + shadowOffset.X));
                maxY = Math.Max(maxY, Math.Max(p.Y, p.Y + shadowOffset.Y));
            }
            m.Dispose();
            Rectangle box = Rectangle.FromLTRB((int)Math.Floor(minX) - 2, (int)Math.Floor(minY) - 2, (int)Math.Ceiling(maxX) + 2, (int)Math.Ceiling(maxY) + 2);
            box.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));

            Graphics g = window.Graphics;
            ClearBox(g, box);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.SmoothingMode = SmoothingMode.None;

            if (showShadow && a.Shadow != null)
            {
                float sx = widthPts / iw;              // display px -> screen px, horizontally
                float sp = a.ShadowPad;
                g.ResetTransform();
                g.TranslateTransform(gripScreenX + shadowOffset.X, gripScreenY + shadowOffset.Y);
                g.RotateTransform(angleDeg);
                // Hand shadow with its blur margin on the top and sides.
                float handScaleY = handPts / handSrc.Height;
                RectangleF hs = new RectangleF(0, handSrc.Y, iw + 2 * sp, handSrc.Height + sp);
                RectangleF hd = new RectangleF(left - sp * sx, handDest.Y - sp * handScaleY, widthPts + 2 * sp * sx, handPts + sp * handScaleY);
                DrawPart(g, a.Shadow, hd, hs);
                RectangleF rs = new RectangleF(0, rodSrc.Y + sp, iw + 2 * sp, rodSrc.Height);
                RectangleF rd = new RectangleF(left - sp * sx, rodDest.Y + 1, widthPts + 2 * sp * sx, rodLen);
                DrawPart(g, a.Shadow, rd, rs);
            }

            g.ResetTransform();
            g.TranslateTransform(gripScreenX, gripScreenY);
            g.RotateTransform(angleDeg);
            DrawPart(g, img, rodDest, rodSrc);
            DrawPart(g, img, handDest, handSrc);
            g.ResetTransform();

            lastBox = box;
            window.Present(box, new Point(screen.X + box.X, screen.Y + box.Y), 255);
        }

        private void DrawPart(Graphics g, Bitmap img, RectangleF dest, RectangleF src)
        {
            PointF[] destPts = { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) };
            g.DrawImage(img, destPts, src, GraphicsUnit.Pixel, clampEdges);
        }

        private void ClearBox(Graphics g, Rectangle box)
        {
            Rectangle clear = lastBox.IsEmpty ? box : Rectangle.Union(lastBox, box);
            g.ResetTransform();
            g.SetClip(clear);
            g.Clear(Color.Transparent);
            g.ResetClip();
        }

        private void AimLaser(PointF cursor)
        {
            float H = screen.Height;
            float press = pressAmount;
            float r = Math.Max(4, H * 0.0038f * handLenFrac / 0.13f) * (1 + 0.2f * press);
            string pose = Math.Round(r * 4) + "," + Math.Round(cursor.X * 2) + "," + Math.Round(cursor.Y * 2);
            if (pose == lastPose && window.Visible) return;
            lastPose = pose;

            float glow = r * (3.2f + 1.0f * press);
            Rectangle box = Rectangle.FromLTRB((int)(cursor.X - glow) - 2, (int)(cursor.Y - glow) - 2, (int)(cursor.X + glow) + 3, (int)(cursor.Y + glow) + 3);
            box.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
            Graphics g = window.Graphics;
            ClearBox(g, box);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(cursor.X - glow, cursor.Y - glow, 2 * glow, 2 * glow);
                using (PathGradientBrush b = new PathGradientBrush(glowPath))
                {
                    b.CenterPoint = cursor;
                    b.CenterColor = Color.FromArgb(150, PointerStyle.Red);
                    b.SurroundColors = new[] { Color.FromArgb(0, PointerStyle.Red) };
                    Blend blend = new Blend();
                    blend.Factors = new[] { 0f, 0.15f, 0.55f, 1f };
                    blend.Positions = new[] { 0f, 0.4f, 0.75f, 1f };
                    b.Blend = blend;
                    g.FillPath(b, glowPath);
                }
            }
            using (SolidBrush dot = new SolidBrush(PointerStyle.Red))
            {
                g.FillEllipse(dot, cursor.X - r, cursor.Y - r, 2 * r, 2 * r);
            }
            float core = r * 0.45f;
            using (SolidBrush c = new SolidBrush(Color.FromArgb(242, 255, 219, 209)))
            {
                g.FillEllipse(c, cursor.X - core, cursor.Y - core, 2 * core, 2 * core);
            }
            lastBox = box;
            window.Present(box, new Point(screen.X + box.X, screen.Y + box.Y), 255);
        }

        public void Hide()
        {
            window.Hide();
            lastPose = null;
        }

        public void BringToTop()
        {
            window.BringToTop();
        }

        public void Dispose()
        {
            window.Dispose();
            clampEdges.Dispose();
        }
    }
}
