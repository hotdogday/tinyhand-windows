using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace TinyHand
{
    /// <summary>
    /// The small bar at the bottom of the screen while the pointer is on: pinned hands, ink colour, tap
    /// sound, settings and exit. It shows when the pointer comes near it, and can carry a hint above it.
    /// </summary>
    internal sealed class Toolbar : IDisposable
    {
        private enum Kind { Style, Ink, Sound, Settings, Exit, Swatch }

        private sealed class Item
        {
            public Kind Kind;
            public PointerStyle Style;
            public string Swatch;
            public RectangleF Rect; // canvas coordinates
            public string Tip;
        }

        private sealed class Host : LayeredWindow
        {
            public Action<Point> Clicked;

            public Host() : base(false) { }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_LBUTTONDOWN)
                {
                    int lp = m.LParam.ToInt32();
                    Point p = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    if (Clicked != null) Clicked(p);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        private readonly Host host = new Host();
        private readonly List<Item> items = new List<Item>();
        private Screenish screen;
        private float k = 1; // DPI scale
        private RectangleF barRect;
        private RectangleF trayRect;
        private Rectangle canvasOnScreen;
        private PointerStyle selected;
        private bool soundOn = true;
        private bool inkTrayOpen;
        private List<KeyValuePair<string, string>> hint;
        private int hover = -1;
        private float opacity;
        private bool revealed;
        private DateTime lastAnim = DateTime.UtcNow;
        private bool missingThumbs;
        private DateTime lastThumbCheck = DateTime.MinValue;
        private string iconFont;

        public Action<PointerStyle> OnPick;
        public Action OnToggleSound;
        public Action OnExit;
        public Action OnSettings;

        public Toolbar()
        {
            host.Clicked = Click;
            using (InstalledFontCollection fonts = new InstalledFontCollection())
            {
                iconFont = fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            }
        }

        public IntPtr Handle { get { return host.Handle; } }
        public bool Revealed { get { return revealed && opacity > 0.05f; } }

        /// <summary>The bar (and open ink tray) in screen pixels.</summary>
        public Rectangle Frame
        {
            get
            {
                RectangleF r = barRect;
                if (inkTrayOpen) r = RectangleF.Union(r, trayRect);
                return new Rectangle(canvasOnScreen.X + (int)r.X, canvasOnScreen.Y + (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
            }
        }

        public void Select(PointerStyle s)
        {
            selected = s;
            Render();
        }

        public void SetSoundOn(bool on)
        {
            soundOn = on;
            Render();
        }

        public void ShowHint(List<KeyValuePair<string, string>> parts)
        {
            hint = parts;
            Layout();
        }

        public void HideHint()
        {
            if (hint == null) return;
            hint = null;
            Layout();
        }

        public void CloseInkTray()
        {
            if (!inkTrayOpen) return;
            inkTrayOpen = false;
            Layout();
        }

        public void Place(Screenish s)
        {
            screen = s;
            k = s.Scale;
            Layout();
        }

        public void Hide()
        {
            revealed = false;
            opacity = 0;
            inkTrayOpen = false;
            host.Hide();
        }

        public void SetRevealed(bool on)
        {
            if (!on) hover = -1;
            revealed = on;
        }

        /// <summary>Advances the fade and hover; call every tick.</summary>
        public void Tick(Point cursor)
        {
            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - lastAnim).TotalSeconds;
            lastAnim = now;
            float target = revealed ? 1 : 0;
            float before = opacity;
            if (opacity != target)
            {
                float step = dt / 0.14f;
                opacity = target > opacity ? Math.Min(target, opacity + step) : Math.Max(target, opacity - step);
            }
            int h = -1;
            if (revealed)
            {
                PointF local = new PointF(cursor.X - canvasOnScreen.X, cursor.Y - canvasOnScreen.Y);
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Rect.Contains(local)) { h = i; break; }
                }
            }
            bool thumbsReady = false;
            if (missingThumbs && (now - lastThumbCheck).TotalSeconds > 0.4)
            {
                lastThumbCheck = now;
                thumbsReady = items.Where(i => i.Kind == Kind.Style && !i.Style.IsLaser).All(i => HandAsset.CachedThumbnail(i.Style.AssetName) != null);
            }
            if (h != hover || thumbsReady)
            {
                hover = h;
                Render();
            }
            else if (opacity != before)
            {
                Present();
            }
        }

        public void BringToTop()
        {
            host.BringToTop();
        }

        private float D(float v) { return v * k; }

        private void Layout()
        {
            if (screen == null) return;
            items.Clear();
            float cell = D(44), pad = D(8), sep = D(10);
            float x = pad;
            List<PointerStyle> pinned = Prefs.Pinned;
            foreach (PointerStyle s in pinned)
            {
                items.Add(new Item { Kind = Kind.Style, Style = s, Rect = new RectangleF(x, 0, cell, cell), Tip = s.Title });
                x += cell;
            }
            x += sep;
            if (Prefs.ShowInkButton)
            {
                items.Add(new Item { Kind = Kind.Ink, Rect = new RectangleF(x, 0, cell, cell), Tip = "Ink colour" });
                x += cell;
            }
            items.Add(new Item { Kind = Kind.Sound, Rect = new RectangleF(x, 0, cell, cell), Tip = "Tap sound" });
            x += cell;
            items.Add(new Item { Kind = Kind.Settings, Rect = new RectangleF(x, 0, cell, cell), Tip = "Settings" });
            x += cell;
            items.Add(new Item { Kind = Kind.Exit, Rect = new RectangleF(x, 0, cell, cell), Tip = "Turn off (Esc)" });
            x += cell + pad;
            float barW = x, barH = cell + 2 * pad;

            float topArea = D(58);
            float hintW = hint == null ? 0 : MeasureHint() + D(28);
            float trayW = InkChoices.Ids.Length * D(32) + D(16);
            float canvasW = Math.Max(barW, Math.Max(hintW, trayW)) + D(4);
            float canvasH = topArea + barH + D(4);
            barRect = new RectangleF((canvasW - barW) / 2, topArea, barW, barH);
            foreach (Item it in items)
            {
                it.Rect = new RectangleF(it.Rect.X + barRect.X, barRect.Y + pad, it.Rect.Width, it.Rect.Height);
            }
            trayRect = new RectangleF((canvasW - trayW) / 2, topArea - D(48), trayW, D(42));
            if (inkTrayOpen)
            {
                float sx = trayRect.X + D(8);
                foreach (string id in InkChoices.Ids)
                {
                    int idx = Array.IndexOf(InkChoices.Ids, id);
                    items.Add(new Item { Kind = Kind.Swatch, Swatch = id, Rect = new RectangleF(sx, trayRect.Y + D(5), D(32), D(32)), Tip = InkChoices.Titles[idx] });
                    sx += D(32);
                }
            }

            Rectangle work = screen.WorkArea;
            int cw = (int)Math.Ceiling(canvasW), ch = (int)Math.Ceiling(canvasH);
            canvasOnScreen = new Rectangle(work.X + (work.Width - cw) / 2, work.Bottom - ch - (int)D(14), cw, ch);
            host.EnsureCanvas(cw, ch);
            Render();
        }

        private float MeasureHint()
        {
            using (Font bold = new Font("Segoe UI Semibold", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point))
            using (Font regular = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point))
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
            {
                float w = 0;
                foreach (KeyValuePair<string, string> part in hint)
                {
                    w += g.MeasureString(part.Key, bold, PointF.Empty, StringFormat.GenericTypographic).Width + D(5);
                    w += g.MeasureString(part.Value, regular, PointF.Empty, StringFormat.GenericTypographic).Width + D(18);
                }
                return w;
            }
        }

        private void Render()
        {
            if (screen == null || host.Canvas == null) return;
            Graphics g = host.Graphics;
            g.ResetTransform();
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Color panel = Color.FromArgb(235, 28, 27, 31);
            Color border = Color.FromArgb(60, 255, 255, 255);
            FillRound(g, barRect, D(14), panel, border);
            missingThumbs = false;

            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Kind == Kind.Swatch) continue;
                RectangleF r = it.Rect;
                bool hot = i == hover;
                bool sel = it.Kind == Kind.Style && it.Style == selected;
                if (sel)
                {
                    FillRound(g, Inset(r, D(2)), D(10), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(150, 255, 255, 255));
                }
                else if (hot || (it.Kind == Kind.Ink && inkTrayOpen))
                {
                    FillRound(g, Inset(r, D(2)), D(10), Color.FromArgb(38, 255, 255, 255), Color.Empty);
                }
                switch (it.Kind)
                {
                    case Kind.Style:
                        DrawStyle(g, it.Style, Inset(r, D(5)));
                        break;
                    case Kind.Ink:
                        {
                            Color c = InkChoices.Resolve(Prefs.InkChoice, selected ?? PointerStyle.Hands[0]);
                            RectangleF dot = Inset(r, D(13));
                            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, dot);
                            using (Pen p = new Pen(Color.FromArgb(200, 255, 255, 255), D(1.5f))) g.DrawEllipse(p, dot);
                            break;
                        }
                    case Kind.Sound:
                        Glyph(g, soundOn ? "" : "", r, soundOn ? Color.White : Color.FromArgb(150, 255, 255, 255));
                        break;
                    case Kind.Settings:
                        Glyph(g, "", r, Color.White);
                        break;
                    case Kind.Exit:
                        Glyph(g, "", r, Color.White);
                        break;
                }
            }

            // Separator between hands and buttons.
            Item firstButton = items.FirstOrDefault(i => i.Kind != Kind.Style && i.Kind != Kind.Swatch);
            if (firstButton != null)
            {
                float sx = firstButton.Rect.X - D(5);
                using (Pen p = new Pen(Color.FromArgb(50, 255, 255, 255), D(1)))
                {
                    g.DrawLine(p, sx, barRect.Y + D(14), sx, barRect.Bottom - D(14));
                }
            }

            if (inkTrayOpen)
            {
                FillRound(g, trayRect, D(12), panel, border);
                string current = Prefs.InkChoice;
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i];
                    if (it.Kind != Kind.Swatch) continue;
                    RectangleF dot = Inset(it.Rect, D(7));
                    Color c = InkChoices.Resolve(it.Swatch, selected ?? PointerStyle.Hands[0]);
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, dot);
                    if (it.Swatch == "match")
                    {
                        Glyph(g, "", it.Rect, Color.White, 9);
                    }
                    if (it.Swatch == current || i == hover)
                    {
                        using (Pen p = new Pen(Color.FromArgb(it.Swatch == current ? 255 : 140, 255, 255, 255), D(2))) g.DrawEllipse(p, Inset(it.Rect, D(3)));
                    }
                }
            }

            if (hint != null && !inkTrayOpen)
            {
                DrawHint(g);
            }
            else if (hover >= 0 && hover < items.Count && !inkTrayOpen)
            {
                DrawTip(g, items[hover]);
            }
            Present();
        }

        private void Present()
        {
            if (screen == null || host.Canvas == null) return;
            if (opacity <= 0.01f)
            {
                host.Hide();
                return;
            }
            float eased = opacity * opacity * (3 - 2 * opacity);
            host.Present(new Rectangle(Point.Empty, host.CanvasSize), canvasOnScreen.Location, (byte)(255 * eased));
        }

        private void DrawStyle(Graphics g, PointerStyle s, RectangleF r)
        {
            if (s.IsLaser)
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = r.Width * 0.18f;
                using (GraphicsPath glow = new GraphicsPath())
                {
                    glow.AddEllipse(cx - rad * 2.6f, cy - rad * 2.6f, rad * 5.2f, rad * 5.2f);
                    using (PathGradientBrush b = new PathGradientBrush(glow))
                    {
                        b.CenterColor = Color.FromArgb(150, PointerStyle.Red);
                        b.SurroundColors = new[] { Color.FromArgb(0, PointerStyle.Red) };
                        g.FillPath(b, glow);
                    }
                }
                using (SolidBrush b = new SolidBrush(PointerStyle.Red)) g.FillEllipse(b, cx - rad, cy - rad, 2 * rad, 2 * rad);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 255, 219, 209))) g.FillEllipse(b, cx - rad * 0.45f, cy - rad * 0.45f, rad * 0.9f, rad * 0.9f);
                return;
            }
            Bitmap thumb = HandAsset.CachedThumbnail(s.AssetName);
            if (thumb == null)
            {
                missingThumbs = true;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, s.Ink))) g.FillEllipse(b, Inset(r, r.Width * 0.2f));
                return;
            }
            g.DrawImage(thumb, r);
        }

        private void Glyph(Graphics g, string glyph, RectangleF r, Color c, float size = 12)
        {
            using (Font f = new Font(iconFont, size * k, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, f, b, r, sf);
            }
        }

        private void DrawTip(Graphics g, Item it)
        {
            if (string.IsNullOrEmpty(it.Tip)) return;
            using (Font f = new Font("Segoe UI", 9f * k, FontStyle.Regular, GraphicsUnit.Point))
            {
                SizeF sz = g.MeasureString(it.Tip, f);
                float w = sz.Width + D(14), h = sz.Height + D(6);
                float x = Math.Max(0, Math.Min(host.CanvasSize.Width - w, it.Rect.X + it.Rect.Width / 2 - w / 2));
                RectangleF box = new RectangleF(x, barRect.Y - h - D(6), w, h);
                FillRound(g, box, D(7), Color.FromArgb(235, 28, 27, 31), Color.FromArgb(60, 255, 255, 255));
                using (SolidBrush b = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(it.Tip, f, b, box, sf);
                }
            }
        }

        private void DrawHint(Graphics g)
        {
            using (Font bold = new Font("Segoe UI Semibold", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point))
            using (Font regular = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush white = new SolidBrush(Color.White))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
            {
                float w = MeasureHint() + D(28) - D(18);
                float h = D(34);
                RectangleF box = new RectangleF((host.CanvasSize.Width - w) / 2, barRect.Y - h - D(10), w, h);
                FillRound(g, box, h / 2, Color.FromArgb(240, 28, 27, 31), Color.FromArgb(60, 255, 255, 255));
                float x = box.X + D(14);
                StringFormat sf = StringFormat.GenericTypographic;
                float ty = box.Y + (h - bold.GetHeight(g)) / 2;
                for (int i = 0; i < hint.Count; i++)
                {
                    g.DrawString(hint[i].Key, bold, white, x, ty, sf);
                    x += g.MeasureString(hint[i].Key, bold, PointF.Empty, sf).Width + D(5);
                    g.DrawString(hint[i].Value, regular, dim, x, ty, sf);
                    x += g.MeasureString(hint[i].Value, regular, PointF.Empty, sf).Width + D(18);
                }
            }
        }

        private static RectangleF Inset(RectangleF r, float d)
        {
            return new RectangleF(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);
        }

        private static void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color stroke)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (stroke != Color.Empty)
                {
                    using (Pen pen = new Pen(stroke, 1)) g.DrawPath(pen, p);
                }
            }
        }

        private void Click(Point p)
        {
            Item hit = items.FirstOrDefault(i => i.Rect.Contains(p));
            if (hit == null)
            {
                return;
            }
            switch (hit.Kind)
            {
                case Kind.Style:
                    if (OnPick != null) OnPick(hit.Style);
                    break;
                case Kind.Ink:
                    inkTrayOpen = !inkTrayOpen;
                    Layout();
                    break;
                case Kind.Swatch:
                    Prefs.InkChoice = hit.Swatch;
                    inkTrayOpen = false;
                    Layout();
                    break;
                case Kind.Sound:
                    if (OnToggleSound != null) OnToggleSound();
                    break;
                case Kind.Settings:
                    if (OnSettings != null) OnSettings();
                    break;
                case Kind.Exit:
                    if (OnExit != null) OnExit();
                    break;
            }
        }

        public void Relayout()
        {
            Layout();
        }

        public void Dispose()
        {
            host.Dispose();
        }
    }
}
