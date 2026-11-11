using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>Colours for the settings window, following the Windows app light/dark setting.</summary>
    internal sealed class Theme
    {
        public readonly bool Dark;
        public Color Bg, Sidebar, Text, Secondary, Card, Separator, Accent, Hover, Selected, ToggleOff, KeyBg, KeyCap, Field, FieldBorder, Menu, Danger;

        public static bool? Override;

        public Theme(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(30, 30, 31);
                Sidebar = Color.FromArgb(44, 44, 46);
                Text = Color.FromArgb(240, 240, 242);
                Secondary = Color.FromArgb(152, 152, 158);
                Card = Color.FromArgb(39, 39, 41);
                Separator = Color.FromArgb(52, 52, 55);
                Accent = Color.FromArgb(10, 132, 255);
                Hover = Color.FromArgb(52, 52, 55);
                Selected = Color.FromArgb(64, 64, 68);
                ToggleOff = Color.FromArgb(78, 78, 82);
                KeyBg = Color.FromArgb(48, 48, 51);
                KeyCap = Color.FromArgb(70, 70, 74);
                Field = Color.FromArgb(58, 58, 62);
                FieldBorder = Color.FromArgb(70, 70, 74);
                Menu = Color.FromArgb(44, 44, 46);
                Danger = Color.FromArgb(255, 105, 97);
            }
            else
            {
                Bg = Color.FromArgb(246, 246, 247);
                Sidebar = Color.FromArgb(232, 232, 235);
                Text = Color.FromArgb(29, 29, 31);
                Secondary = Color.FromArgb(110, 110, 116);
                Card = Color.FromArgb(255, 255, 255);
                Separator = Color.FromArgb(232, 232, 235);
                Accent = Color.FromArgb(0, 122, 255);
                Hover = Color.FromArgb(238, 238, 241);
                Selected = Color.FromArgb(214, 214, 219);
                ToggleOff = Color.FromArgb(214, 214, 219);
                KeyBg = Color.FromArgb(240, 240, 243);
                KeyCap = Color.FromArgb(255, 255, 255);
                Field = Color.FromArgb(255, 255, 255);
                FieldBorder = Color.FromArgb(214, 214, 219);
                Menu = Color.FromArgb(250, 250, 251);
                Danger = Color.FromArgb(215, 45, 35);
            }
        }

        public static Theme Current()
        {
            if (Override.HasValue) return new Theme(Override.Value);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return new Theme(v is int && (int)v == 0);
            }
        }
    }

    /// <summary>Drawing helpers. Sizes are in 96-DPI pixels; the surface scales them.</summary>
    internal static class Ui
    {
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        private static Graphics measurer;
        private static string iconFamily;

        public static Font F(float size, bool semibold = false, bool bold = false)
        {
            string key = size + "|" + semibold + "|" + bold;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static Font Icon(float size)
        {
            if (iconFamily == null)
            {
                using (InstalledFontCollection c = new InstalledFontCollection())
                {
                    iconFamily = c.Families.Any(x => x.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                }
            }
            string key = "icon|" + size;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(iconFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, p);
            }
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float width, bool dashed = false)
        {
            float h = width / 2;
            using (GraphicsPath p = Round(new RectangleF(r.X + h, r.Y + h, r.Width - width, r.Height - width), radius))
            using (Pen pen = new Pen(c, width))
            {
                if (dashed) pen.DashPattern = new[] { 4f, 3f };
                g.DrawPath(pen, p);
            }
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r,
            StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center, bool wrap = false)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = h;
                sf.LineAlignment = v;
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                if (!wrap)
                {
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                }
                g.DrawString(s, f, b, r, sf);
            }
        }

        public static SizeF Measure(string s, Font f, float maxWidth = 0)
        {
            if (measurer == null)
            {
                measurer = Graphics.FromImage(new Bitmap(1, 1));
                measurer.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                SizeF size = maxWidth > 0 ? measurer.MeasureString(s, f, (int)maxWidth, sf) : measurer.MeasureString(s, f, PointF.Empty, sf);
                return new SizeF((float)Math.Ceiling(size.Width), (float)Math.Ceiling(size.Height));
            }
        }

        public static Color A(Color c, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B);
        }

        /// <summary>A one-colour hand (from the glove thumbnail), like the original's hand symbol.</summary>
        public static void HandGlyph(Graphics g, RectangleF r, Color c)
        {
            Bitmap thumb = HandAsset.CachedThumbnail("hand_glove");
            if (thumb == null)
            {
                Text(g, "", Icon(r.Height * 0.8f), c, r, StringAlignment.Center, StringAlignment.Center);
                return;
            }
            using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
            {
                System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, c.A / 255f, 0 },
                    new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 },
                });
                ia.SetColorMatrix(cm);
                g.DrawImage(thumb, new[] { r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) },
                    new RectangleF(0, 0, thumb.Width, thumb.Height), GraphicsUnit.Pixel, ia);
            }
        }

        /// <summary>A hand thumbnail, or the laser dot.</summary>
        public static void Style(Graphics g, PointerStyle s, RectangleF r, int alpha = 255)
        {
            if (s.IsLaser)
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = r.Width * 0.13f;
                using (GraphicsPath glow = new GraphicsPath())
                {
                    glow.AddEllipse(cx - rad * 2.8f, cy - rad * 2.8f, rad * 5.6f, rad * 5.6f);
                    using (PathGradientBrush b = new PathGradientBrush(glow))
                    {
                        b.CenterColor = Color.FromArgb(150 * alpha / 255, PointerStyle.Red);
                        b.SurroundColors = new[] { Color.FromArgb(0, PointerStyle.Red) };
                        g.FillPath(b, glow);
                    }
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, PointerStyle.Red))) g.FillEllipse(b, cx - rad, cy - rad, 2 * rad, 2 * rad);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242 * alpha / 255, 255, 219, 209))) g.FillEllipse(b, cx - rad * 0.45f, cy - rad * 0.45f, rad * 0.9f, rad * 0.9f);
                return;
            }
            Bitmap thumb = HandAsset.CachedThumbnail(s.AssetName);
            if (thumb == null)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70 * alpha / 255, s.Ink))) g.FillEllipse(b, r.X + r.Width * 0.25f, r.Y + r.Height * 0.25f, r.Width * 0.5f, r.Height * 0.5f);
                return;
            }
            if (alpha >= 255)
            {
                g.DrawImage(thumb, r);
                return;
            }
            using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
            {
                System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                cm.Matrix33 = alpha / 255f;
                ia.SetColorMatrix(cm);
                g.DrawImage(thumb, new[] { r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) },
                    new RectangleF(0, 0, thumb.Width, thumb.Height), GraphicsUnit.Pixel, ia);
            }
        }
    }

    /// <summary>Something drawn on a <see cref="Surface"/>, optionally clickable.</summary>
    internal class Widget
    {
        public RectangleF R;
        public string Tip;
        public bool Clickable;
        public bool Hot;
        public bool Pressed;
        public Action<Graphics, Widget> Paint;
        public Action Click;

        public virtual void OnPaint(Graphics g, Surface s)
        {
            if (Paint != null) Paint(g, this);
        }

        public virtual void OnDown(PointF p, Surface s) { }
        public virtual void OnDrag(PointF p, Surface s) { }

        public virtual void OnUp(PointF p, Surface s, bool inside)
        {
            if (inside && Click != null) Click();
        }

        public virtual bool Hit(PointF p)
        {
            return R.Contains(p);
        }
    }

    /// <summary>A custom-drawn panel of widgets, laid out in 96-DPI units and scaled to the screen.</summary>
    internal sealed class Surface : Control
    {
        public readonly List<Widget> Widgets = new List<Widget>();
        public float S = 1;
        public Theme T = new Theme(true);
        private PointF mouse = new PointF(-100, -100);
        public PointF Mouse { get { return mouse; } private set { mouse = value; } }
        public Func<Keys, bool> KeyHandler;
        public Action BackgroundDown;
        private Widget hot;
        private Widget pressed;
        private readonly ToolTip tip = new ToolTip();
        private string lastTip;

        public Surface()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public bool Dragging { get { return pressed != null; } }

        public Widget Add(Widget w)
        {
            Widgets.Add(w);
            return w;
        }

        /// <summary>Call after replacing the widgets, so hover state follows the new ones.</summary>
        public void Rebuilt()
        {
            hot = null;
            UpdateHot();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(S, S);
            foreach (Widget w in Widgets.ToArray())
            {
                GraphicsState state = g.Save();
                try
                {
                    w.OnPaint(g, this);
                }
                catch (Exception ex)
                {
                    Log.Write("settings paint failed: " + ex.Message);
                }
                g.Restore(state);
            }
        }

        private PointF Dip(Point p)
        {
            return new PointF(p.X / S, p.Y / S);
        }

        private void UpdateHot()
        {
            Widget h = null;
            for (int i = Widgets.Count - 1; i >= 0; i--)
            {
                Widget w = Widgets[i];
                if (w.Clickable && w.Hit(Mouse))
                {
                    h = w;
                    break;
                }
            }
            foreach (Widget w in Widgets) w.Hot = w == h;
            hot = h;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            string t = h == null ? null : h.Tip;
            if (t != lastTip)
            {
                lastTip = t;
                tip.SetToolTip(this, t);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Mouse = Dip(e.Location);
            if (pressed != null)
            {
                pressed.OnDrag(Mouse, this);
            }
            else
            {
                UpdateHot();
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (pressed != null) return;
            Mouse = new PointF(-100, -100);
            UpdateHot();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Mouse = Dip(e.Location);
            UpdateHot();
            if (e.Button != MouseButtons.Left) return;
            if (hot == null)
            {
                if (BackgroundDown != null) BackgroundDown();
                return;
            }
            pressed = hot;
            pressed.Pressed = true;
            pressed.OnDown(Mouse, this);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Mouse = Dip(e.Location);
            if (pressed == null) return;
            Widget w = pressed;
            pressed = null;
            w.Pressed = false;
            w.OnUp(Mouse, this, w.Hit(Mouse));
            UpdateHot();
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return KeyHandler != null || base.IsInputKey(keyData);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (KeyHandler != null && KeyHandler(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>Menu colours that follow the settings theme.</summary>
    internal sealed class ThemedMenuColors : ProfessionalColorTable
    {
        private readonly Theme t;

        public ThemedMenuColors(Theme t)
        {
            this.t = t;
            UseSystemColors = false;
        }

        public override Color MenuItemSelected { get { return t.Accent; } }
        public override Color MenuItemBorder { get { return t.Accent; } }
        public override Color MenuBorder { get { return t.FieldBorder; } }
        public override Color ToolStripDropDownBackground { get { return t.Menu; } }
        public override Color ImageMarginGradientBegin { get { return t.Menu; } }
        public override Color ImageMarginGradientMiddle { get { return t.Menu; } }
        public override Color ImageMarginGradientEnd { get { return t.Menu; } }
        public override Color SeparatorDark { get { return t.Separator; } }
        public override Color SeparatorLight { get { return t.Separator; } }
    }

    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Theme t;

        public ThemedMenuRenderer(Theme t) : base(new ThemedMenuColors(t))
        {
            this.t = t;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : t.Text;
            base.OnRenderItemText(e);
        }
    }
}
